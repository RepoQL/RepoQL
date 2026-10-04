"""Exercise the plugin entrypoints with literal harness payloads and a fake CLI."""
import json
from pathlib import Path
import tempfile
import unittest

from hook_support import Cases, IMPLEMENTATIONS, argv, context_of, hook_env, install_fake, run, same_path

HARNESSES = ['repoql', 'repoql-codex']
# The bash hooks take the rendered view; the PowerShell hooks ask for JSON.
HINT_FLAGS = {'sh': [], 'ps1': ['--json']}

FAKE_RQL = '''
import json, os, sys
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
    print('not hints')
    sys.exit(0)
limit = int(args[args.index('--limit') + 1])
terms = [] if mode == 'empty' else [
    {'uri': 'concept:///Rule.md', 'invariant': 'Preserve the contract.', 'why': 'Callers depend on it.'}
]
if mode == 'many':
    terms = [{'uri': f'concept:///{args[2]}-{i}', 'invariant': 'Rule'} for i in range(limit)]
if '--json' in args:
    print(json.dumps({'targetUri': args[2], 'concepts': terms}))
else:
    for term in terms:
        print(term['uri'] + '\\t' + term['invariant'] + ('\\n  why: ' + term['why'] if 'why' in term else ''))
'''


class ConceptWriteHooksTests(Cases, unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.workspace = self.root / 'workspace with spaces'
        self.workspace.mkdir()
        self.bin = self.root / 'bin'
        install_fake(self.bin, 'rql', FAKE_RQL)
        self.log = self.root / 'calls.jsonl'
        self.env = hook_env(self.bin, HOOK_CALLS=str(self.log), HOOK_MODE='normal')

    def each(self, harnesses=HARNESSES):
        return [(implementation, harness) for implementation in IMPLEMENTATIONS for harness in harnesses]

    def run_hook(self, implementation, harness, files=None, **overrides):
        files = files if files is not None else ['src/File with spaces.cs']
        if harness == 'repoql':
            payload = {'tool_name': 'MultiEdit', 'tool_input': {'edits': [{'file_path': f} for f in files]}}
        else:
            payload = {'tool_name': 'apply_patch', 'tool_input': {'patch': '\n'.join(f'*** Update File: {f}' for f in files)}}
        payload.update(cwd=str(self.workspace), session_id='session-123')
        payload.update(overrides)
        result = run(argv(implementation, harness, 'concepts-write-hook'), payload, self.env, self.root)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_current_command_session_workspace_and_context(self):
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness)
                self.assertEqual(result.stderr, '')
                context = context_of(result, 'PreToolUse')
                self.assertNotIn('permissionDecision', json.loads(result.stdout)['hookSpecificOutput'])
                self.assertIn('concept:///Rule.md\tPreserve the contract.', context)
                self.assertIn('Callers depend on it.', context)
                call = self.calls()[-1]
                self.assertTrue(same_path(call['cwd'], self.workspace), call['cwd'])
                self.assertEqual(call['args'], ['concept', 'hints', 'src/File with spaces.cs', '--session', 'session-123',
                                                '--limit', '5', *HINT_FLAGS[implementation]])

    def test_each_unique_path_is_queried(self):
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                self.run_hook(implementation, harness, ['new.cs', 'existing.cs', 'new.cs'])
                self.assertEqual(sorted(c['args'][2] for c in self.calls()), ['existing.cs', 'new.cs'])

    def test_glob_metacharacters_in_paths_are_escaped(self):
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                self.run_hook(implementation, harness, ['app/[slug]/page.tsx', 'a*b?c{d;e 50%.cs'])
                self.assertEqual({c['args'][2] for c in self.calls()},
                                 {'app/%5Bslug]/page.tsx', 'a%2Ab%3Fc%7Bd%3Be 50%.cs'})

    def test_paths_and_patches_survive_json_escaping(self):
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                self.run_hook(implementation, harness, ['src\\répo\\page 😀.tsx', 'src/tab\\tname.cs'])
                self.assertEqual({c['args'][2] for c in self.calls()}, {'src\\répo\\page 😀.tsx', 'src/tab\\tname.cs'})

    def test_total_context_cap(self):
        self.env['HOOK_MODE'] = 'many'
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness, ['a.cs', 'b.cs'])
                self.assertEqual(context_of(result, 'PreToolUse').count('concept:///'), 5)
                self.assertEqual(len(self.calls()), 1)

    def test_empty_results_are_quiet(self):
        self.env['HOOK_MODE'] = 'empty'
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness)
                self.assertEqual((result.stdout, result.stderr), ('', ''))
                self.assertTrue(self.calls())

    def test_a_failed_cli_is_visible_but_does_not_block(self):
        self.env['HOOK_MODE'] = 'failure'
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness)
                self.assertEqual(result.stdout, '')
                self.assertIn('RepoQL concept hints: CLI failed;', result.stderr)

    def test_unrecognised_cli_output_does_not_block(self):
        self.env['HOOK_MODE'] = 'malformed'
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness)
                self.assertEqual(result.stdout, '')

    def test_missing_session_does_not_share_ambient_dedupe(self):
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness, session_id='')
                self.assertEqual(result.stdout, '')
                self.assertFalse(self.calls())

    def test_codex_add_delete_move_and_command_field(self):
        for implementation, harness in self.each(['repoql-codex']):
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness, tool_input={'command':
                    '*** Begin Patch\n*** Add File: added.cs\n+new "quoted" \\ line\n*** Delete File: deleted.cs\n'
                    '*** Update File: old.cs\n*** Move to: moved.cs\n*** End Patch'})
                self.assertTrue(result.stdout)
                self.assertEqual({c['args'][2] for c in self.calls()}, {'added.cs', 'deleted.cs', 'old.cs', 'moved.cs'})

    def test_claude_write_file_path(self):
        for implementation, harness in self.each(['repoql']):
            with self.case(implementation=implementation, harness=harness):
                target = str(self.workspace / 'new.cs')
                result = self.run_hook(implementation, harness, tool_name='Write',
                                       tool_input={'file_path': target, 'content': '{"file_path": "decoy.cs"}'})
                self.assertTrue(result.stdout)
                self.assertEqual([c['args'][2] for c in self.calls()], [target])

    def test_large_edits_keep_the_existing_eight_file_bound(self):
        self.env['HOOK_MODE'] = 'empty'
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                self.run_hook(implementation, harness, [f'{i}.cs' for i in range(20)])
                self.assertEqual(len(self.calls()), 8)

    def test_no_paths_are_quiet(self):
        for implementation, harness in self.each():
            with self.case(implementation=implementation, harness=harness):
                result = self.run_hook(implementation, harness, [])
                self.assertEqual(result.stdout, '')
                self.assertFalse(self.calls())


if __name__ == '__main__':
    unittest.main()
