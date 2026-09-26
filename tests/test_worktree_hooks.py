"""Verify the worktree adapters forward literal Claude Code hook payloads to rql."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / 'plugins' / 'repoql' / 'scripts'


class WorktreeHooksTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.project = self.root / 'project with spaces'
        self.project.mkdir()
        self.worktree = self.root / 'wt-feature'
        (self.worktree / 'src').mkdir(parents=True)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.log = self.root / 'calls.jsonl'
        self.env = {**os.environ, 'PATH': f'{self.bin}:{os.environ["PATH"]}', 'HOOK_CALLS': str(self.log),
                    'HOOK_MODE': 'notice', 'CLAUDE_PROJECT_DIR': str(self.project), 'REPOQL_CWD': '/wrong-workspace'}
        (self.bin / 'rql').write_text('''#!/usr/bin/env python3
import json, os, sys
stdin = '' if sys.stdin.isatty() else sys.stdin.read()
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps({'args': sys.argv[1:], 'cwd': os.getcwd(), 'workspace_pin': os.environ.get('REPOQL_CWD'), 'stdin': stdin}) + '\\n')
if os.environ['HOOK_MODE'] == 'failure':
    print('boom', file=sys.stderr)
    sys.exit(1)
if os.environ['HOOK_MODE'] == 'notice' and sys.argv[1:3] == ['worktree', 'check']:
    print('RepoQL worktree notice: this result covers src/A.cs.')
''')
        (self.bin / 'rql').chmod(0o755)

    def run_hook(self, script, payload):
        result = subprocess.run(['bash', str(SCRIPTS / script)], input=json.dumps(payload), text=True,
                                capture_output=True, env=self.env, cwd=self.root, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self, expected=None):
        # Tracking runs detached, so wait briefly for the expected number of calls.
        deadline = time.monotonic() + 3
        while True:
            lines = self.log.read_text().splitlines() if self.log.exists() else []
            if expected is None or len(lines) >= expected or time.monotonic() > deadline:
                return [json.loads(line) for line in lines]
            time.sleep(0.05)

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
        result = self.run_hook('worktree-track-hook.sh', self.edit_payload(file_path=target))
        self.assertEqual(result.stdout, '')
        self.assertEqual(self.calls(expected=1), [{'args': ['worktree', 'track', target, '--session', 'wt-session'],
            'cwd': str(self.project), 'workspace_pin': str(self.project), 'stdin': ''}])

    def test_edit_resolves_relative_path_against_hook_cwd(self):
        payload = self.edit_payload(file_path='src/A.cs')
        payload['cwd'] = str(self.worktree)
        self.run_hook('worktree-track-hook.sh', payload)
        self.assertEqual(self.calls(expected=1)[0]['args'][2], str(self.worktree / 'src/A.cs'))

    def test_notebook_edit_tracks_notebook_path(self):
        target = str(self.worktree / 'nb.ipynb')
        self.run_hook('worktree-track-hook.sh', self.edit_payload(notebook_path=target))
        self.assertEqual(self.calls(expected=1)[0]['args'][2], target)

    def test_track_without_project_dir_calls_nothing(self):
        del self.env['CLAUDE_PROJECT_DIR']
        self.run_hook('worktree-track-hook.sh', self.edit_payload(file_path=str(self.worktree / 'src/A.cs')))
        time.sleep(0.3)
        self.assertEqual(self.calls(), [])

    def test_rql_tool_call_forwards_scope_cwd_and_text_then_returns_notice(self):
        payload = self.mcp_payload({'uriGlob': 'file:///src/** => structure'}, 'file:///src/A.cs')
        payload['cwd'] = str(self.worktree)
        result = self.run_hook('worktree-check-hook.sh', payload)
        self.assertEqual(json.loads(result.stdout), {'hookSpecificOutput': {
            'hookEventName': 'PostToolUse',
            'additionalContext': 'RepoQL worktree notice: this result covers src/A.cs.'}})
        self.assertEqual(self.calls(), [{'args': ['worktree', 'check', '--session', 'wt-session', '--cwd', str(self.worktree),
                                                  '--pattern', 'file:///src/** => structure'],
                                         'cwd': str(self.project), 'workspace_pin': str(self.project),
                                         'stdin': 'file:///src/A.cs'}])

    def test_rql_tool_call_without_glob_omits_pattern(self):
        self.run_hook('worktree-check-hook.sh', self.mcp_payload({'keywords': 'auth'}, 'ranked'))
        self.assertNotIn('--pattern', self.calls()[0]['args'])

    def test_silent_check_emits_nothing(self):
        self.env['HOOK_MODE'] = 'silent'
        result = self.run_hook('worktree-check-hook.sh', self.mcp_payload({}, 'text'))
        self.assertEqual(result.stdout, '')

    def test_failed_check_never_blocks(self):
        self.env['HOOK_MODE'] = 'failure'
        result = self.run_hook('worktree-check-hook.sh', self.mcp_payload({}, 'text'))
        self.assertEqual(result.stdout, '')

    def test_server_named_rql_is_checked(self):
        payload = self.mcp_payload({}, 'text')
        payload['tool_name'] = 'mcp__rql__explore'
        self.run_hook('worktree-check-hook.sh', payload)
        self.assertEqual(self.calls()[0]['args'][:2], ['worktree', 'check'])

    def test_other_tools_are_ignored(self):
        payload = self.mcp_payload({}, 'text')
        payload['tool_name'] = 'mcp__github__search'
        self.run_hook('worktree-check-hook.sh', payload)
        self.assertEqual(self.calls(), [])


if __name__ == '__main__':
    unittest.main()
