"""Verify the startup scripts say truthfully why rql is missing: another session is installing it, or the install could not run.

The hook runs on a PATH that holds neither rql nor a working curl, so no case here can download anything.
"""
import json
import os
from pathlib import Path
import shutil
import tempfile
import threading
import unittest

from hook_support import BASH, IMPLEMENTATIONS, WINDOWS, argv, context_of, install_fake, run

HARNESSES = ('repoql', 'repoql-codex', 'repoql-cursor')
# What the bash hooks call; curl is left out so each case decides whether it exists.
TOOLS = ('awk', 'basename', 'bash', 'cat', 'cut', 'date', 'dirname', 'env', 'find', 'grep', 'head', 'mkdir', 'mktemp',
         'pkill', 'python3', 'rm', 'rmdir', 'sed', 'sh', 'sleep', 'sort', 'tail', 'tr', 'uname', 'wc')
IN_PROGRESS = 'another session is installing the rql binary right now'
# The Claude Code plugin's MCP launcher installs too, so an install still running there may be this session's own.
IN_PROGRESS_BY = {'repoql': 'the rql binary is still downloading in the background'}
FAILED = 'automatic install'
FAKE_RQL = 'import sys\nsys.exit(1)\n'


class BootstrapOutcomeTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.home = Path(scratch.name).resolve()
        self.bin_dir = self.home / '.local' / 'bin'
        self.bin_dir.mkdir(parents=True)
        self.tools = self.home / 'tools'
        self.tools.mkdir()
        if not WINDOWS:
            for tool in TOOLS:
                if shutil.which(tool):
                    (self.tools / tool).symlink_to(shutil.which(tool))
        self.state = self.home / 'state'

    def env(self, **extra):
        system = [self.tools]
        if WINDOWS:
            system32 = Path(os.environ['SystemRoot']) / 'System32'
            system = [system32, system32 / 'WindowsPowerShell' / 'v1.0']
        env = {**os.environ, 'PATH': os.pathsep.join(map(str, [self.bin_dir, *system])), 'PYTHONUTF8': '1',
               'HOME': str(self.home), 'LOCALAPPDATA': str(self.home / 'localappdata'),
               'REPOQL_STATE_DIR': str(self.state), 'CURSOR_PROJECT_DIR': str(self.home), **extra}
        for inherited in ('PLUGIN_DATA', 'CLAUDE_PLUGIN_DATA', 'REPOQL_NO_BOOTSTRAP'):
            env.pop(inherited, None)
        return env

    def with_curl(self):
        # Present, so the bash bootstrap gets as far as the lock, and unable to fetch an installer.
        install_fake(self.tools, 'curl', 'import sys\nsys.exit(22)\n')

    def start(self, implementation, harness, **extra):
        result = run(argv(implementation, harness, 'session-start'), {'cwd': str(self.home)}, self.env(**extra),
                     self.home, timeout=60)
        # Whatever the bootstrap found, the hook exits 0 and answers JSON.
        self.assertEqual(result.returncode, 0, result.stderr)
        if harness == 'repoql-cursor':
            return json.loads(result.stdout)['additional_context']
        # What the Claude Code plugin tells the user, beside what it tells the model.
        self.notice = json.loads(result.stdout).get('systemMessage')
        return context_of(result, 'SessionStart')

    def installed_in(self, implementation):
        # bash shortens the home directory the way the installer prints it; PowerShell names the directory in full.
        return '~/.local/bin' if implementation == 'sh' else str(self.bin_dir)

    def each(self):
        for implementation in IMPLEMENTATIONS:
            for harness in HARNESSES:
                shutil.rmtree(self.state, ignore_errors=True)
                with self.subTest(implementation=implementation, harness=harness):
                    yield implementation, harness

    def test_an_install_in_another_session_is_not_reported_as_a_failure(self):
        self.with_curl()
        for implementation, harness in self.each():
            (self.state / 'bootstrap.lock').mkdir(parents=True)
            context = self.start(implementation, harness, REPOQL_BOOTSTRAP_WAIT='0')
            self.assertIn('# RepoQL: host install in progress', context)
            if harness == 'repoql':
                self.assertIn('RepoQL is still downloading rql', self.notice)
            self.assertIn(IN_PROGRESS_BY.get(harness, IN_PROGRESS), context)
            self.assertNotIn(FAILED, context)
            self.assertNotIn('manually', context)
            self.assertNotIn('downloads.repoql.ai', context)
            # The lock belongs to the other session.
            self.assertTrue((self.state / 'bootstrap.lock').is_dir())

    def test_the_hook_waits_for_the_other_session_and_uses_its_install(self):
        self.with_curl()
        for implementation, harness in self.each():
            lock = self.state / 'bootstrap.lock'
            lock.mkdir(parents=True)

            def finish_install():
                install_fake(self.bin_dir, 'rql', FAKE_RQL)
                lock.rmdir()

            other_session = threading.Timer(1.5, finish_install)
            other_session.start()
            try:
                context = self.start(implementation, harness, REPOQL_BOOTSTRAP_WAIT='30')
            finally:
                other_session.join()
                shutil.rmtree(self.bin_dir)
                self.bin_dir.mkdir()
            self.assertIn('# RepoQL: Repository Orientation', context)
            self.assertIn('rql was just installed', context)
            if harness == 'repoql':
                self.assertEqual(self.notice, f'RepoQL installed rql to {self.installed_in(implementation)}.')
            self.assertFalse(lock.exists())

    def test_a_state_directory_that_cannot_be_created_is_named_instead_of_a_log(self):
        self.with_curl()
        (self.home / 'blocker').write_text('a file where the state directory would go')
        state = self.home / 'blocker' / 'state'
        for implementation, harness in self.each():
            context = self.start(implementation, harness, REPOQL_STATE_DIR=str(state))
            self.assertIn('# RepoQL: host not installed', context)
            self.assertIn(f'automatic install could not run: cannot create the state directory {state}', context)
            if harness == 'repoql':
                self.assertIn(f'RepoQL could not install rql: automatic install could not run: cannot create the state directory {state}. Install it manually', self.notice)
            self.assertNotIn('bootstrap.log', context)
            self.assertNotIn(IN_PROGRESS, context)

    @unittest.skipUnless(BASH, 'the bash bootstrap is the one that needs curl')
    def test_a_missing_curl_is_named_instead_of_a_log(self):
        for harness in HARNESSES:
            with self.subTest(harness=harness):
                context = self.start('sh', harness)
                self.assertIn('automatic install could not run: curl not found on PATH', context)
                self.assertNotIn('bootstrap.log', context)
                self.assertFalse(self.state.exists())

    # The PowerShell bootstrap has no tool to fake: its failed install would be a real one.
    @unittest.skipUnless(BASH, 'only the bash bootstrap can be made to fail without a download')
    def test_a_failed_install_cites_the_log_it_wrote(self):
        self.with_curl()
        for harness in HARNESSES:
            shutil.rmtree(self.state, ignore_errors=True)
            with self.subTest(harness=harness):
                context = self.start('sh', harness)
                log = self.state / 'bootstrap.log'
                self.assertIn(f'automatic install failed (log: {log})', context)
                self.assertIn('bootstrap failed', log.read_text(encoding='utf-8'))
                self.assertFalse((self.state / 'bootstrap.lock').exists())


if __name__ == '__main__':
    unittest.main()
