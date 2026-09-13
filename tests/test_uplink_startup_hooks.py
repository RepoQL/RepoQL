"""Verify account uplink context in the actual startup scripts without cloud access."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class UplinkStartupHooksTests(unittest.TestCase):
    def test_bash_startup_includes_names_once_and_keeps_failure_explicit(self):
        for harness in ('repoql', 'repoql-codex'):
            for failure in (False, True):
                with self.subTest(harness=harness, failure=failure), tempfile.TemporaryDirectory() as scratch:
                    wrapper = '''rql() {
                        if [ "$1" = uplinks ]; then
                            [ "$UPLINK_TEST_FAILURE" = 1 ] && return 1
                            printf '%s\\n' 'Accessible uplinks: billing, platform' 'Pass uplink="name"; omit for local.'
                        fi
                        return 0
                    }
                    export -f rql
                    bash "$1"
                    '''
                    result = subprocess.run(['bash', '-c', wrapper, 'hook-test',
                        str(ROOT / 'plugins' / harness / 'scripts/session-start.sh')],
                        input=json.dumps({'cwd': scratch}), text=True, capture_output=True, cwd=scratch,
                        env={**os.environ, 'UPLINK_TEST_FAILURE': '1' if failure else '0'}, timeout=10)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    context = json.loads(result.stdout)['hookSpecificOutput']['additionalContext']
                    self.assertEqual(context.count('## Accessible Uplinks'), 1)
                    self.assertIn('## Concepts', context)
                    if failure:
                        self.assertIn('not checked', context)
                        self.assertIn('run rql uplinks', context)
                        self.assertNotIn('Accessible uplinks: billing', context)
                    else:
                        self.assertEqual(context.count('Accessible uplinks: billing, platform'), 1)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is unavailable')
    def test_powershell_startup_includes_names_and_keeps_failure_explicit(self):
        for harness in ('repoql', 'repoql-codex'):
            for failure in (False, True):
                with self.subTest(harness=harness, failure=failure), tempfile.TemporaryDirectory() as scratch:
                    fake = Path(scratch) / 'rql-test'
                    fake.write_text('''#!/bin/sh
if [ "$1" = uplinks ]; then
    [ "$UPLINK_TEST_FAILURE" = 1 ] && exit 1
    echo 'Accessible uplinks: billing, platform'
    echo 'Pass uplink="name"; omit for local.'
fi
exit 0
''')
                    fake.chmod(0o755)
                    script = ROOT / 'plugins' / harness / 'scripts/session-start.ps1'
                    wrapper = '''function Get-Command {
                        param([string]$Name)
                        if ($Name -eq 'rql') { return [pscustomobject]@{Source=$env:UPLINK_TEST_COMMAND} }
                        Microsoft.PowerShell.Core\\Get-Command $Name
                    }
                    function rql { & $env:UPLINK_TEST_COMMAND @args }
                    & $env:UPLINK_TEST_SCRIPT
                    '''
                    result = subprocess.run(['pwsh', '-NoProfile', '-Command', wrapper],
                        input=json.dumps({'cwd': scratch}), text=True, capture_output=True, cwd=scratch,
                        env={**os.environ, 'UPLINK_TEST_FAILURE': '1' if failure else '0',
                            'UPLINK_TEST_COMMAND': str(fake), 'UPLINK_TEST_SCRIPT': str(script)}, timeout=15)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    context = result.stdout
                    self.assertEqual(context.count('## Accessible Uplinks'), 1)
                    if failure:
                        self.assertIn('not checked', context)
                    else:
                        self.assertIn('Accessible uplinks: billing, platform', context)


if __name__ == '__main__':
    unittest.main()
