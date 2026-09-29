"""Exercise the Cursor plugin's hooks with literal Cursor payloads and a fake CLI.

Cursor reads one snake_case JSON object from stdout, treats empty stdout as an invalid
response and stderr as failure, and blocks a preToolUse write on invalid JSON — so every
case asserts a single JSON object, empty stderr, and exit 0.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / 'plugins' / 'repoql-cursor'
# Resolved once so cases that strip PATH still launch the script.
BASH = shutil.which('bash')

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
        print('not json')
        sys.exit(0)
    terms = [] if mode == 'empty' else [
        {'uri': 'concept:///Rule.md', 'invariant': 'Preserve the contract.', 'why': 'Callers depend on it.'}]
    print(json.dumps({'targetUri': args[2], 'concepts': terms}))
    sys.exit(0)
print('unexpected command', file=sys.stderr)
sys.exit(2)
'''


class CursorHooksTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.home = Path(tmp.name).resolve()
        self.workspace = self.home / 'workspace with spaces'
        self.workspace.mkdir()
        # The hooks put $HOME/.local/bin first on PATH, so the fake lives there under a scratch HOME.
        bin_dir = self.home / '.local' / 'bin'
        bin_dir.mkdir(parents=True)
        (bin_dir / 'rql').write_text(FAKE_RQL)
        (bin_dir / 'rql').chmod(0o755)
        self.log = self.home / 'calls.jsonl'
        self.env = {**os.environ, 'HOME': str(self.home), 'HOOK_CALLS': str(self.log),
                    'REPOQL_NO_BOOTSTRAP': '1', 'CURSOR_PROJECT_DIR': str(self.workspace)}

    def run_script(self, script, payload, **env):
        result = subprocess.run([BASH, str(PLUGIN / 'scripts' / script)], input=json.dumps(payload),
                                text=True, capture_output=True, env={**self.env, **env}, cwd=self.home, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, '')
        lines = result.stdout.strip().splitlines()
        self.assertEqual(len(lines), 1, result.stdout)
        return json.loads(lines[0])

    def calls(self, command):
        if not self.log.exists():
            return []
        calls = [json.loads(line) for line in self.log.read_text().splitlines()]
        return [c for c in calls if c['args'][:len(command)] == command]

    # preToolUse (Write) — concept hints

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

    def test_write_surfaces_concepts_without_a_permission_decision(self):
        output = self.run_script('concepts-write-hook.sh', self.write_payload())
        self.assertEqual(set(output), {'additional_context'})
        self.assertIn('concept:///Rule.md\tPreserve the contract.', output['additional_context'])
        self.assertIn('why: Callers depend on it.', output['additional_context'])
        self.assertEqual(self.calls(['concept', 'hints']), [{'cwd': str(self.workspace), 'args':
            ['concept', 'hints', 'src/app/%5Bslug]/page.tsx', '--session', 'conv-123', '--limit', '5', '--json']}])

    def test_string_encoded_tool_input_and_alternate_path_fields(self):
        for tool_input in (json.dumps({'file_path': 'a.cs'}), {'path': 'a.cs'}, {'target_file': 'a.cs'}):
            with self.subTest(tool_input=tool_input):
                output = self.run_script('concepts-write-hook.sh', self.write_payload(tool_input))
                self.assertIn('additional_context', output)

    def test_every_failure_lets_the_write_through(self):
        cases = {
            'cli failure': ({'HOOK_MODE': 'failure'}, self.write_payload()),
            'malformed cli output': ({'HOOK_MODE': 'malformed'}, self.write_payload()),
            'no concepts': ({'HOOK_MODE': 'empty'}, self.write_payload()),
            'no path in input': ({}, self.write_payload({'content': 'x'})),
            'no session': ({}, self.write_payload(conversation_id=None)),
            'rql missing': ({'PATH': '/usr/bin:/bin', 'HOME': str(self.workspace)}, self.write_payload()),
        }
        for name, (env, payload) in cases.items():
            with self.subTest(name):
                self.assertEqual(self.run_script('concepts-write-hook.sh', payload, **env), {})

    # sessionStart — orientation, PATH export, concepts index

    def session_payload(self):
        return {'conversation_id': 'conv-123', 'session_id': 'conv-123', 'hook_event_name': 'sessionStart',
                'cursor_version': '3.22.7', 'workspace_roots': [str(self.workspace)],
                'is_background_agent': False, 'composer_mode': 'agent'}

    def write_concepts_readme(self):
        concepts = self.workspace / '.repoql' / 'concepts'
        concepts.mkdir(parents=True)
        (concepts / 'README.md').write_text('# Concepts\n- Rule — invariant\n')

    def test_session_start_orients_and_exports_path(self):
        self.write_concepts_readme()
        output = self.run_script('session-start.sh', self.session_payload())
        self.assertTrue(output['env']['PATH'].startswith(f'{self.home}/.local/bin:'))
        context = output['additional_context']
        self.assertIn('# RepoQL: Repository Orientation', context)
        self.assertIn('github://acme/widgets', context)
        self.assertIn('Accessible uplinks: acme-gcp', context)
        self.assertIn('## Repository Concepts Index (.repoql/concepts/', context)
        self.assertIn('- Rule — invariant', context)
        self.assertTrue(all(c['cwd'] == str(self.workspace) for c in self.calls(['query'])))

    def test_session_start_defers_the_index_to_the_generated_rule(self):
        self.write_concepts_readme()
        rules = self.workspace / '.cursor' / 'rules'
        rules.mkdir(parents=True)
        (rules / 'repoql-concepts.g.mdc').write_text('---\nalwaysApply: true\n---\n')
        output = self.run_script('session-start.sh', self.session_payload())
        self.assertNotIn('Repository Concepts Index', output['additional_context'])

    def test_session_start_without_rql_or_jq_still_answers_json(self):
        # REPOQL_NO_BOOTSTRAP=1 (from setUp) keeps the bootstrap from downloading anything.
        no_rql = {'PATH': '/usr/bin:/bin', 'HOME': str(self.workspace)}
        self.assertEqual(set(self.run_script('session-start.sh', self.session_payload(), **no_rql)), {'env'})
        self.assertEqual(self.run_script('session-start.sh', self.session_payload(), PATH='/nonexistent'), {})

    # Wiring

    def test_every_hook_command_names_an_executable_plugin_script(self):
        hooks = json.loads((PLUGIN / 'hooks' / 'hooks.json').read_text())
        self.assertEqual(hooks['version'], 1)
        for event, entries in hooks['hooks'].items():
            for entry in entries:
                with self.subTest(event=event, command=entry['command']):
                    prefix = '${CURSOR_PLUGIN_ROOT}/'
                    self.assertTrue(entry['command'].startswith(prefix))
                    script = PLUGIN / entry['command'][len(prefix):]
                    self.assertTrue(os.access(script, os.X_OK), script)

    def test_skill_names_match_their_folders(self):
        for skill in (PLUGIN / 'skills').iterdir():
            front = (skill / 'SKILL.md').read_text().split('---')[1]
            names = [line.split(':', 1)[1].strip().strip('"') for line in front.splitlines() if line.startswith('name:')]
            self.assertEqual(names, [skill.name])


if __name__ == '__main__':
    unittest.main()
