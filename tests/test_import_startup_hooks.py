"""Verify the startup scripts list every workspace repository and import, count the concepts and vocab words each carries, invite the import tool, and name concept://."""
from pathlib import Path
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, argv, context_of, hook_env, install_fake, run

INVITATION = 'Use the import tool whenever you like to add more.'
LOCAL_IMPORT = 'local:///srv/vendor/lib (325 concepts, 15 vocab words)'
CONCEPTS = 'concept:///** holds the concepts of this repository and its imports.'
# The fake answers the listing query, which must count memory, by mode; a legacy host rejects it and answers
# only the GitHub fallback.
FAKE_RQL = f'''
import os, sys
args = sys.argv[1:]
mode = os.environ['IMPORT_TEST_MODE']
if args[:1] == ['query']:
    modern = 'kind IN' in args[1]
    if mode == 'listed' and modern and 'vocab word' in args[1]:
        print('kind\\tline\\nimport\\tgithub://acme/widgets\\nimport\\t{LOCAL_IMPORT}\\nworkspace\\tgithub://acme/billing\\n[42 tok | 3 ms]')
        sys.exit(0)
    if mode == 'legacy' and not modern:
        print('kind\\tline\\nimport\\tgithub://acme/widgets')
        sys.exit(0)
    if mode == 'none':
        print('kind\\tline')
        sys.exit(0)
    sys.exit(1)
sys.exit(1 if args[:1] == ['uplinks'] else 0)
'''
IMPORTS = {
    'listed': ['github://acme/widgets', LOCAL_IMPORT],
    'legacy': ['github://acme/widgets'],
    'none': ['(none)'],
    'unreachable': ['(not checked'],
}


class ImportStartupHooksTests(unittest.TestCase):
    def assert_listing(self, context, mode):
        before_imports, after_heading = context.split('## Imported Repositories', 1)
        imports = after_heading.split('## Accessible Uplinks', 1)[0]
        for line in IMPORTS[mode]:
            self.assertIn(line, imports)
        self.assertEqual(imports.count(INVITATION), 1)
        self.assertNotIn('rql import', imports)
        self.assertNotIn('github://acme/billing', imports)
        self.assertNotIn('(memory:', context)
        self.assertEqual(context.count(CONCEPTS), 1)
        if mode == 'listed':
            self.assertIn('Use these URIs directly', imports)
            workspace = before_imports.split('## Workspace Repositories', 1)[1]
            self.assertIn('This workspace is a directory of repositories.', workspace)
            self.assertIn('github://acme/billing', workspace)
        else:
            self.assertNotIn('## Workspace Repositories', context)

    def test_startup_lists_every_import_kind_and_invites_more(self):
        for implementation in IMPLEMENTATIONS:
            for harness in ('repoql', 'repoql-codex'):
                for mode in IMPORTS:
                    with self.subTest(implementation=implementation, harness=harness, mode=mode), \
                            tempfile.TemporaryDirectory() as scratch:
                        # The bash hooks put $HOME/.local/bin first on PATH, so the fake lives there under a scratch HOME.
                        bin_dir = Path(scratch) / '.local' / 'bin'
                        install_fake(bin_dir, 'rql', FAKE_RQL)
                        result = run(argv(implementation, harness, 'session-start'), {'cwd': scratch},
                                     hook_env(bin_dir, HOME=scratch, IMPORT_TEST_MODE=mode), scratch, timeout=30)
                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assert_listing(context_of(result, 'SessionStart'), mode)


if __name__ == '__main__':
    unittest.main()
