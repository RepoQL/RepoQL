"""Verify delivered-text adapters with literal native and MCP read payloads."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HARNESSES = ['repoql', 'repoql-codex']


class VocabularyReadHooksTests(unittest.TestCase):
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
                    'HOOK_CALLS': str(self.log), 'HOOK_MODE': 'normal', 'REPOQL_CWD': '/wrong-workspace'}
        (self.bin / 'rql').write_text('''#!/usr/bin/env python3
import json, os, sys
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps({'args': sys.argv[1:], 'cwd': os.getcwd(), 'workspace_pin': os.environ.get('REPOQL_CWD'), 'content': sys.stdin.read()}) + '\\n')
if os.environ['HOOK_MODE'] == 'failure':
    print('host unavailable', file=sys.stderr)
    sys.exit(1)
if os.environ['HOOK_MODE'] != 'empty':
    print('DirtySweep [engine] — The dirty-file actor.')
''')
        (self.bin / 'rql').chmod(0o755)

    def run_hook(self, harness, **overrides):
        payload = {'hook_event_name': 'PostToolUse', 'session_id': 'read-session',
                   'cwd': str(self.workspace), 'tool_name': 'Read',
                   'tool_input': {'file_path': str(self.workspace / 'src/A.cs')},
                   'tool_response': {'type': 'text', 'file': {
                       'filePath': str(self.workspace / 'src/A.cs'), 'content': 'DirtySweep works.',
                       'numLines': 1, 'startLine': 1, 'totalLines': 20}}}
        payload.update(overrides)
        result = subprocess.run(['bash', str(ROOT / 'plugins' / harness / 'scripts/vocabulary-read-hook.sh')],
                                input=json.dumps(payload), text=True, capture_output=True,
                                env=self.env, cwd=self.root, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_native_read_delivers_definition_with_session_scope_and_workspace(self):
        for harness in HARNESSES:
            with self.subTest(harness=harness):
                result = self.run_hook(harness)
                self.assertEqual(result.stderr, '')
                self.assertEqual(json.loads(result.stdout), {'hookSpecificOutput': {
                    'hookEventName': 'PostToolUse',
                    'additionalContext': 'DirtySweep [engine] — The dirty-file actor.'}})
                self.assertEqual(self.calls()[-1], {'cwd': str(self.workspace), 'workspace_pin': str(self.workspace), 'content': 'DirtySweep works.',
                    'args': ['vocabulary', 'hints', str(self.workspace / 'src/A.cs'), '--session', 'read-session',
                             '--limit', '5', '--max-chars', '2000']})

    def test_mcp_text_blocks_and_uri_modifiers(self):
        for harness in HARNESSES:
            with self.subTest(harness=harness):
                self.run_hook(harness, tool_name='mcp__plugin_repoql_repoql__read',
                    tool_input={'uriGlob': 'file:///src/**/*.cs#line=1,5 => content', 'keywords': 'NeverMatchArguments'},
                    tool_response={'content': [{'type': 'text', 'text': 'DirtySweep'},
                                               {'type': 'image', 'data': 'NeverMatchImage'},
                                               {'type': 'text', 'text': 'file watcher'}], 'isError': False})
                call = self.calls()[-1]
                self.assertEqual(call['content'], 'DirtySweep\nfile watcher')
                self.assertEqual(call['args'][2], 'file:///src/**/*.cs')

    def test_plain_text_response(self):
        for harness in HARNESSES:
            self.run_hook(harness, tool_name='read_file', tool_input={'path': 'src/A.cs'}, tool_response='file watcher')
            self.assertEqual(self.calls()[-1]['content'], 'file watcher')
            self.assertEqual(self.calls()[-1]['args'][2], str(self.workspace / 'src/A.cs'))

    def test_empty_errors_images_metadata_and_unrelated_tools_do_not_call_cli(self):
        for harness in HARNESSES:
            for overrides in [
                {'tool_response': ''}, {'tool_response': {'isError': True, 'content': 'DirtySweep'}},
                {'tool_response': {'is_error': True, 'content': 'DirtySweep'}},
                {'tool_response': {'content': [{'type': 'image', 'data': 'DirtySweep'}]}},
                {'tool_response': {'structuredContent': {'secret': 'DirtySweep'}}},
                {'tool_input': {'file_path': 'DirtySweep.cs'}, 'tool_response': {}},
                {'tool_name': 'Bash'}, {'tool_name': 'mcp__other__read'},
                {'session_id': ''}, {'cwd': '/does-not-exist'}, {'tool_input': {}}]:
                with self.subTest(harness=harness, overrides=overrides):
                    result = self.run_hook(harness, **overrides)
                    self.assertEqual(result.stdout, '')
                    self.assertEqual(self.calls(), [])

    def test_bounded_unicode_content_prefix(self):
        for harness in HARNESSES:
            self.run_hook(harness, tool_response='🙂' * 70000)
            self.assertEqual(self.calls()[-1]['content'], '🙂' * 65536)

    def test_cli_failure_reports_but_never_blocks_read(self):
        self.env['HOOK_MODE'] = 'failure'
        for harness in HARNESSES:
            result = self.run_hook(harness)
            self.assertEqual(result.stdout, '')
            self.assertIn('RepoQL vocabulary hints: CLI failed;', result.stderr)

    def test_no_new_definitions_is_quiet(self):
        self.env['HOOK_MODE'] = 'empty'
        for harness in HARNESSES:
            result = self.run_hook(harness)
            self.assertEqual((result.stdout, result.stderr), ('', ''))

    def test_hook_registration_covers_supported_reads_only(self):
        for harness in HARNESSES:
            config = json.loads((ROOT / 'plugins' / harness / 'hooks/hooks.json').read_text())
            hook = config['hooks']['PostToolUse'][0]
            for name in ['Read', 'read_file', 'mcp__repoql__read', 'mcp__plugin_RepoQL_RepoQL__read']:
                self.assertTrue(re.fullmatch(hook['matcher'], name), name)
            for name in ['Bash', 'Write', 'mcp__other__read', 'mcp__repoql__read_other']:
                self.assertFalse(re.fullmatch(hook['matcher'], name), name)
            self.assertIn('vocabulary-read-hook.sh', hook['hooks'][0]['command'])

    def test_both_adapters_stay_identical(self):
        self.assertEqual(*[(ROOT / 'plugins' / harness / 'scripts/vocabulary-read-hook.sh').read_bytes()
                           for harness in HARNESSES])


if __name__ == '__main__':
    unittest.main()
