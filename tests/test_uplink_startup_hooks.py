"""Verify account uplink context in the actual startup scripts without cloud access."""
from pathlib import Path
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, argv, context_of, hook_env, install_fake, run

FAKE_RQL = '''
import os, sys
if sys.argv[1:2] == ['uplinks']:
    if os.environ['UPLINK_TEST_FAILURE'] == '1':
        sys.exit(1)
    print('Accessible uplinks: billing, platform')
    print('Pass uplink="name"; omit for local.')
'''


class UplinkStartupHooksTests(unittest.TestCase):
    def test_startup_includes_names_once_and_keeps_failure_explicit(self):
        for implementation in IMPLEMENTATIONS:
            for harness in ('repoql', 'repoql-codex'):
                for failure in (False, True):
                    with self.subTest(implementation=implementation, harness=harness, failure=failure), \
                            tempfile.TemporaryDirectory() as scratch:
                        bin_dir = Path(scratch) / '.local' / 'bin'
                        install_fake(bin_dir, 'rql', FAKE_RQL)
                        result = run(argv(implementation, harness, 'session-start'), {'cwd': scratch},
                                     hook_env(bin_dir, HOME=scratch, UPLINK_TEST_FAILURE='1' if failure else '0'),
                                     scratch, timeout=30)
                        self.assertEqual(result.returncode, 0, result.stderr)
                        context = context_of(result, 'SessionStart')
                        self.assertEqual(context.count('## Accessible Uplinks'), 1)
                        self.assertIn('## Concepts', context)
                        if failure:
                            self.assertIn('not checked', context)
                            self.assertIn('run rql uplinks', context)
                            self.assertNotIn('Accessible uplinks: billing', context)
                        else:
                            self.assertEqual(context.count('Accessible uplinks: billing, platform'), 1)
                            self.assertIn('Pass uplink="name"; omit for local.', context)


if __name__ == '__main__':
    unittest.main()
