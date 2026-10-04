"""Verify delivered-text adapters with literal native and MCP read payloads."""
import json
from pathlib import Path
import re
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, ROOT, argv, context_of, hook_env, install_fake, run, same_path, scripts

HARNESSES = ['repoql', 'repoql-codex']

FAKE_RQL = '''
import json, os, sys
content = sys.stdin.buffer.read().decode('utf-8')
with open(os.environ['HOOK_CALLS'], 'a') as log:
    log.write(json.dumps({'args': sys.argv[1:], 'cwd': os.getcwd(), 'workspace_pin': os.environ.get('REPOQL_CWD'), 'content': content}) + '\\n')
if os.environ['HOOK_MODE'] == 'failure':
    print('host unavailable', file=sys.stderr)
    sys.exit(1)
if os.environ['HOOK_MODE'] != 'empty':
    sys.stdout.buffer.write('DirtySweep [engine] \u2014 The dirty-file actor.\\n'.encode('utf-8'))
'''


class VocabularyReadHooksTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.workspace = self.root / 'workspace with spaces'
        self.workspace.mkdir()
        self.bin = self.root / 'bin'
        install_fake(self.bin, 'rql', FAKE_RQL)
        self.log = self.root / 'calls.jsonl'
        self.env = hook_env(self.bin, HOOK_CALLS=str(self.log), HOOK_MODE='normal', REPOQL_CWD='/wrong-workspace')

    def each(self):
        for implementation in IMPLEMENTATIONS:
            for harness in HARNESSES:
                with self.subTest(implementation=implementation, harness=harness):
                    self.log.unlink(missing_ok=True)
                    yield implementation, harness

    def run_hook(self, implementation, harness, **overrides):
        payload = {'hook_event_name': 'PostToolUse', 'session_id': 'read-session',
                   'cwd': str(self.workspace), 'tool_name': 'Read',
                   'tool_input': {'file_path': str(self.workspace / 'src/A.cs')},
                   'tool_response': {'type': 'text', 'file': {
                       'filePath': str(self.workspace / 'src/A.cs'), 'content': 'DirtySweep works.',
                       'numLines': 1, 'startLine': 1, 'totalLines': 20}}}
        payload.update(overrides)
        result = run(argv(implementation, harness, 'vocabulary-read-hook'), payload, self.env, self.root)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self):
        # PowerShell ends the text it pipes to a program with a line break.
        calls = [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()] if self.log.exists() else []
        for call in calls:
            call['content'] = call['content'].rstrip('\r\n')
        return calls

    def test_native_read_delivers_definition_with_session_scope_and_workspace(self):
        for implementation, harness in self.each():
            result = self.run_hook(implementation, harness)
            self.assertEqual(result.stderr, '')
            self.assertEqual(context_of(result, 'PostToolUse'), 'DirtySweep [engine] — The dirty-file actor.')
            call = self.calls()[-1]
            self.assertTrue(same_path(call['cwd'], self.workspace), call['cwd'])
            self.assertEqual(call['workspace_pin'], str(self.workspace))
            self.assertEqual(call['content'], 'DirtySweep works.')
            self.assertEqual(call['args'], ['vocabulary', 'hints', str(self.workspace / 'src/A.cs'), '--session', 'read-session',
                                            '--limit', '5', '--max-chars', '2000'])

    def test_mcp_text_blocks_and_uri_modifiers(self):
        for implementation, harness in self.each():
            self.run_hook(implementation, harness, tool_name='mcp__plugin_repoql_repoql__read',
                tool_input={'uriGlob': 'file:///src/**/*.cs#line=1,5 => content', 'keywords': 'NeverMatchArguments'},
                tool_response={'content': [{'type': 'text', 'text': 'Dirty "Sweep"\n\tline \\ two — é'},
                                           {'type': 'image', 'data': 'NeverMatchImage'},
                                           {'type': 'text', 'text': 'file watcher'}], 'isError': False})
            call = self.calls()[-1]
            self.assertEqual(call['content'].replace('\r\n', '\n'), 'Dirty "Sweep"\n\tline \\ two — é\nfile watcher')
            self.assertEqual(call['args'][2], 'file:///src/**/*.cs')

    def test_plain_text_response(self):
        for implementation, harness in self.each():
            self.run_hook(implementation, harness, tool_name='read_file', tool_input={'path': 'src/A.cs'}, tool_response='file watcher')
            self.assertEqual(self.calls()[-1]['content'], 'file watcher')
            self.assertEqual(Path(self.calls()[-1]['args'][2]), self.workspace / 'src/A.cs')

    def test_native_paths_are_escaped_but_mcp_globs_are_not(self):
        for implementation, harness in self.each():
            self.run_hook(implementation, harness, tool_name='read_file', tool_input={'path': 'app/[slug]/page;1.tsx'}, tool_response='x')
            self.assertEqual(Path(self.calls()[-1]['args'][2]), self.workspace / 'app/%5Bslug]/page%3B1.tsx')
            self.run_hook(implementation, harness, tool_name='mcp__repoql__read', tool_input={'uriGlob': 'file:///src/{a,b}/[A-Z]*.cs'},
                          tool_response={'content': [{'type': 'text', 'text': 'x'}]})
            self.assertEqual(self.calls()[-1]['args'][2], 'file:///src/{a,b}/[A-Z]*.cs')

    def test_empty_errors_images_metadata_and_unrelated_tools_do_not_call_cli(self):
        for implementation, harness in self.each():
            for overrides in [
                {'tool_response': ''}, {'tool_response': {'isError': True, 'content': 'DirtySweep'}},
                {'tool_response': {'is_error': True, 'content': 'DirtySweep'}},
                {'tool_response': {'content': [{'type': 'image', 'data': 'DirtySweep'}]}},
                {'tool_response': {'structuredContent': {'secret': 'DirtySweep'}}},
                {'tool_input': {'file_path': 'DirtySweep.cs'}, 'tool_response': {}},
                {'tool_name': 'Bash'}, {'tool_name': 'mcp__other__read'},
                {'session_id': ''}, {'cwd': '/does-not-exist'}, {'tool_input': {}}]:
                with self.subTest(overrides=overrides):
                    result = self.run_hook(implementation, harness, **overrides)
                    self.assertEqual(result.stdout, '')
                    self.assertEqual(self.calls(), [])

    def test_content_is_a_bounded_prefix_of_whole_characters(self):
        # The CLI reads at most 131072 UTF-16 characters, two for each of these.
        for implementation, harness in self.each():
            self.run_hook(implementation, harness, tool_response='🙂' * 70000)
            content = self.calls()[-1]['content']
            self.assertEqual(content, '🙂' * len(content))
            self.assertTrue(16384 <= len(content) <= 65536, len(content))

    def test_cli_failure_reports_but_never_blocks_read(self):
        self.env['HOOK_MODE'] = 'failure'
        for implementation, harness in self.each():
            result = self.run_hook(implementation, harness)
            self.assertEqual(result.stdout, '')
            self.assertIn('RepoQL vocabulary hints: CLI failed;', result.stderr)

    def test_no_new_definitions_is_quiet(self):
        self.env['HOOK_MODE'] = 'empty'
        for implementation, harness in self.each():
            result = self.run_hook(implementation, harness)
            self.assertEqual((result.stdout, result.stderr), ('', ''))

    def test_hook_registration_covers_supported_reads_only(self):
        for harness in HARNESSES:
            config = json.loads((ROOT / 'plugins' / harness / 'hooks/hooks.json').read_text())
            hook = config['hooks']['PostToolUse'][0]
            for name in ['Read', 'read_file', 'mcp__repoql__read', 'mcp__plugin_RepoQL_RepoQL__read']:
                self.assertTrue(re.fullmatch(hook['matcher'], name), name)
            for name in ['Bash', 'Write', 'mcp__other__read', 'mcp__repoql__read_other']:
                self.assertFalse(re.fullmatch(hook['matcher'], name), name)
            self.assertIn('scripts/vocabulary-read-hook.', hook['hooks'][0]['command'])

    def test_both_adapters_stay_identical(self):
        for extension in ('sh', 'ps1'):
            self.assertEqual(*[(scripts(harness) / f'vocabulary-read-hook.{extension}').read_bytes() for harness in HARNESSES])


if __name__ == '__main__':
    unittest.main()
