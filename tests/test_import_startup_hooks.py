"""Verify the startup scripts invite the import tool under the imported-repository listing."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
INVITATION = 'Use the import tool whenever you like to add more.'
FAKE_RQL = '''#!/bin/sh
if [ "$1" = query ]; then
    case "$IMPORT_TEST_MODE" in
        listed) echo 'source_uri'; echo 'github://acme/widgets'; exit 0 ;;
        none) echo 'source_uri'; exit 0 ;;
        *) exit 1 ;;
    esac
fi
[ "$1" = uplinks ] && exit 1
exit 0
'''
EXPECTED = {
    'listed': 'github://acme/widgets',
    'none': '(none)',
    'unreachable': '(not checked',
}


def fake_rql(scratch):
    # The hooks put $HOME/.local/bin first on PATH, so the fake lives there under a scratch HOME.
    bin_dir = Path(scratch) / '.local' / 'bin'
    bin_dir.mkdir(parents=True)
    fake = bin_dir / 'rql'
    fake.write_text(FAKE_RQL)
    fake.chmod(0o755)
    return fake


class ImportStartupHooksTests(unittest.TestCase):
    def test_bash_startup_invites_import_under_every_listing_outcome(self):
        for harness in ('repoql', 'repoql-codex'):
            for mode, expected in EXPECTED.items():
                with self.subTest(harness=harness, mode=mode), tempfile.TemporaryDirectory() as scratch:
                    fake_rql(scratch)
                    result = subprocess.run(['bash', str(ROOT / 'plugins' / harness / 'scripts/session-start.sh')],
                        input=json.dumps({'cwd': scratch}), text=True, capture_output=True, cwd=scratch,
                        env={**os.environ, 'HOME': scratch, 'IMPORT_TEST_MODE': mode}, timeout=15)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    context = json.loads(result.stdout)['hookSpecificOutput']['additionalContext']
                    listing = context.split('## Imported Repositories', 1)[1].split('## Accessible Uplinks', 1)[0]
                    self.assertIn(expected, listing)
                    self.assertEqual(listing.count(INVITATION), 1)
                    self.assertNotIn('rql import', listing)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is unavailable')
    def test_powershell_startup_invites_import_under_every_listing_outcome(self):
        for mode, expected in EXPECTED.items():
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as scratch:
                fake = fake_rql(scratch)
                script = ROOT / 'plugins' / 'repoql-codex' / 'scripts/session-start.ps1'
                wrapper = '''function Get-Command {
                    param([string]$Name)
                    if ($Name -eq 'rql') { return [pscustomobject]@{Source=$env:IMPORT_TEST_COMMAND} }
                    Microsoft.PowerShell.Core\\Get-Command $Name
                }
                & $env:IMPORT_TEST_SCRIPT
                '''
                result = subprocess.run(['pwsh', '-NoProfile', '-Command', wrapper],
                    input=json.dumps({'cwd': scratch}), text=True, capture_output=True, cwd=scratch,
                    env={**os.environ, 'HOME': scratch, 'IMPORT_TEST_MODE': mode,
                        'IMPORT_TEST_COMMAND': str(fake), 'IMPORT_TEST_SCRIPT': str(script)}, timeout=15)
                self.assertEqual(result.returncode, 0, result.stderr)
                listing = result.stdout.split('## Imported Repositories', 1)[1].split('## Accessible Uplinks', 1)[0]
                self.assertIn(expected, listing)
                self.assertEqual(listing.count(INVITATION), 1)
                self.assertNotIn('rql import', listing)


if __name__ == '__main__':
    unittest.main()
