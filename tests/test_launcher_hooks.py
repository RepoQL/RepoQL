"""Start every hooks.json command the way its harness does, from an install path with a space.

Claude Code hands a hook command to a shell: sh on macOS and Linux, and on Windows Git Bash or, without it,
PowerShell. It substitutes the plugin root into the command and exports it. Codex substitutes it too, and
runs the command with the user's shell, or with cmd.exe and command_windows on Windows.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

from hook_support import ROOT, WINDOWS, context_of, hook_env, install_fake, run

PLUGINS = ROOT / 'plugins'
GIT_BASH = Path(os.environ.get('ProgramFiles', 'C:\\Program Files')) / 'Git' / 'bin' / 'bash.exe'

FAKE_RQL = '''
import json, os, sys
args = sys.argv[1:]
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps(args) + '\\n')
if args[:1] == ['query']:
    print('kind\\tline\\nimport\\tgithub://acme/widgets')
elif args[:1] == ['uplinks']:
    print('Accessible uplinks: acme-gcp')
elif args[:2] == ['concept', 'hints']:
    if '--json' in args:
        print(json.dumps({'concepts': [{'uri': 'concept:///Rule.md', 'invariant': 'Preserve the contract.'}]}))
    else:
        print('concept:///Rule.md\\tPreserve the contract.')
elif args[:2] == ['vocabulary', 'hints']:
    print('DirtySweep [engine] - The dirty-file actor.')
elif args[:2] == ['worktree', 'check']:
    print('RepoQL worktree notice.')
'''


class LauncherFixture(unittest.TestCase):
    harness = None

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.home = Path(tmp.name).resolve()
        self.workspace = self.home / 'workspace with spaces'
        self.workspace.mkdir()
        self.plugin_root = self.home / 'plugin root' / self.harness
        shutil.copytree(PLUGINS / self.harness / 'scripts', self.plugin_root / 'scripts')
        bin_dir = self.home / '.local' / 'bin'
        install_fake(bin_dir, 'rql', FAKE_RQL)
        self.log = self.home / 'calls.jsonl'
        self.env = hook_env(bin_dir, HOME=str(self.home), HOOK_CALLS=str(self.log),
                            CLAUDE_PROJECT_DIR=str(self.workspace))
        base = {'session_id': 'session-123', 'cwd': str(self.workspace)}
        read = {'tool_name': 'mcp__repoql__read', 'tool_input': {'uriGlob': 'file:///src/**'},
                'tool_response': {'content': [{'type': 'text', 'text': 'DirtySweep works.'}]}}
        self.payloads = {
            'session-start': base,
            'concepts-write-hook': {**base, 'tool_name': 'Write', 'tool_input': {
                'file_path': 'a.cs', 'patch': '*** Update File: a.cs'}},
            'vocabulary-read-hook': {**base, **read},
            'worktree-check-hook': {**base, **read},
            'worktree-track-hook': {**base, 'tool_name': 'Edit', 'tool_input': {'file_path': str(self.workspace / 'a.cs')}},
        }
        self.expected = {'session-start': ('SessionStart', '# RepoQL: Repository Orientation'),
                         'concepts-write-hook': ('PreToolUse', 'concept:///Rule.md\tPreserve the contract.'),
                         'vocabulary-read-hook': ('PostToolUse', 'DirtySweep [engine]'),
                         'worktree-check-hook': ('PostToolUse', 'RepoQL worktree notice.')}

    def hooks(self, key):
        config = json.loads((PLUGINS / self.harness / 'hooks' / 'hooks.json').read_text())
        for groups in config['hooks'].values():
            for group in groups:
                for hook in group['hooks']:
                    yield hook[key if key in hook else 'command']

    def hook_name(self, command):
        return next(name for name in self.payloads if f'{name}.' in command)

    def assert_answers(self, command, launch):
        name = self.hook_name(command)
        with self.subTest(hook=name):
            self.log.unlink(missing_ok=True)
            result = launch(command, self.payloads[name])
            self.assertEqual((result.returncode, result.stderr), (0, ''), result.stdout)
            if name in self.expected:
                event, text = self.expected[name]
                self.assertIn(text, context_of(result, event))
            else:
                # Tracking answers nothing; the bash hook makes its call detached.
                deadline = time.monotonic() + 3
                while not self.log.exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertIn('track', self.log.read_text())


class ClaudeCodeLauncherTests(LauncherFixture):
    harness = 'repoql'

    def setUp(self):
        super().setUp()
        self.env['CLAUDE_PLUGIN_ROOT'] = str(self.plugin_root)

    def shells(self):
        if not WINDOWS:
            return [[path, '-c'] for path in filter(None, map(shutil.which, ('sh', 'bash', 'zsh', 'dash')))]
        powershells = [[path, '-NoProfile', '-NonInteractive', '-Command']
                       for path in filter(None, map(shutil.which, ('powershell', 'pwsh')))]
        return powershells + ([[str(GIT_BASH), '-c']] if GIT_BASH.exists() else [])

    def test_every_hook_answers_under_every_shell_claude_code_uses(self):
        shells = self.shells()
        # A missing shell must fail here, not pass by being skipped: CI has all of them.
        self.assertGreaterEqual(len(shells), 3 if WINDOWS or os.environ.get('CI') else 1, shells)
        for shell in shells:
            for command in self.hooks('command'):
                with self.subTest(shell=shell[0]):
                    # Claude Code writes the placeholder with forward slashes on every platform.
                    command = command.replace('${CLAUDE_PLUGIN_ROOT}', self.plugin_root.as_posix())
                    self.assert_answers(command, lambda line, payload: run([*shell, line], payload, self.env, self.workspace, 30))


class CodexLauncherTests(LauncherFixture):
    harness = 'repoql-codex'

    def test_every_hook_answers_the_way_codex_starts_it(self):
        key = 'command_windows' if WINDOWS else 'command'
        for command in self.hooks(key):
            command = command.replace('${PLUGIN_ROOT}', str(self.plugin_root))
            if WINDOWS:
                # Codex passes cmd.exe the line inside one pair of quotes, unescaped.
                launch = lambda line, payload: subprocess.run(
                    f'cmd.exe /C "{line}"', input=json.dumps(payload), text=True, encoding='utf-8',
                    capture_output=True, env=self.env, cwd=self.workspace, timeout=30)
            else:
                launch = lambda line, payload: run(['/bin/sh', '-lc', line], payload, self.env, self.workspace, 30)
            self.assert_answers(command, launch)

    def test_every_hook_has_a_windows_command(self):
        config = json.loads((PLUGINS / self.harness / 'hooks' / 'hooks.json').read_text())
        for groups in config['hooks'].values():
            for group in groups:
                for hook in group['hooks']:
                    name = self.hook_name(hook['command'])
                    self.assertIn(f'{name}.sh', hook['command'])
                    self.assertIn(f'{name}.ps1', hook['command_windows'])


class SharedScriptTests(unittest.TestCase):
    HARNESSES = ('repoql', 'repoql-codex', 'repoql-cursor')

    def test_shared_scripts_stay_identical_across_plugins(self):
        for name in ('json.sh', 'json-leaves.awk', 'hook-io.ps1', 'bootstrap-rql.ps1'):
            with self.subTest(script=name):
                copies = {(PLUGINS / harness / 'scripts' / name).read_bytes() for harness in self.HARNESSES}
                self.assertEqual(len(copies), 1)

    def test_every_hook_has_a_bash_and_a_powershell_implementation(self):
        for harness in self.HARNESSES:
            folder = PLUGINS / harness / 'scripts'
            hooks = {path.stem for path in folder.glob('*.sh')} - {'json'}
            twins = {path.stem for path in folder.glob('*.ps1')} - {'hook-io'}
            self.assertEqual(hooks, twins, harness)

    def test_no_hook_reaches_for_jq(self):
        for harness in self.HARNESSES:
            for path in [*(PLUGINS / harness / 'scripts').iterdir(), PLUGINS / harness / 'hooks' / 'hooks.json']:
                with self.subTest(path=str(path.relative_to(PLUGINS))):
                    self.assertNotRegex(path.read_text(encoding='utf-8').replace('without jq', ''), r'\bjq\b')

    def test_powershell_scripts_are_ascii(self):
        # Windows PowerShell reads a script without a byte-order mark as ANSI.
        for harness in self.HARNESSES:
            for script in (PLUGINS / harness / 'scripts').glob('*.ps1'):
                with self.subTest(script=str(script.relative_to(PLUGINS))):
                    script.read_bytes().decode('ascii')


if __name__ == '__main__':
    unittest.main()
