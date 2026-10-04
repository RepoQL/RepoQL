"""Exercise the Cursor plugin's hooks with literal Cursor payloads and a fake CLI.

Cursor reads one snake_case JSON object from stdout, treats empty stdout as an invalid
response and stderr as failure, and blocks a preToolUse write on invalid JSON — so every
case asserts a single JSON object, empty stderr, and exit 0.

Each hook exists twice: a bash script for macOS and Linux, a PowerShell script for Windows.
The same cases run against both, and the launcher cases run each hooks.json command the
way Cursor itself composes it on the platform under test.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / 'plugins' / 'repoql-cursor'
SCRIPTS = PLUGIN / 'scripts'
WINDOWS = os.name == 'nt'
# Resolved once so cases that strip PATH still launch the script.
BASH = None if WINDOWS else shutil.which('bash')
# On Windows the hooks run under Windows PowerShell, which run-hook.cmd starts by that name.
POWERSHELL = shutil.which('powershell') if WINDOWS else shutil.which('pwsh')
PLUGIN_ROOT_VARIABLE = '${CURSOR_PLUGIN_ROOT}'

FAKE_RQL = '''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps({'args': args, 'cwd': os.getcwd()}) + '\\n')
mode = os.environ.get('HOOK_MODE', 'normal')
if args[:1] == ['query']:
    print('kind\\tline')
    print('import\\tgithub://acme/widgets')
    sys.exit(0)
if args[:1] == ['uplinks']:
    print('Accessible uplinks: acme-gcp')
    sys.exit(0)
if args[:2] == ['concept', 'hints']:
    if mode == 'failure':
        print('host unavailable', file=sys.stderr)
        sys.exit(1)
    if mode == 'malformed':
        print('not hints')
        sys.exit(0)
    terms = [] if mode == 'empty' else [
        {'uri': 'concept:///Rule.md', 'invariant': 'Preserve the contract \\u2014 always.', 'why': 'Callers depend on it.'}]
    if '--json' in args:
        print(json.dumps({'targetUri': args[2], 'concepts': terms}))
    else:
        for term in terms:
            sys.stdout.buffer.write((term['uri'] + '\\t' + term['invariant'] + '\\n  why: ' + term['why'] + '\\n').encode())
    sys.exit(0)
print('unexpected command', file=sys.stderr)
sys.exit(2)
'''

# Stands where jq would be found first, so a hook that reaches for it is caught.
FAKE_JQ = '''#!/bin/sh
: > "$HOME/jq-was-called"
exit 1
'''


class HookFixture(unittest.TestCase):
    """A scratch home holding a workspace and a fake rql that records its calls."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.home = Path(tmp.name).resolve()
        self.workspace = self.home / 'workspace with spaces'
        self.workspace.mkdir()
        # The bash hooks put $HOME/.local/bin first on PATH, so the fakes live there under a scratch HOME.
        self.bin_dir = self.home / '.local' / 'bin'
        self.bin_dir.mkdir(parents=True)
        if WINDOWS:
            (self.bin_dir / 'rql.py').write_text(FAKE_RQL, encoding='utf-8')
            (self.bin_dir / 'rql.ps1').write_text(
                f'& "{sys.executable}" (Join-Path $PSScriptRoot "rql.py") @args\nexit $LASTEXITCODE\n', encoding='utf-8')
        else:
            for name, source in (('rql', FAKE_RQL), ('jq', FAKE_JQ)):
                (self.bin_dir / name).write_text(source, encoding='utf-8')
                (self.bin_dir / name).chmod(0o755)
        self.log = self.home / 'calls.jsonl'
        self.env = {**os.environ, 'HOME': str(self.home), 'HOOK_CALLS': str(self.log),
                    'PATH': f'{self.bin_dir}{os.pathsep}{os.environ["PATH"]}', 'PYTHONUTF8': '1',
                    'REPOQL_NO_BOOTSTRAP': '1', 'CURSOR_PROJECT_DIR': str(self.workspace)}
        # A scratch PATH that still starts the interpreters but finds no rql.
        self.bare_path = os.path.join(os.environ['SystemRoot'], 'System32') if WINDOWS else '/usr/bin:/bin'

    def run_hook(self, argv, stdin, timeout=15, **env):
        result = subprocess.run(argv, input=stdin, text=True, encoding='utf-8', capture_output=True,
                                env={**self.env, **env}, cwd=self.home, timeout=timeout)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, '')
        lines = result.stdout.strip().splitlines()
        self.assertEqual(len(lines), 1, result.stdout)
        self.assertFalse((self.home / 'jq-was-called').exists(), 'the hook reached for jq')
        return json.loads(lines[0])

    def calls(self, command):
        if not self.log.exists():
            return []
        calls = [json.loads(line) for line in self.log.read_text().splitlines()]
        return [c for c in calls if c['args'][:len(command)] == command]

    def assert_ran_in_workspace(self, calls):
        self.assertTrue(calls)
        for call in calls:
            self.assertTrue(os.path.samefile(call['cwd'], self.workspace), call['cwd'])

    def write_payload(self, tool_input=None, **overrides):
        payload = {
            'conversation_id': 'conv-123', 'generation_id': 'gen-1', 'hook_event_name': 'preToolUse',
            'cursor_version': '3.22.7', 'workspace_roots': [str(self.workspace)],
            'tool_name': 'Write', 'tool_use_id': 'tool-1', 'cwd': str(self.workspace),
            'tool_input': tool_input if tool_input is not None
            else {'file_path': 'src/app/[slug]/page.tsx', 'content': 'export {}'},
        }
        payload.update(overrides)
        return payload

    def session_payload(self):
        return {'conversation_id': 'conv-123', 'session_id': 'conv-123', 'hook_event_name': 'sessionStart',
                'cursor_version': '3.22.7', 'workspace_roots': [str(self.workspace)],
                'is_background_agent': False, 'composer_mode': 'agent'}

    def write_concepts_readme(self):
        concepts = self.workspace / '.repoql' / 'concepts'
        concepts.mkdir(parents=True)
        (concepts / 'README.md').write_text('# Concepts\n- Rule — invariant\n', encoding='utf-8')


class HookCases:
    """The behaviour both implementations of each hook must share."""

    def run_script(self, name, payload, **env):
        # ensure_ascii=False sends text as Cursor does: raw UTF-8, not \\u escapes.
        return self.run_hook(self.argv(name), json.dumps(payload, ensure_ascii=False), **env)

    # preToolUse (Write) — concept hints

    def test_write_surfaces_concepts_without_a_permission_decision(self):
        output = self.run_script('concepts-write-hook', self.write_payload())
        self.assertEqual(set(output), {'additional_context'})
        self.assertIn('concept:///Rule.md\tPreserve the contract — always.', output['additional_context'])
        self.assertIn('why: Callers depend on it.', output['additional_context'])
        calls = self.calls(['concept', 'hints'])
        self.assert_ran_in_workspace(calls)
        self.assertEqual([c['args'] for c in calls], [
            ['concept', 'hints', 'src/app/%5Bslug]/page.tsx', '--session', 'conv-123', '--limit', '5', *self.hints_flags]])

    def test_string_encoded_tool_input_and_alternate_path_fields(self):
        for tool_input in (json.dumps({'file_path': 'a.cs'}), {'path': 'a.cs'}, {'target_file': 'a.cs'},
                           {'edits': [{'file_path': 'a.cs', 'old_string': '{"path": "decoy.cs"}'}]}):
            with self.subTest(tool_input=tool_input):
                self.log.unlink(missing_ok=True)
                output = self.run_script('concepts-write-hook', self.write_payload(tool_input))
                self.assertIn('additional_context', output)
                self.assertEqual([c['args'][2] for c in self.calls(['concept', 'hints'])], ['a.cs'])

    def test_paths_survive_json_escaping(self):
        # Windows forbids a quote in a file name, and Windows PowerShell cannot pass one to a program.
        quoted = 'Jo Smith' if WINDOWS else 'Jo "Q" Smith'
        for path in (f'C:\\Users\\{quoted}\\répo\\page 😀.tsx', 'src/tab\\tname.cs'):
            for encode in (lambda value: value, json.dumps):
                with self.subTest(path=path, string_encoded=encode is json.dumps):
                    self.log.unlink(missing_ok=True)
                    self.run_script('concepts-write-hook', self.write_payload(encode({'file_path': path})))
                    self.assertEqual([c['args'][2] for c in self.calls(['concept', 'hints'])], [path])

    def test_a_large_file_body_is_read_inside_the_hook_timeout(self):
        # hooks.json gives the write hook five seconds; the payload carries the whole file.
        body = 'line "quoted" \\ back\\\\slash\n\t"file_path": "decoy.cs",\n' * 40000
        for tool_input in ({'content': body, 'file_path': 'a.cs'}, json.dumps({'content': body, 'file_path': 'a.cs'})):
            with self.subTest(string_encoded=isinstance(tool_input, str)):
                self.log.unlink(missing_ok=True)
                output = self.run_hook(self.argv('concepts-write-hook'),
                                       json.dumps(self.write_payload(tool_input)), timeout=5)
                self.assertIn('additional_context', output)
                self.assertEqual([c['args'][2] for c in self.calls(['concept', 'hints'])], ['a.cs'])

    def test_every_failure_lets_the_write_through(self):
        cases = {
            'cli failure': ({'HOOK_MODE': 'failure'}, self.write_payload()),
            'unrecognised cli output': ({'HOOK_MODE': 'malformed'}, self.write_payload()),
            'no concepts': ({'HOOK_MODE': 'empty'}, self.write_payload()),
            'no path in input': ({}, self.write_payload({'content': 'x'})),
            'no session': ({}, self.write_payload(conversation_id=None)),
            'rql missing': ({'PATH': self.bare_path, 'HOME': str(self.workspace)}, self.write_payload()),
        }
        for name, (env, payload) in cases.items():
            with self.subTest(name):
                self.assertEqual(self.run_script('concepts-write-hook', payload, **env), {})
        self.assertEqual(self.run_hook(self.argv('concepts-write-hook'), 'not json'), {})

    # sessionStart — orientation, PATH export, concepts index

    def test_session_start_orients(self):
        self.write_concepts_readme()
        output = self.run_script('session-start', self.session_payload())
        context = output['additional_context']
        self.assertIn('# RepoQL: Repository Orientation', context)
        self.assertIn('github://acme/widgets', context)
        self.assertIn('Accessible uplinks: acme-gcp', context)
        self.assertIn('## Repository Concepts Index (.repoql/concepts/', context)
        self.assertIn('- Rule — invariant', context)
        self.assert_ran_in_workspace(self.calls(['query']))
        self.assertEqual(set(output), {'additional_context', *self.session_keys})

    def test_session_start_defers_the_index_to_the_generated_rule(self):
        self.write_concepts_readme()
        rules = self.workspace / '.cursor' / 'rules'
        rules.mkdir(parents=True)
        (rules / 'repoql-concepts.g.mdc').write_text('---\nalwaysApply: true\n---\n')
        output = self.run_script('session-start', self.session_payload())
        self.assertNotIn('Repository Concepts Index', output['additional_context'])

    def test_session_start_without_rql_still_answers_json(self):
        # REPOQL_NO_BOOTSTRAP=1 (from setUp) keeps the bootstrap from downloading anything.
        no_rql = {'PATH': self.bare_path, 'HOME': str(self.workspace)}
        self.assertEqual(set(self.run_script('session-start', self.session_payload(), **no_rql)), self.session_keys)


@unittest.skipUnless(BASH, 'the bash hooks serve macOS and Linux')
class BashHookTests(HookCases, HookFixture):
    hints_flags = []
    session_keys = {'env'}

    def argv(self, name):
        return [BASH, str(SCRIPTS / f'{name}.sh')]

    def test_session_start_exports_a_path_that_finds_rql(self):
        output = self.run_script('session-start', self.session_payload())
        self.assertTrue(output['env']['PATH'].startswith(f'{self.bin_dir}:'))

    def test_session_start_without_its_tools_still_answers_json(self):
        self.assertEqual(self.run_script('session-start', self.session_payload(), PATH='/nonexistent'), {})


@unittest.skipUnless(POWERSHELL, 'the PowerShell hooks need PowerShell')
class PowerShellHookTests(HookCases, HookFixture):
    hints_flags = ['--json']
    session_keys = set()

    def argv(self, name):
        return [POWERSHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                '-File', str(SCRIPTS / f'{name}.ps1')]


class LauncherTests(HookFixture):
    """Run each hooks.json command as Cursor composes it, from an install path with a space."""

    def setUp(self):
        super().setUp()
        self.plugin_root = self.home / 'plugin root' / 'repoql-cursor'
        shutil.copytree(PLUGIN / 'scripts', self.plugin_root / 'scripts')
        hooks = json.loads((PLUGIN / 'hooks' / 'hooks.json').read_text())['hooks']
        self.commands = {event: entries[0]['command'] for event, entries in hooks.items()}
        self.payloads = {'sessionStart': self.session_payload(), 'preToolUse': self.write_payload()}
        self.env['CURSOR_PLUGIN_ROOT'] = str(self.plugin_root)

    def assert_every_hook_answers(self, launch):
        for event, command in self.commands.items():
            with self.subTest(event=event):
                output = launch(command, json.dumps(self.payloads[event]))
                self.assertIn('RepoQL' if event == 'sessionStart' else 'concept:///Rule.md', output['additional_context'])

    @unittest.skipIf(WINDOWS, 'POSIX shells')
    def test_cursor_runs_each_hook_through_a_posix_shell(self):
        # Cursor appends the payload as a here-document and hands the line to the user's shell.
        for shell in filter(None, (shutil.which(name) for name in ('sh', 'bash', 'zsh', 'dash'))):
            with self.subTest(shell=shell):
                self.assert_every_hook_answers(lambda command, payload: self.run_hook(
                    [shell, '-c', f"{command} <<'CURSOR_HOOK_EOF'\n{payload}\nCURSOR_HOOK_EOF"], None))

    @unittest.skipUnless(WINDOWS, 'Windows PowerShell')
    def test_cursor_runs_each_hook_through_powershell(self):
        # Cursor writes the payload to a file, substitutes the plugin root, puts the call
        # operator ahead of a quoted path, and pipes the payload in from PowerShell.
        payload_file = self.home / 'payload.json'

        def launch(command, payload):
            payload_file.write_text(payload, encoding='utf-8')
            command = command.replace(PLUGIN_ROOT_VARIABLE, str(self.plugin_root))
            script = ("$OutputEncoding = [System.Text.Encoding]::UTF8; "
                      f"Get-Content -LiteralPath '{payload_file}' -Raw | & {{ $input | & {command} }}")
            return self.run_hook([POWERSHELL, '-NoProfile', '-NonInteractive', '-Command', script], None)

        self.assert_every_hook_answers(launch)


class WiringTests(unittest.TestCase):
    def test_every_hook_command_starts_the_launcher_on_a_hook_with_both_implementations(self):
        hooks = json.loads((PLUGIN / 'hooks' / 'hooks.json').read_text())
        self.assertEqual(hooks['version'], 1)
        launcher = f'"{PLUGIN_ROOT_VARIABLE}/scripts/run-hook.cmd" '
        for event, entries in hooks['hooks'].items():
            for entry in entries:
                with self.subTest(event=event, command=entry['command']):
                    # Quoted, so an install path with a space survives both shells.
                    self.assertTrue(entry['command'].startswith(launcher))
                    name = entry['command'][len(launcher):]
                    self.assertTrue((SCRIPTS / f'{name}.sh').is_file())
                    self.assertTrue((SCRIPTS / f'{name}.ps1').is_file())
        if not WINDOWS:
            self.assertTrue(os.access(SCRIPTS / 'run-hook.cmd', os.X_OK))

    def test_powershell_scripts_are_ascii(self):
        # Windows PowerShell reads a script without a byte-order mark as ANSI.
        for script in SCRIPTS.glob('*.ps1'):
            with self.subTest(script=script.name):
                script.read_bytes().decode('ascii')

    def test_skill_names_match_their_folders(self):
        for skill in (PLUGIN / 'skills').iterdir():
            front = (skill / 'SKILL.md').read_text(encoding='utf-8').split('---')[1]
            names = [line.split(':', 1)[1].strip().strip('"') for line in front.splitlines() if line.startswith('name:')]
            self.assertEqual(names, [skill.name])


if __name__ == '__main__':
    unittest.main()
