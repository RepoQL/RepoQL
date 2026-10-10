"""Verify the startup scripts list every mounted source the agent was not already told about, count the concepts and vocab words each carries, invite the import tool, and name concept://."""
import json
from pathlib import Path
import re
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, ROOT, argv, context_of, hook_env, install_fake, run

INVITATION = 'Use the import tool whenever you like to add more.'
CONCEPTS = 'concept:///** holds the concepts of this repository and its imports.'
HARNESSES = ('repoql', 'repoql-codex', 'repoql-cursor')
# The fake runs the hook's own SQL over a mount table, so the cases below hold the query's filter and not only
# the rendering. SQLite stands in for DuckDB; the two functions it lacks are registered with DuckDB's meaning.
# A legacy host has no Filesystems.kind, so it rejects the listing query and answers only the GitHub fallback.
FAKE_RQL = r'''
import os, sqlite3, sys
args = sys.argv[1:]
mode = os.environ['IMPORT_TEST_MODE']
if args[:1] != ['query']:
    sys.exit(1 if args[:1] == ['uplinks'] else 0)
if mode == 'unreachable':
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
    db.executemany('INSERT INTO Filesystems VALUES (?, ?, ?, ?, ?)', MOUNTS if mode == 'listed' else MOUNTS[:6])
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
IMPORTS = {
    'listed': LISTED,
    'legacy': ['github://acme/billing\ngithub://acme/widgets\n' + INVITATION],
    'none': ['(none)'],
    'unreachable': ['(not checked'],
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


if __name__ == '__main__':
    unittest.main()
