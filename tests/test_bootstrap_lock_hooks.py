"""The Codex and Cursor bootstraps download rql once per machine, even where mkdir is not a mutex.

Each takes $state_dir/bootstrap.lock, the directory every plugin's bootstrap knows. On Ubuntu 26.04 the default
mkdir is uutils, which checks and then creates, so two callers racing for a missing lock can both "create" it.
The owner record bootstrap.pid, created with O_EXCL, decides which of them holds the lock.
"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from hook_support import BASH, ROOT

HARNESSES = ('repoql-codex', 'repoql-cursor')

# The check-then-create mkdir of uutils: it refuses a directory that already exists, but two callers that both
# look before either creates both succeed. The pause makes that race certain.
RACY_MKDIR = '''#!/bin/bash
case "$1" in -*) exec /bin/mkdir "$@" ;; esac
[ -e "$1" ] && { echo "mkdir: $1: File exists" >&2; exit 1; }
sleep 0.5
/bin/mkdir -p "$1"
'''

# Stands in for curl fetching the hosted installer: counts each fetch and prints an installer that takes a while
# and then puts rql in the canonical directory the way the real one does.
FAKE_CURL = '''#!/bin/bash
echo fetched >>"$HOME/curl-calls"
cat <<'INSTALLER'
sleep "${FAKE_DOWNLOAD_SECONDS:-0}"
mkdir -p "$HOME/.local/bin"
printf '#!/bin/sh\\nexit 0\\n' >"$HOME/.local/bin/rql.download.$$"
chmod +x "$HOME/.local/bin/rql.download.$$"
mv -f "$HOME/.local/bin/rql.download.$$" "$HOME/.local/bin/rql"
INSTALLER
'''


@unittest.skipUnless(BASH, 'bootstrap-rql.sh runs on macOS and Linux')
class BootstrapLock(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.home = Path(tmp.name).resolve()
        self.tools = self.home / 'tools'
        self.tools.mkdir()
        (self.tools / 'curl').write_text(FAKE_CURL)
        (self.tools / 'curl').chmod(0o755)
        self.state = self.home / 'state'
        # The system directories only, so an rql installed on this machine cannot end the bootstrap early.
        self.env = {**os.environ, 'HOME': str(self.home), 'REPOQL_STATE_DIR': str(self.state),
                    'PATH': os.pathsep.join([str(self.tools), '/usr/bin', '/bin']), 'REPOQL_BOOTSTRAP_WAIT': '30'}
        self.env.pop('REPOQL_NO_BOOTSTRAP', None)

    def bootstrap(self, harness, **extra):
        script = ROOT / 'plugins' / harness / 'scripts' / 'bootstrap-rql.sh'
        return subprocess.Popen([BASH, str(script)], env={**self.env, **extra}, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def reset(self):
        for path in (self.home / 'curl-calls', self.home / '.local' / 'bin' / 'rql'):
            path.unlink(missing_ok=True)
        subprocess.run(['rm', '-rf', str(self.state)], check=True)

    def test_racing_callers_share_one_download_where_mkdir_checks_then_creates(self):
        (self.tools / 'mkdir').write_text(RACY_MKDIR)
        (self.tools / 'mkdir').chmod(0o755)
        for harness in HARNESSES:
            self.reset()
            with self.subTest(harness=harness):
                callers = [self.bootstrap(harness, FAKE_DOWNLOAD_SECONDS='2') for _ in range(3)]
                outputs = [caller.communicate(timeout=60)[0] for caller in callers]
                self.assertEqual([caller.returncode for caller in callers], [0, 0, 0], outputs)
                self.assertEqual((self.home / 'curl-calls').read_text().count('fetched'), 1)
                self.assertFalse((self.state / 'bootstrap.lock').exists())
                self.assertFalse((self.state / 'bootstrap.pid').exists())

    def test_the_holder_records_its_pid_so_other_plugins_can_tell_it_is_alive(self):
        for harness in HARNESSES:
            self.reset()
            with self.subTest(harness=harness):
                caller = self.bootstrap(harness, FAKE_DOWNLOAD_SECONDS='2')
                owner = self.state / 'bootstrap.pid'
                for _ in range(100):
                    if owner.exists() and owner.read_text().strip():
                        break
                    subprocess.run(['sleep', '0.05'])
                self.assertEqual(owner.read_text().strip(), str(caller.pid))
                output = caller.communicate(timeout=60)[0]
                self.assertEqual(caller.returncode, 0, output)
                self.assertFalse(owner.exists())

    def test_a_stale_lock_is_reclaimed_with_its_owner_record(self):
        for harness in HARNESSES:
            self.reset()
            with self.subTest(harness=harness):
                # A holder killed mid-download long ago left both halves of the lock behind.
                lock = self.state / 'bootstrap.lock'
                lock.mkdir(parents=True)
                (self.state / 'bootstrap.pid').write_text('999999\n')
                hour_ago = lock.stat().st_mtime - 3600
                os.utime(lock, (hour_ago, hour_ago))
                caller = self.bootstrap(harness)
                output = caller.communicate(timeout=60)[0]
                self.assertEqual(caller.returncode, 0, output)
                self.assertEqual((self.home / 'curl-calls').read_text().count('fetched'), 1)
                self.assertFalse(lock.exists())
                self.assertFalse((self.state / 'bootstrap.pid').exists())


if __name__ == '__main__':
    unittest.main()
