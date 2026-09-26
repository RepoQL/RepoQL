#!/usr/bin/env node
// Bring the plugin up to date with rql.
//
//   npm run sync -- [--core <RepoQL.Core checkout>] [--workspace <dir>]
//
// 1. --core: re-vendor the wire contract (repoql.host.v1, repoql.tools.v1)
//    from RepoQL.Core's src/L3/RepoQL.Hosting.Contracts/Protos.
// 2. Snapshot the tool catalog (ToolCatalog.DescribeTools) from the rql host
//    serving --workspace (default: the current directory) into
//    src/catalog.json — the descriptions OpenClaw shows before a host is up.
// 3. Rewrite openclaw.plugin.json contracts.tools to match, because OpenClaw
//    refuses runtime tool registrations the manifest does not declare.
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "fs";
import { dirname, join, resolve } from "path";
import { fileURLToPath } from "url";
import * as grpc from "@grpc/grpc-js";
import * as protoLoader from "@grpc/proto-loader";

const PLUGIN_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const PROTO_ROOT = join(PLUGIN_ROOT, "src", "proto");
const PROTOS = ["repoql/host/v1/host.proto", "repoql/tools/v1/tools.proto"];
const TOOL_PREFIX = "repoql_";
const LOCAL_TOOLS = ["repoql_status"];

const args = parseArgs(process.argv.slice(2));

if (args.core) {
  const source = join(resolve(args.core), "src/L3/RepoQL.Hosting.Contracts/Protos");
  for (const proto of PROTOS) {
    mkdirSync(dirname(join(PROTO_ROOT, proto)), { recursive: true });
    copyFileSync(join(source, proto), join(PROTO_ROOT, proto));
    console.log(`vendored ${proto}`);
  }
}

const workspace = resolve(args.workspace ?? process.cwd());
const socket = resolveSocket(workspace);
const definition = protoLoader.loadSync(PROTOS, { includeDirs: [PROTO_ROOT], defaults: true, longs: Number });
const pkg = grpc.loadPackageDefinition(definition);
const target = `unix:${socket}`;
const credentials = grpc.credentials.createInsecure();
const host = new pkg.repoql.host.v1.HostService(target, credentials);
const catalog = new pkg.repoql.tools.v1.ToolCatalog(target, credentials);

try {
  const info = await unary(host, "GetHostInfo");
  const { tools } = await unary(catalog, "DescribeTools");
  const snapshot = { rqlVersion: info.version, tools };
  writeFileSync(join(PLUGIN_ROOT, "src", "catalog.json"), JSON.stringify(snapshot, null, 2) + "\n");
  console.log(`catalog: ${tools.length} tools from rql ${info.version} (${info.workspaceRoot})`);

  const manifestPath = join(PLUGIN_ROOT, "openclaw.plugin.json");
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  manifest.contracts = { ...manifest.contracts, tools: [...LOCAL_TOOLS, ...tools.map((t) => TOOL_PREFIX + t.name)] };
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
  console.log(`manifest: contracts.tools = ${manifest.contracts.tools.join(", ")}`);
} catch (err) {
  console.error(`sync failed against ${socket}: ${err.details ?? err.message}`);
  console.error("Run `rql serve` in the workspace (or pass --workspace <dir>) and retry.");
  process.exitCode = 1;
} finally {
  host.close();
}

function resolveSocket(root) {
  const map = join(root, ".repoql", "cache", "socket.path");
  if (existsSync(map)) {
    const text = readFileSync(map, "utf8").trim();
    const mapped = text.startsWith("{") ? JSON.parse(text).path : text;
    if (mapped && existsSync(resolve(root, mapped))) {
      return resolve(root, mapped);
    }
  }
  return join(root, ".repoql", "cache", "repoql.sock");
}

function unary(client, method) {
  return new Promise((ok, fail) =>
    client[method]({}, { deadline: new Date(Date.now() + 10_000) }, (err, res) => (err ? fail(err) : ok(res)))
  );
}

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--core") out.core = argv[++i];
    else if (argv[i] === "--workspace") out.workspace = argv[++i];
    else throw new Error(`Unknown argument '${argv[i]}'. Usage: npm run sync -- [--core <path>] [--workspace <dir>]`);
  }
  return out;
}
