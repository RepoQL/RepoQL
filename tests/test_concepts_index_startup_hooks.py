"""Verify the Claude Code startup hook hands over the concepts index only when CLAUDE.md does not already import it."""
import os
from pathlib import Path
import tempfile
import unittest

from hook_support import IMPLEMENTATIONS, WINDOWS, argv, context_of, hook_env, install_fake, run

IMPORT = '@.repoql/concepts/README.md'
POINTER = 'concept:///** holds the concepts of this repository and its imports.'
# A case-insensitive file system answers the bash hook's first candidate, readme.md.
INDEX = '## Repository Concepts Index (.repoql/concepts/readme.md)\n\n# Concepts\n- Rule \u2014 invariant'
FAKE_RQL = '''
import sys
args = sys.argv[1:]
if args[:1] == ['query']:
    print('kind\\tline')
sys.exit(1 if args[:1] == ['uplinks'] else 0)
'''
# CLAUDE.md texts whose import Claude Code follows, as ConceptClaudeMdInjector.ContainsImportLine reads them.
IMPORTING = {
    'alone': IMPORT + '\n',
    'among other lines': '# Project\n\n' + IMPORT + '\n\nBuild with make.\n',
    'crlf': '# Project\r\n\r\n' + IMPORT + '\r\n',
    'indented with trailing spaces': '# Project\n  \t' + IMPORT + '  \n',
    'last line without a line break': '# Project\n' + IMPORT,
    'lowercase readme': '@.repoql/concepts/readme.md\n',
}
# CLAUDE.md texts that import nothing: the hook is then the only source of the index.
NOT_IMPORTING = {
    'no import': '# Project\n\nBuild with make.\n',
    'empty': '',
    'import inside a sentence': 'See ' + IMPORT + ' for the rules.\n',
    'another file': '@.repoql/concepts/README.md.bak\n@docs/.repoql/concepts/README.md\n',
}


class ConceptsIndexStartupHooksTests(unittest.TestCase):
    def start(self, implementation, claude_md, rql=True):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        root = Path(scratch.name)
        concepts = root / '.repoql' / 'concepts'
        concepts.mkdir(parents=True)
        # Bytes, so Windows does not turn the line breaks into CRLF.
        (concepts / 'README.md').write_bytes('# Concepts\n- Rule \u2014 invariant\n'.encode('utf-8'))
        if claude_md is not None:
            # Bytes, so a CRLF text reaches the hook as written.
            (root / 'CLAUDE.md').write_bytes(claude_md.encode('utf-8'))
        # The bash hooks put $HOME/.local/bin first on PATH, so the fake lives there under a scratch HOME.
        bin_dir = root / '.local' / 'bin'
        env = hook_env(bin_dir, HOME=str(root))
        if rql:
            install_fake(bin_dir, 'rql', FAKE_RQL)
        else:
            # A scratch PATH that still starts the interpreters but finds no rql.
            env['PATH'] = os.path.join(os.environ['SystemRoot'], 'System32') if WINDOWS else '/usr/bin:/bin'
        result = run(argv(implementation, 'repoql', 'session-start'), {'cwd': str(root)}, env, str(root), timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def test_claude_md_import_carries_the_index_so_the_hook_leaves_it_out(self):
        for implementation in IMPLEMENTATIONS:
            for name, claude_md in IMPORTING.items():
                with self.subTest(implementation=implementation, claude_md=name):
                    context = context_of(self.start(implementation, claude_md), 'SessionStart')
                    self.assertNotIn('Repository Concepts Index', context)
                    self.assertNotIn('invariant', context)
                    self.assertEqual(context.count(POINTER), 1)

    def test_without_the_import_the_hook_hands_over_the_index(self):
        for implementation in IMPLEMENTATIONS:
            for name, claude_md in {'no CLAUDE.md': None, **NOT_IMPORTING}.items():
                with self.subTest(implementation=implementation, claude_md=name):
                    context = context_of(self.start(implementation, claude_md), 'SessionStart')
                    self.assertIn(INDEX, context.replace('README.md', 'readme.md'))
                    self.assertEqual(context.count(POINTER), 1)

    def test_without_rql_the_index_still_follows_the_import(self):
        # REPOQL_NO_BOOTSTRAP=1 (from hook_env) keeps the bootstrap from downloading anything.
        for implementation in IMPLEMENTATIONS:
            with self.subTest(implementation=implementation):
                context = context_of(self.start(implementation, None, rql=False), 'SessionStart')
                self.assertEqual(context.replace('README.md', 'readme.md').strip(), INDEX)
                self.assertEqual(self.start(implementation, IMPORT + '\n', rql=False).stdout, '')


if __name__ == '__main__':
    unittest.main()
