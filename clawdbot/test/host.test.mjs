// A host that owns its socket but does not answer — indexing hard, or stuck — must be
// connected to, never launched beside. Run with `npm test`.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { resolvePluginConfig } from "../dist/src/config.js";
import { RqlHostManager, probe } from "../dist/src/runtime/host.js";

const logger = { info() {}, warn() {}, error() {}, debug() {} };

/** A workspace whose socket accepts connections and then never says a word. */
async function silentHost() {
  const repoRoot = mkdtempSync(join(tmpdir(), "rq-busy-"));
  mkdirSync(join(repoRoot, ".repoql", "cache"), { recursive: true });
  const sockets = new Set();
  const server = createServer((socket) => sockets.add(socket));
  await new Promise((resolveListen) => server.listen(join(repoRoot, ".repoql", "cache", "repoql.sock"), resolveListen));
  const close = () => {
    for (const socket of sockets) socket.destroy();
    return new Promise((resolveClose) => server.close(resolveClose));
  };
  return { repoRoot, close };
}

test("a socket nobody listens on is absent", async () => {
  const repoRoot = mkdtempSync(join(tmpdir(), "rq-absent-"));
  assert.deepEqual(await probe(join(repoRoot, "repoql.sock")), { kind: "absent" });
});

test("a socket that accepts but never answers is busy, not absent", async () => {
  const host = await silentHost();
  try {
    assert.deepEqual(await probe(join(host.repoRoot, ".repoql", "cache", "repoql.sock")), { kind: "busy" });
  } finally {
    await host.close();
  }
});

test("a busy host is connected to, never launched beside", async () => {
  const host = await silentHost();
  // A launch would fail loudly: this executable does not exist.
  const config = resolvePluginConfig({ rqlPath: join(host.repoRoot, "no-such-rql"), autoStart: true, repoRoot: host.repoRoot });
  const manager = new RqlHostManager({ config, logger, workspaceDir: host.repoRoot });
  try {
    assert.equal(await manager.presence(), "busy");
    const client = await manager.getClient();
    assert.ok(client, "getClient resolves to a client for the host that owns the socket");
  } finally {
    manager.dispose();
    await host.close();
  }
});
