"""Verify the once-per-machine signed-out notice in the Claude Code startup scripts, with a fake rql.

The notice appears only when `rql account whoami --json` reports no credential at all. A signed-in machine, a session
that fails its live check, an API key, and a whoami that fails outright each stay quiet. After a definite answer the
hook never asks again.
"""
import json
from pathlib import Path
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, argv, context_of, hook_env, install_fake, run

FAKE_RQL = '''
import json, os, sys
with open(os.environ['RQL_CALL_LOG'], 'a', encoding='utf-8') as log:
    log.write(' '.join(sys.argv[1:]) + '\\n')
if sys.argv[1:4] != ['account', 'whoami', '--json']:
    sys.exit(1)
state = os.environ['WHOAMI_STATE']
material = {'oidcEnvironmentAvailable': False, 'oidcRequestHost': None, 'apiKeyMasked': None,
            'sessionPresent': False, 'sessionEmail': None, 'sessionExpiresAt': None,
            'exchangeEndpoint': 'https://portal.repoql.ai/api/', 'exchangeEndpointIsDefault': True}
if state == 'crash':
    sys.exit(2)
if state == 'signed-in':
    material['sessionPresent'] = True
    print(json.dumps({'authenticated': True, 'subject': 'user_1', 'material': material}))
    sys.exit(0)
if state == 'session-failing':
    material['sessionPresent'] = True
if state == 'api-key':
    material['apiKeyMasked'] = 'rq_live_...abcd'
print(json.dumps({'authenticated': False, 'failureMessage': 'No credential.', 'material': material}))
sys.exit(1)
'''


class SigninStartupHooksTests(unittest.TestCase):
    def start(self, implementation, scratch, state):
        bin_dir = Path(scratch) / '.local' / 'bin'
        install_fake(bin_dir, 'rql', FAKE_RQL)
        calls = Path(scratch) / 'calls.log'
        state_dir = Path(scratch) / 'state'
        env = hook_env(bin_dir, HOME=scratch, REPOQL_STATE_DIR=str(state_dir), RQL_CALL_LOG=str(calls),
                       WHOAMI_STATE=state)
        result = run(argv(implementation, 'repoql', 'session-start'), {'cwd': scratch}, env, scratch, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        whoami_calls = [line for line in calls.read_text(encoding='utf-8').splitlines()
                        if line.startswith('account whoami')] if calls.exists() else []
        calls.unlink(missing_ok=True)
        return result, whoami_calls, state_dir / 'signin-notice'

    def test_signed_out_machine_is_told_once(self):
        for implementation in IMPLEMENTATIONS:
            with self.subTest(implementation=implementation), tempfile.TemporaryDirectory() as scratch:
                result, whoami_calls, marker = self.start(implementation, scratch, 'signed-out')
                reply = json.loads(result.stdout)
                self.assertIn('RepoQL is running signed out', reply['systemMessage'])
                self.assertIn('rql account login', reply['systemMessage'])
                self.assertIn('https://repoql.com/terms/2026-09-11/', reply['systemMessage'])
                context = context_of(result, 'SessionStart')
                self.assertEqual(context.count('## Account'), 1)
                self.assertIn('never run it unasked', context)
                self.assertEqual(len(whoami_calls), 1)
                self.assertTrue(marker.exists())

                again, whoami_calls, _ = self.start(implementation, scratch, 'signed-out')
                self.assertNotIn('systemMessage', json.loads(again.stdout))
                self.assertNotIn('## Account', context_of(again, 'SessionStart'))
                self.assertEqual(whoami_calls, [])

    def test_signed_in_machine_is_never_told_or_asked_again(self):
        for implementation in IMPLEMENTATIONS:
            with self.subTest(implementation=implementation), tempfile.TemporaryDirectory() as scratch:
                result, whoami_calls, marker = self.start(implementation, scratch, 'signed-in')
                self.assertNotIn('systemMessage', json.loads(result.stdout))
                self.assertNotIn('## Account', context_of(result, 'SessionStart'))
                self.assertEqual(len(whoami_calls), 1)
                self.assertTrue(marker.exists())

                _, whoami_calls, _ = self.start(implementation, scratch, 'signed-out')
                self.assertEqual(whoami_calls, [])

    def test_uncertain_answers_stay_quiet_and_ask_again(self):
        for implementation in IMPLEMENTATIONS:
            for state in ('session-failing', 'api-key', 'crash'):
                with self.subTest(implementation=implementation, state=state), \
                        tempfile.TemporaryDirectory() as scratch:
                    result, whoami_calls, marker = self.start(implementation, scratch, state)
                    self.assertNotIn('systemMessage', json.loads(result.stdout))
                    context = context_of(result, 'SessionStart')
                    self.assertNotIn('## Account', context)
                    self.assertIn('## Concepts', context)
                    self.assertEqual(len(whoami_calls), 1)
                    self.assertFalse(marker.exists())


if __name__ == '__main__':
    unittest.main()
