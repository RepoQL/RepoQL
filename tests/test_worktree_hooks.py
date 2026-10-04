"""Verify the worktree adapters forward literal Claude Code hook payloads to rql."""
import json
from pathlib import Path
import tempfile
import time
import unittest

from hook_support import Cases, IMPLEMENTATIONS, argv, context_of, hook_env, install_fake, run, same_path

FAKE_RQL = '''
import json, os, sys
stdin = '' if sys.stdin.isatty() else sys.stdin.buffer.read().decode('utf-8')
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps({'args': sys.argv[1:], 'cwd': os.getcwd(), 'workspace_pin': os.environ.get('REPOQL_CWD'), 'stdin': stdin}) + '\\n')
if os.environ['HOOK_MODE'] == 'failure':
    print('boom', file=sys.stderr)
    sys.exit(1)
if os.environ['HOOK_MODE'] == 'notice' and sys.argv[1:3] == ['worktree', 'check']:
    print('RepoQL worktree notice: this result covers src/A.cs.')
'''


class WorktreeHooksTests(Cases, unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.project = self.root / 'project with spaces'
        self.project.mkdir()
        self.worktree = self.root / 'wt-feature'
        (self.worktree / 'src').mkdir(parents=True)
        self.bin = self.root / 'bin'
        install_fake(self.bin, 'rql', FAKE_RQL)
        self.log = self.root / 'calls.jsonl'
        self.env = hook_env(self.bin, HOOK_CALLS=str(self.log), HOOK_MODE='notice',
                            CLAUDE_PROJECT_DIR=str(self.project), REPOQL_CWD='/wrong-workspace')

    def each(self):
        return IMPLEMENTATIONS

    def run_hook(self, implementation, hook, payload):
        result = run(argv(implementation, 'repoql', hook), payload, self.env, self.root)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self, expected=None):
        # The bash hook tracks detached, so wait briefly for the expected number of calls.
        deadline = time.monotonic() + 3
        while True:
            lines = self.log.read_text(encoding='utf-8').splitlines() if self.log.exists() else []
            if expected is None or len(lines) >= expected or time.monotonic() > deadline:
                calls = [json.loads(line) for line in lines]
                for call in calls:
                    # PowerShell ends the text it pipes to a program with a line break.
                    call['stdin'] = call['stdin'].rstrip('\r\n')
                return calls
            time.sleep(0.05)

    def assert_reads_from_the_project(self, call):
        self.assertTrue(same_path(call['cwd'], self.project), call['cwd'])
        self.assertEqual(call['workspace_pin'], str(self.project))

    def edit_payload(self, **tool_input):
        return {'hook_event_name': 'PostToolUse', 'session_id': 'wt-session', 'cwd': str(self.project),
                'tool_name': 'Edit', 'tool_input': tool_input, 'tool_response': {'success': True}}

    def mcp_payload(self, tool_input, text):
        return {'hook_event_name': 'PostToolUse', 'session_id': 'wt-session', 'cwd': str(self.project),
                'tool_name': 'mcp__plugin_repoql_repoql__read', 'tool_input': tool_input,
                'tool_response': {'content': [{'type': 'text', 'text': text},
                                              {'type': 'image', 'data': 'NeverForwardImages'}], 'isError': False}}

    def test_edit_tracks_absolute_path_from_the_project_directory(self):
        target = str(self.worktree / 'src/A.cs')
        for implementation in self.each():
            with self.case(implementation=implementation):
                result = self.run_hook(implementation, 'worktree-track-hook', self.edit_payload(file_path=target))
                self.assertEqual(result.stdout, '')
                call, = self.calls(expected=1)
                self.assertEqual((call['args'], call['stdin']), (['worktree', 'track', target, '--session', 'wt-session'], ''))
                self.assert_reads_from_the_project(call)

    def test_edit_resolves_relative_path_against_hook_cwd(self):
        payload = self.edit_payload(file_path='src/A.cs')
        payload['cwd'] = str(self.worktree)
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-track-hook', payload)
                self.assertEqual(Path(self.calls(expected=1)[0]['args'][2]), self.worktree / 'src/A.cs')

    def test_notebook_edit_tracks_notebook_path(self):
        target = str(self.worktree / 'nb.ipynb')
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-track-hook', self.edit_payload(notebook_path=target))
                self.assertEqual(self.calls(expected=1)[0]['args'][2], target)

    def test_track_without_project_dir_calls_nothing(self):
        del self.env['CLAUDE_PROJECT_DIR']
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-track-hook', self.edit_payload(file_path=str(self.worktree / 'src/A.cs')))
                time.sleep(0.3)
                self.assertEqual(self.calls(), [])

    def test_rql_tool_call_forwards_scope_cwd_and_text_then_returns_notice(self):
        payload = self.mcp_payload({'uriGlob': 'file:///src/** => structure'}, 'file:///src/A.cs')
        payload['cwd'] = str(self.worktree)
        for implementation in self.each():
            with self.case(implementation=implementation):
                result = self.run_hook(implementation, 'worktree-check-hook', payload)
                self.assertEqual(context_of(result, 'PostToolUse'), 'RepoQL worktree notice: this result covers src/A.cs.')
                call, = self.calls()
                self.assertEqual(call['args'], ['worktree', 'check', '--session', 'wt-session', '--cwd', str(self.worktree),
                                                '--pattern', 'file:///src/** => structure'])
                self.assertEqual(call['stdin'], 'file:///src/A.cs')
                self.assert_reads_from_the_project(call)

    def test_top_level_text_blocks_are_forwarded(self):
        payload = self.mcp_payload({}, 'unused')
        payload['tool_response'] = [{'type': 'text', 'text': 'first'}, {'type': 'text', 'text': 'second'}]
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-check-hook', payload)
                self.assertEqual(self.calls()[0]['stdin'].replace('\r\n', '\n'), 'first\nsecond')

    def test_rql_tool_call_without_glob_omits_pattern(self):
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-check-hook', self.mcp_payload({'keywords': 'auth'}, 'ranked'))
                self.assertNotIn('--pattern', self.calls()[0]['args'])

    def test_silent_check_emits_nothing(self):
        self.env['HOOK_MODE'] = 'silent'
        for implementation in self.each():
            with self.case(implementation=implementation):
                result = self.run_hook(implementation, 'worktree-check-hook', self.mcp_payload({}, 'text'))
                self.assertEqual(result.stdout, '')

    def test_failed_check_never_blocks(self):
        self.env['HOOK_MODE'] = 'failure'
        for implementation in self.each():
            with self.case(implementation=implementation):
                result = self.run_hook(implementation, 'worktree-check-hook', self.mcp_payload({}, 'text'))
                self.assertEqual(result.stdout, '')

    def test_server_named_rql_is_checked(self):
        payload = self.mcp_payload({}, 'text')
        payload['tool_name'] = 'mcp__rql__explore'
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-check-hook', payload)
                self.assertEqual(self.calls()[0]['args'][:2], ['worktree', 'check'])

    def test_other_tools_are_ignored(self):
        payload = self.mcp_payload({}, 'text')
        payload['tool_name'] = 'mcp__github__search'
        for implementation in self.each():
            with self.case(implementation=implementation):
                self.run_hook(implementation, 'worktree-check-hook', payload)
                self.assertEqual(self.calls(), [])


if __name__ == '__main__':
    unittest.main()
