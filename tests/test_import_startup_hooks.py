"""Verify the startup scripts list every mounted source the agent was not already told about, count the concepts and vocab words each carries, invite the import tool, and name concept://."""
import json
from pathlib import Path
import re
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, POWERSHELL_FILE, ROOT, argv, context_of, hook_env, install_fake, run

INVITATION = 'Use the import tool whenever you like to add more.'
CONCEPTS = 'concept:///** holds the concepts of this repository and its imports.'
HARNESSES = ('repoql', 'repoql-codex', 'repoql-cursor')
# The fake runs the hook's own SQL over a mount table, so the cases below hold the query's filter and not only
# the rendering. SQLite stands in for DuckDB; the two functions it lacks are registered with DuckDB's meaning.
# A legacy host has no Filesystems.kind, so it rejects the listing query and answers only the roster. A busy host,
# still starting or sweeping, lets the listing pass its deadline and answers the roster; a silent one answers neither.
# An unreachable host is one rql's probe found no host listening for, and rql says so in those words.
FAKE_RQL = r'''
import os, sqlite3, sys
args = sys.argv[1:]
mode = os.environ['IMPORT_TEST_MODE']
if args[:1] != ['query']:
    sys.exit(1 if args[:1] == ['uplinks'] else 0)
if mode == 'unreachable':
    print('No host is running for this repository, and --no-launch suppressed starting one.', file=sys.stderr)
    sys.exit(1)
if mode == 'silent' or (mode == 'busy' and args[1].startswith('WITH repos')):
    print('Query exceeded the 5000ms deadline. Narrow the scope, or raise --timeout-ms.', file=sys.stderr)
    sys.exit(1)
MOUNTS = [
    ('primary', 'file', None, '', 'file:///'),
    (None, 'help', None, None, 'help:///'),
    (None, 'concept', None, None, 'concept:///'),
    (None, 'concept', None, 'local/srv/vendor/lib', 'concept:///local/srv/vendor/lib'),
    (None, 'vocabulary', None, None, 'vocabulary:///'),
    ('worktree', 'worktree', 'feature', '', 'worktree://feature'),
    ('workspace', 'github', 'acme', 'billing', 'github://acme/billing'),
    ('import', 'github', 'acme', 'widgets', 'github://acme/widgets'),
    ('import', 'local', None, 'srv/vendor/lib', 'local:///srv/vendor/lib'),
    ('import', 's3', 'acme-logs', 'app', 's3://acme-logs/app/'),
    ('connector', 'workos', 'prod', '', 'workos://prod'),
    ('hologram', 'future', 'deck', '', 'future://deck'),
    (None, 'nfs', 'share', '', 'nfs://share'),
]
FILES = [
    ('concept:///local/srv/vendor/lib/rule/One.md', '.md', 'One.md'),
    ('concept:///local/srv/vendor/lib/rule/Two.md', '.md', 'Two.md'),
    ('concept:///local/srv/vendor/lib/README.md', '.md', 'README.md'),
    ('vocabulary:///local/srv/vendor/lib/widget', '', 'widget'),
    ('concept:///rule/Mine.md', '.md', 'Mine.md'),
]
db = sqlite3.connect(':memory:')
db.create_function('starts_with', 2, lambda text, prefix: text is not None and prefix is not None and text.startswith(prefix))
db.create_function('concat_ws', -1, lambda separator, *parts: separator.join(str(part) for part in parts if part is not None))
db.execute('CREATE TABLE Files (uri, extension, name)')
db.executemany('INSERT INTO Files VALUES (?, ?, ?)', FILES)
if mode == 'legacy':
    db.execute('CREATE TABLE Filesystems (scheme, authority, path_prefix, source_uri)')
    db.executemany('INSERT INTO Filesystems VALUES (?, ?, ?, ?)', [mount[1:] for mount in MOUNTS])
else:
    db.execute('CREATE TABLE Filesystems (kind, scheme, authority, path_prefix, source_uri)')
    db.executemany('INSERT INTO Filesystems VALUES (?, ?, ?, ?, ?)', MOUNTS if mode in ('listed', 'busy') else MOUNTS[:6])
try:
    rows = db.execute(args[1]).fetchall()
except sqlite3.Error as error:
    print(error, file=sys.stderr)
    sys.exit(1)
print('kind\tline')
for row in rows:
    print('\t'.join(row))
print('[42 tok | 3 ms]')
'''
LISTED = [
    'github://acme/widgets',
    'local:///srv/vendor/lib (2 concepts, 1 vocab word)',
    's3://acme-logs/app/',
    'workos://prod (connector)',
    # A mount kind and a scheme this plugin has never heard of are announced, not dropped.
    'future://deck (hologram)',
    'nfs://share',
]
# Without the counted listing every source is still named, the workspace's repositories among them, bare.
ROSTERED = [
    'github://acme/billing',
    'github://acme/widgets',
    'local:///srv/vendor/lib',
    's3://acme-logs/app/',
    'workos://prod',
    'future://deck',
    'nfs://share',
]
BUSY = 'the RepoQL host is still starting or indexing and did not answer the full listing in time'
REFUSED = 'the RepoQL host did not answer the full listing'
UNCOUNTED = '(Kinds and concept and vocab word counts are missing: '
IMPORTS = {
    'listed': LISTED,
    'legacy': [UNCOUNTED + REFUSED + '.)'],
    'busy': [UNCOUNTED + BUSY + '.)'],
    'none': ['(none)'],
    'unreachable': ['(not checked'],
    'silent': ['(not listed', BUSY, 'query Filesystems for them'],
}
# What the agent already knows, or mounted for another checkout: never worth a line at startup.
UNANNOUNCED = ('file:///', 'help:///', 'concept:///', 'vocabulary:///', 'worktree://')


def context_from(result, harness):
    if harness == 'repoql-cursor':
        return json.loads(result.stdout)['additional_context']
    return context_of(result, 'SessionStart')


class ImportStartupHooksTests(unittest.TestCase):
    def assert_listing(self, context, mode):
        before_imports, after_heading = context.split('## Imported Repositories', 1)
        imports = after_heading.split('## Accessible Uplinks', 1)[0]
        for line in IMPORTS[mode]:
            self.assertIn(line, imports)
        for uri in UNANNOUNCED:
            self.assertNotIn(uri, imports)
        self.assertEqual(imports.count(INVITATION), 1)
        self.assertNotIn('rql import', imports)
        self.assertNotIn('(memory:', context)
        self.assertEqual(context.count(CONCEPTS), 1)
        if mode in ('legacy', 'busy'):
            self.assertEqual([line for line in imports.splitlines() if '://' in line], sorted(ROSTERED))
        else:
            self.assertNotIn(UNCOUNTED, imports)
        if mode != 'unreachable':
            self.assertNotIn('not running', imports)
        if mode == 'listed':
            self.assertIn('Use these URIs directly', imports)
            self.assertEqual([line for line in imports.splitlines() if '://' in line], sorted(LISTED))
            workspace = before_imports.split('## Workspace Repositories', 1)[1]
            self.assertIn('This workspace is a directory of repositories.', workspace)
            self.assertIn('github://acme/billing', workspace)
        else:
            self.assertNotIn('## Workspace Repositories', context)

    def test_startup_lists_every_mounted_source_and_invites_more(self):
        for implementation in IMPLEMENTATIONS:
            for harness in HARNESSES:
                for mode in IMPORTS:
                    with self.subTest(implementation=implementation, harness=harness, mode=mode), \
                            tempfile.TemporaryDirectory() as scratch:
                        # The bash hooks put $HOME/.local/bin first on PATH, so the fake lives there under a scratch HOME.
                        bin_dir = Path(scratch) / '.local' / 'bin'
                        install_fake(bin_dir, 'rql', FAKE_RQL)
                        result = run(argv(implementation, harness, 'session-start'), {'cwd': scratch},
                                     hook_env(bin_dir, HOME=scratch, IMPORT_TEST_MODE=mode), scratch, timeout=30)
                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assert_listing(context_from(result, harness), mode)

    def test_every_script_sends_the_same_listing_query(self):
        queries = {re.search(r'WITH repos AS.*?ORDER BY section, source_uri',
                             (ROOT / 'plugins' / harness / 'scripts' / f'session-start.{extension}').read_text(encoding='utf-8')).group(0)
                   for harness in HARNESSES for extension in ('sh', 'ps1')}
        self.assertEqual(len(queries), 1)

    @unittest.skipUnless('ps1' in IMPLEMENTATIONS, 'PowerShell is not installed')
    def test_powershell_error_records_keep_the_failure_reason(self):
        # Windows PowerShell 5.1 turns redirected native stderr into error records. Emit those
        # records directly so newer PowerShell also catches diagnostics lost to SilentlyContinue.
        errors = {
            'unreachable': 'No host is running for this repository, and --no-launch suppressed starting one.',
            'silent': 'Query exceeded the 5000ms deadline. Narrow the scope, or raise --timeout-ms.',
        }
        for harness in HARNESSES:
            for mode, message in errors.items():
                with self.subTest(harness=harness, mode=mode), tempfile.TemporaryDirectory() as scratch:
                    wrapper = Path(scratch) / 'error-records.ps1'
                    wrapper.write_text('''param([string]$Hook)
function rql {
    $global:LASTEXITCODE = 1
    Write-Error $env:IMPORT_TEST_ERROR
}
& $Hook
''', encoding='ascii')
                    result = run([*POWERSHELL_FILE, str(wrapper), argv('ps1', harness, 'session-start')[-1]],
                                 {'cwd': scratch}, hook_env(Path(scratch), IMPORT_TEST_ERROR=message), scratch)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stderr, '')
                    self.assert_listing(context_from(result, harness), mode)


if __name__ == '__main__':
    unittest.main()
