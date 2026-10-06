"""The MCP launcher and the bootstrap it shares with the SessionStart hook, on macOS and Linux.

Claude Code starts the bundled server from .mcp.json with the PATH it was launched with, in the session that
runs `/plugin install` and before any hook has finished. So the launcher must find rql off PATH, and with no rql
at all must hold the connection open while one download installs it, then hand the connection over.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest

from hook_support import BASH, ROOT

PLUGIN = ROOT / 'plugins' / 'repoql'
LAUNCHER = PLUGIN / 'scripts' / 'rql-mcp'
BOOTSTRAP = PLUGIN / 'scripts' / 'bootstrap-rql.sh'

# The server the launcher hands over to: it logs what it is sent and answers like an MCP server.
FAKE_RQL = '''
import json, os, sys
with open(os.path.join(os.environ['HOME'], 'rql-received.jsonl'), 'a') as log:
    log.write(json.dumps({'argv': sys.argv[1:]}) + '\\n')
    for line in sys.stdin:
        log.write(line)
        log.flush()
        message = json.loads(line)
        if message.get('method') == 'initialize':
            result = {'protocolVersion': '2025-03-26', 'capabilities': {'tools': {}},
                      'serverInfo': {'name': 'fake-rql', 'version': '1'}}
        elif message.get('method') == 'tools/list':
            result = {'tools': [{'name': 'explore', 'inputSchema': {'type': 'object'}}]}
        else:
            continue
        print(json.dumps({'jsonrpc': '2.0', 'id': message['id'], 'result': result}), flush=True)
'''

# Stands in for the hosted installer: after a delay, puts rql in the canonical directory the way it does.
INSTALLER = '''#!/bin/bash
echo "installer PATH=$PATH"
sleep "${FAKE_DOWNLOAD_SECONDS:-0}"
mkdir -p "$HOME/.local/bin"
cp "$FAKE_RQL" "$HOME/.local/bin/rql.download.$$"
chmod +x "$HOME/.local/bin/rql.download.$$"
mv -f "$HOME/.local/bin/rql.download.$$" "$HOME/.local/bin/rql"
'''

FAKE_CURL = '''#!/bin/bash
while [ $# -gt 0 ]; do
    [ "$1" = "-o" ] && out=$2
    shift
done
echo fetched >>"$HOME/curl-calls"
cp "$FAKE_INSTALLER" "$out"
'''

INITIALIZE = {'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
    'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'test', 'version': '1'}}}


@unittest.skipUnless(BASH, 'the launcher and bootstrap-rql.sh run on macOS and Linux')
class McpBootstrap(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.home = Path(tmp.name).resolve()
        tools = self.home / 'tools'
        tools.mkdir()
        # Kept off PATH: only an install puts it where the launcher looks.
        self.fake_rql = self.home / 'downloaded-rql'
        self.fake_rql.write_text(f'#!{sys.executable}\n{FAKE_RQL}')
        installer = tools / 'install-rql.sh'
        installer.write_text(INSTALLER)
        (tools / 'curl').write_text(FAKE_CURL)
        for tool in tools.iterdir():
            tool.chmod(0o755)
        # The PATH a session was launched with: no rql, and no ~/.local/bin.
        self.env = {'HOME': str(self.home), 'PATH': f'{tools}:/usr/bin:/bin', 'FAKE_RQL': str(self.fake_rql),
                    'FAKE_INSTALLER': str(installer), 'TMPDIR': str(self.home)}
        self.log = self.home / '.local' / 'state' / 'repoql' / 'bootstrap.log'

    def install(self):
        canonical = self.home / '.local' / 'bin'
        canonical.mkdir(parents=True)
        (canonical / 'rql').write_text(self.fake_rql.read_text())
        (canonical / 'rql').chmod(0o755)

    def launch(self, **extra):
        process = subprocess.Popen([str(LAUNCHER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True, env={**self.env, **extra}, cwd=self.home)
        self.addCleanup(process.kill)
        return process

    def exchange(self, process, message, seconds=10):
        """Send one message and return the next line the launcher writes, failing rather than hanging."""
        if message is not None:
            process.stdin.write(json.dumps(message) + '\n')
            process.stdin.flush()
        answer = []
        reader = threading.Thread(target=lambda: answer.append(process.stdout.readline()), daemon=True)
        reader.start()
        reader.join(seconds)
        self.assertTrue(answer and answer[0], f'no answer to {message} within {seconds}s')
        return json.loads(answer[0])

    def bootstrap(self, **extra):
        return subprocess.Popen([BASH, str(BOOTSTRAP)], env={**self.env, **extra}, cwd=self.home,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def await_rql(self, seconds=15):
        deadline = time.time() + seconds
        while time.time() < deadline and not (self.home / '.local' / 'bin' / 'rql').exists():
            time.sleep(0.2)
        return (self.home / '.local' / 'bin' / 'rql').exists()

    def test_plugin_starts_the_launcher_not_a_bare_rql(self):
        server = json.loads((PLUGIN / '.mcp.json').read_text())['mcpServers']['repoql']
        self.assertEqual(server['command'], '${CLAUDE_PLUGIN_ROOT}/scripts/rql-mcp')
        self.assertTrue(os.access(LAUNCHER, os.X_OK), 'Claude Code executes the launcher directly')
        self.assertTrue(LAUNCHER.with_suffix('.cmd').exists(), 'Windows resolves the command to the .cmd')

    def test_installed_rql_is_found_when_the_launch_path_lacks_it(self):
        self.install()
        process = self.launch()
        self.assertEqual(self.exchange(process, INITIALIZE)['result']['serverInfo']['name'], 'fake-rql')
        self.assertEqual(json.loads((self.home / 'rql-received.jsonl').read_text().splitlines()[0]), {'argv': ['mcp']})

    def test_download_inside_the_grace_period_leaves_the_handshake_to_rql(self):
        process = self.launch(FAKE_DOWNLOAD_SECONDS='1')
        self.assertEqual(self.exchange(process, INITIALIZE)['result']['serverInfo']['name'], 'fake-rql')
        received = (self.home / 'rql-received.jsonl').read_text().splitlines()
        self.assertEqual([json.loads(line) for line in received], [{'argv': ['mcp']}, INITIALIZE])

    def test_slow_download_is_answered_at_once_then_handed_to_rql(self):
        process = self.launch(FAKE_DOWNLOAD_SECONDS='3', REPOQL_MCP_GRACE='0')
        held = self.exchange(process, INITIALIZE, seconds=2)['result']
        self.assertEqual(held['protocolVersion'], '2025-06-18')
        self.assertEqual(held['capabilities'], {'tools': {'listChanged': True}})
        process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.assertEqual(self.exchange(process, {'jsonrpc': '2.0', 'id': 'a', 'method': 'tools/list'}),
                         {'jsonrpc': '2.0', 'id': 'a', 'result': {'tools': []}})

        # rql lands: the client hears only that the tool list changed, and the next list is rql's.
        self.assertEqual(self.exchange(process, None, seconds=20),
                         {'jsonrpc': '2.0', 'method': 'notifications/tools/list_changed'})
        listed = self.exchange(process, {'jsonrpc': '2.0', 'id': 3, 'method': 'tools/list'})
        self.assertEqual([tool['name'] for tool in listed['result']['tools']], ['explore'])

        received = [json.loads(line) for line in (self.home / 'rql-received.jsonl').read_text().splitlines()]
        self.assertEqual(received[0], {'argv': ['mcp']})
        self.assertEqual([message.get('method') for message in received[1:]],
                         ['initialize', 'notifications/initialized', 'tools/list'])
        self.assertEqual(received[1], INITIALIZE)

        process.stdin.close()
        self.assertEqual(process.wait(timeout=10), 0)

    def test_stopping_the_launcher_does_not_stop_the_download(self):
        process = self.launch(FAKE_DOWNLOAD_SECONDS='3', REPOQL_MCP_GRACE='0')
        self.exchange(process, INITIALIZE, seconds=2)
        deadline = time.time() + 5
        while time.time() < deadline and not self.log.exists():
            time.sleep(0.05)
        process.terminate()
        process.wait(timeout=5)
        self.assertFalse((self.home / '.local' / 'bin' / 'rql').exists())
        self.assertTrue(self.await_rql(), self.log.read_text())

    def test_disabled_bootstrap_fails_without_writing_to_the_protocol_stream(self):
        process = self.launch(REPOQL_NO_BOOTSTRAP='1')
        self.assertEqual(process.wait(timeout=10), 1)
        self.assertEqual(process.stdout.read(), '')
        self.assertIn('rql is unavailable', process.stderr.read())

    def test_concurrent_callers_share_one_download(self):
        callers = [self.bootstrap(FAKE_DOWNLOAD_SECONDS='2') for _ in range(3)]
        self.assertEqual([caller.wait(timeout=30) for caller in callers], [0, 0, 0])
        self.assertEqual((self.home / 'curl-calls').read_text().count('fetched'), 1)
        self.assertEqual(self.log.read_text().count('installing from downloads.repoql.ai'), 1)

    def test_a_caller_that_runs_out_of_time_reports_it_and_the_download_finishes(self):
        caller = self.bootstrap(FAKE_DOWNLOAD_SECONDS='4', REPOQL_BOOTSTRAP_WAIT='1')
        self.assertEqual(caller.wait(timeout=30), 2)
        self.assertTrue(self.await_rql(), self.log.read_text())

    def test_installer_sees_the_launch_path_so_it_can_add_the_canonical_directory(self):
        self.assertEqual(self.bootstrap().wait(timeout=30), 0)
        installer_path = next(line for line in self.log.read_text().splitlines() if line.startswith('installer PATH='))
        self.assertNotIn(str(self.home / '.local' / 'bin'), installer_path)

    def test_failed_install_exits_1_and_releases_the_lock(self):
        Path(self.env['FAKE_INSTALLER']).write_text('#!/bin/bash\nexit 1\n')
        self.assertEqual(self.bootstrap().wait(timeout=30), 1)
        self.assertIn('bootstrap failed', self.log.read_text())
        self.assertFalse((self.log.parent / 'bootstrap.lock').exists())


if __name__ == '__main__':
    unittest.main()
