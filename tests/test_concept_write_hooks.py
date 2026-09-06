"""Exercise the plugin entrypoints with literal harness payloads and a fake CLI."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ConceptWriteHooksTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.workspace = self.root / 'workspace with spaces'
        self.workspace.mkdir()
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'calls.jsonl'
        self.env = {**os.environ, 'PATH': f'{self.bin}:{os.environ["PATH"]}',
                    'HOOK_CALLS': str(self.log), 'HOOK_MODE': 'normal'}
        (self.bin / 'rql').write_text('''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps({'args': args, 'cwd': os.getcwd()}) + '\\n')
if args[:2] != ['concept', 'hints']:
    print('retired command', file=sys.stderr)
    sys.exit(2)
mode = os.environ['HOOK_MODE']
if mode == 'failure':
    print('host unavailable', file=sys.stderr)
    sys.exit(1)
if mode == 'malformed':
    print('not json')
    sys.exit(0)
limit = int(args[args.index('--limit') + 1])
terms = [] if mode == 'empty' else [
    {'uri': 'concept:///Rule.md', 'invariant': 'Preserve the contract.', 'why': 'Callers depend on it.'}
]
if mode == 'many':
    terms = [{'uri': f'concept:///{args[2]}-{i}', 'invariant': 'Rule'} for i in range(limit)]
print(json.dumps({'targetUri': args[2], 'concepts': terms}))
''')
        (self.bin / 'rql').chmod(0o755)

    def run_hook(self, harness, files=None, **overrides):
        files = files if files is not None else ['src/File with spaces.cs']
        if harness == 'repoql':
            payload = {'tool_name': 'MultiEdit', 'tool_input': {'edits': [{'file_path': f} for f in files]}}
        else:
            payload = {'tool_name': 'apply_patch', 'tool_input': {'patch': '\n'.join(f'*** Update File: {f}' for f in files)}}
        payload.update(cwd=str(self.workspace), session_id='session-123')
        payload.update(overrides)
        result = subprocess.run(['bash', str(ROOT / 'plugins' / harness / 'scripts/concepts-write-hook.sh')],
                                input=json.dumps(payload), text=True, capture_output=True,
                                env=self.env, cwd=self.root, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_current_command_session_workspace_and_context(self):
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                result = self.run_hook(harness)
                self.assertEqual(result.stderr, '')
                output = json.loads(result.stdout)['hookSpecificOutput']
                self.assertEqual(output['hookEventName'], 'PreToolUse')
                self.assertNotIn('permissionDecision', output)
                self.assertIn('concept:///Rule.md\tPreserve the contract.', output['additionalContext'])
                self.assertIn('Callers depend on it.', output['additionalContext'])
                self.assertEqual(self.calls()[-1], {'cwd': str(self.workspace), 'args':
                    ['concept', 'hints', 'src/File with spaces.cs', '--session', 'session-123', '--limit', '5', '--json']})

    def test_each_unique_path_is_queried(self):
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                before = len(self.calls())
                self.run_hook(harness, ['new.cs', 'existing.cs', 'new.cs'])
                calls = self.calls()[before:]
                self.assertEqual({c['args'][2] for c in calls}, {'new.cs', 'existing.cs'})
                self.assertEqual(len(calls), 2)

    def test_total_context_cap(self):
        self.env['HOOK_MODE'] = 'many'
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                before = len(self.calls())
                result = self.run_hook(harness, ['a.cs', 'b.cs'])
                context = json.loads(result.stdout)['hookSpecificOutput']['additionalContext']
                self.assertEqual(context.count('concept:///'), 5)
                self.assertEqual(len(self.calls()) - before, 1)

    def test_empty_results_are_quiet(self):
        self.env['HOOK_MODE'] = 'empty'
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                result = self.run_hook(harness)
                self.assertEqual((result.stdout, result.stderr), ('', ''))
                self.assertTrue(self.calls())

    def test_failures_are_visible_but_do_not_block(self):
        for mode in ['failure', 'malformed']:
            self.env['HOOK_MODE'] = mode
            for harness in ['repoql', 'repoql-codex']:
                with self.subTest(harness=harness, mode=mode):
                    result = self.run_hook(harness)
                    self.assertEqual(result.stdout, '')
                    self.assertIn('RepoQL concept hints:', result.stderr)

    def test_missing_session_does_not_share_ambient_dedupe(self):
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                result = self.run_hook(harness, session_id='')
                self.assertEqual(result.stdout, '')
                self.assertFalse(self.calls())

    def test_codex_add_delete_move_and_command_field(self):
        result = self.run_hook('repoql-codex', tool_input={'command':
            '*** Begin Patch\n*** Add File: added.cs\n+new\n*** Delete File: deleted.cs\n'
            '*** Update File: old.cs\n*** Move to: moved.cs\n*** End Patch'})
        self.assertTrue(result.stdout)
        self.assertEqual({c['args'][2] for c in self.calls()}, {'added.cs', 'deleted.cs', 'old.cs', 'moved.cs'})

    def test_claude_write_file_path(self):
        result = self.run_hook('repoql', tool_name='Write', tool_input={'file_path': str(self.workspace / 'new.cs')})
        self.assertTrue(result.stdout)
        self.assertEqual(self.calls()[0]['args'][2], str(self.workspace / 'new.cs'))

    def test_large_edits_keep_the_existing_eight_file_bound(self):
        self.env['HOOK_MODE'] = 'empty'
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                before = len(self.calls())
                self.run_hook(harness, [f'{i}.cs' for i in range(20)])
                self.assertEqual(len(self.calls()) - before, 8)

    def test_no_paths_are_quiet(self):
        for harness in ['repoql', 'repoql-codex']:
            with self.subTest(harness=harness):
                result = self.run_hook(harness, [])
                self.assertEqual(result.stdout, '')
                self.assertFalse(self.calls())


if __name__ == '__main__':
    unittest.main()
