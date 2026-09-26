#!/usr/bin/env node
// Live smoke test: drive every plugin tool path against a real rql host,
// without OpenClaw. Registers the built plugin (dist/) against a stub plugin
// API, then calls each tool the way OpenClaw would.
//
//   npm run build && npm run smoke -- --workspace <git repository>
//
// Launches a host for the workspace if none is running (autoStart). Exits
// non-zero when any call does not behave as expected.
import { resolve } from "path";

const argv = process.argv.slice(2);
const workspaceFlag = argv.indexOf("--workspace");
const workspace = resolve(workspaceFlag >= 0 ? argv[workspaceFlag + 1] : process.cwd());

const plugin = (await import(new URL("../dist/index.js", import.meta.url))).default;
const factories = new Map();
const services = [];
plugin.register({
  logger: { info: (m) => console.log(`  · ${m}`), warn: (m) => console.log(`  ! ${m}`), error: (m) => console.log(`  ✗ ${m}`) },
  pluginConfig: {},
  registerTool: (factory, opts) => factories.set(opts.name, factory),
  registerService: (service) => services.push(service),
});

const ctx = { workspaceDir: workspace, sessionId: "repoql-smoke" };
let failures = 0;

async function expect(name, params, outcome, pattern) {
  const tool = factories.get(name)?.(ctx);
  if (!tool) {
    console.log(`✗ ${name}: not offered`);
    failures++;
    return;
  }
  let text;
  let ok;
  try {
    const result = await tool.execute("smoke", params);
    text = result.content.map((b) => b.text).join("\n");
    ok = true;
  } catch (err) {
    text = err.message;
    ok = false;
  }
  const passed = (outcome === "ok") === ok && (!pattern || pattern.test(text));
  if (!passed) failures++;
  console.log(`${passed ? "✓" : "✗"} ${name} ${JSON.stringify(params)} → ${ok ? "ok" : "error"}`);
  if (!passed) console.log(text.split("\n").map((l) => `    ${l}`).join("\n"));
}

console.log(`workspace: ${workspace}`);
console.log(`registered: ${[...factories.keys()].join(", ")}`);

await expect("repoql_status", {}, "ok", /RepoQL host is running/);
await expect("repoql_query", { sql: "SELECT 42 AS answer" }, "ok", /42/);
await expect("repoql_query", { sql: "DROP TABLE Files" }, "error", /read-only/);
await expect("repoql_read", { uriGlob: "file:///** => tree: folders", tokenBudget: 500 }, "ok");
await expect("repoql_explore", { keywords: "readme", uriGlob: "file:///**" }, "ok");
await expect("repoql_discover_vocabulary", { keywords: "entry point" }, "ok");
await expect("repoql_execute", { code: "(() => repoql.query('SELECT 1 AS one'))()" }, "ok", /one/);
await expect("repoql_command", { command: "config list" }, "ok");
await expect("repoql_command", { command: "help" }, "ok", /Run beside this client/);
await expect("repoql_command", { command: "host status" }, "ok", /Host: \w+ — rql/);
await expect("repoql_command", { command: "no-such-command" }, "error", /Unknown command/);

// After first contact every tool comes from the live host catalog.
const live = [...factories.entries()].filter(([name]) => name !== "repoql_status" && factories.get(name)(ctx)).length;
console.log(`live catalog: ${live} of ${factories.size - 1} declared tools offered by the host`);

for (const service of services) await service.stop?.();
console.log(failures ? `\n${failures} failure(s)` : "\nall passed");
process.exit(failures ? 1 : 0);
