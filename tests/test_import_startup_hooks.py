"""Verify the startup scripts list every workspace repository and import, invite the import tool, and name concept://."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
INVITATION = 'Use the import tool whenever you like to add more.'
LOCAL_IMPORT = 'local:///srv/vendor/lib'
CONCEPTS = 'addressable at concept://, including the concepts imported sources carry'
# The fake answers the listing query by mode; a legacy host rejects it and answers only the GitHub fallback.
FAKE_RQL = f'''#!/bin/sh
if [ "$1" = query ]; then
    case "$IMPORT_TEST_MODE:$2" in
        listed:*"kind IN"*)
            printf 'kind\\tline\\n'
            printf 'import\\tgithub://acme/widgets\\n'
            printf 'import\\t{LOCAL_IMPORT}\\n'
            printf 'workspace\\tgithub://acme/billing\\n'
            printf '[42 tok | 3 ms]\\n'
            exit 0 ;;
        legacy:*"kind IN"*) exit 1 ;;
        legacy:*) printf 'kind\\tline\\nimport\\tgithub://acme/widgets\\n'; exit 0 ;;
        none:*) printf 'kind\\tline\\n'; exit 0 ;;
        *) exit 1 ;;
    esac
fi
[ "$1" = uplinks ] && exit 1
exit 0
'''
IMPORTS = {
    'listed': ['github://acme/widgets', LOCAL_IMPORT],
    'legacy': ['github://acme/widgets'],
    'none': ['(none)'],
    'unreachable': ['(not checked'],
}


def fake_rql(scratch):
    # The bash hooks put $HOME/.local/bin first on PATH, so the fake lives there under a scratch HOME.
    bin_dir = Path(scratch) / '.local' / 'bin'
    bin_dir.mkdir(parents=True)
    fake = bin_dir / 'rql'
    fake.write_text(FAKE_RQL)
    fake.chmod(0o755)
    return fake


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

    def test_bash_startup_lists_every_import_kind_and_invites_more(self):
        for harness in ('repoql', 'repoql-codex'):
            for mode in IMPORTS:
                with self.subTest(harness=harness, mode=mode), tempfile.TemporaryDirectory() as scratch:
                    fake_rql(scratch)
                    result = subprocess.run(['bash', str(ROOT / 'plugins' / harness / 'scripts/session-start.sh')],
                        input=json.dumps({'cwd': scratch}), text=True, capture_output=True, cwd=scratch,
                        env={**os.environ, 'HOME': scratch, 'IMPORT_TEST_MODE': mode}, timeout=15)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assert_listing(json.loads(result.stdout)['hookSpecificOutput']['additionalContext'], mode)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is unavailable')
    def test_powershell_startup_lists_every_import_kind_and_invites_more(self):
        for harness in ('repoql', 'repoql-codex'):
            for mode in IMPORTS:
                with self.subTest(harness=harness, mode=mode), tempfile.TemporaryDirectory() as scratch:
                    fake = fake_rql(scratch)
                    wrapper = '''function Get-Command {
                        param([string]$Name)
                        if ($Name -eq 'rql') { return [pscustomobject]@{Source=$env:IMPORT_TEST_COMMAND} }
                        Microsoft.PowerShell.Core\\Get-Command $Name
                    }
                    function rql { & $env:IMPORT_TEST_COMMAND @args }
                    & $env:IMPORT_TEST_SCRIPT
                    '''
                    result = subprocess.run(['pwsh', '-NoProfile', '-Command', wrapper],
                        input=json.dumps({'cwd': scratch}), text=True, capture_output=True, cwd=scratch,
                        env={**os.environ, 'HOME': scratch, 'IMPORT_TEST_MODE': mode,
                            'IMPORT_TEST_COMMAND': str(fake),
                            'IMPORT_TEST_SCRIPT': str(ROOT / 'plugins' / harness / 'scripts/session-start.ps1')},
                        timeout=20)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assert_listing(result.stdout, mode)


if __name__ == '__main__':
    unittest.main()
