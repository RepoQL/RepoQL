# @repoql/repoql

An [OpenClaw](https://github.com/openclaw/openclaw) plugin that gives agents
**queryable repository intelligence** through a local [RepoQL](https://repoql.ai)
`rql` host. The tools, their descriptions, and their schemas are the host's own,
so an agent works the index the same way over OpenClaw as it does over MCP.

## How it works

The plugin is a thin gRPC client of the host's public wire contract. It finds
the host for the agent's workspace over a Unix socket, launches one with
`rql serve --implicit-start` when none is running, and holds a lease on it while
the gateway runs (`repoql.host.v1`). Every tool call goes through the host's tool
catalog (`repoql.tools.v1.ToolCatalog`): `DescribeTools` supplies each tool's
description and JSON Schema, and `CallTool` runs it. The host owns indexing,
embeddings, query execution, and rendering.

```
agent ── OpenClaw ── @repoql/repoql ──gRPC/unix socket──► rql serve (host)
```

The host is shared with every other RepoQL client on the machine, so the plugin
never stops it on shutdown — it releases its lease, and a host it launched idles
out on its own.

## Requirements

- `rql` 1.6.30 or later on `PATH` (or set `rqlPath`). See https://repoql.ai for
  install; `rql update` upgrades it. Older hosts are detected and reported.
- Node.js 22+ (OpenClaw runtime).

## Install

```bash
openclaw plugins install clawhub:@repoql/repoql
openclaw gateway restart
```

Then enable and configure it under `plugins.entries.repoql`:

```json5
{
  plugins: {
    entries: {
      repoql: {
        enabled: true,
        config: { autoStart: true }
      }
    }
  }
}
```

## Tools

Each tool is the host tool of the same name under a `repoql_` prefix, and carries
the host's full description.

| Tool | What it does |
|------|--------------|
| `repoql_explore` | Search the indexed graph for relevant files and symbols (start here). |
| `repoql_discover_vocabulary` | Map rough terms onto the repository's real names. |
| `repoql_read` | Read indexed content by URI, with fragments and modifiers. |
| `repoql_query` | Execute DuckDB SQL over the graph. |
| `repoql_explain` | Synthesized answer with citations for a scoped question. |
| `repoql_execute` | Run sandboxed JavaScript over the graph — diagrams, conversions, artifacts. |
| `repoql_import` | Import or remove an external source (`github://owner/repo`, SARIF…). |
| `repoql_capture_concept` | Write a durable invariant into the repository's concept memory. |
| `repoql_capture_vocabulary` | Remember a repository name with the outside words that find it. |
| `repoql_command` | Management commands — config, account, imports, connectors, diagnostics, host lifecycle. |
| `repoql_watch` | Run a process under the host OTEL collector and query its telemetry. |
| `repoql_status` | Check the host for this workspace: version, readiness, socket. |

`repoql_command` runs in the host, except `host status|start|stop|restart`,
`init`, and `dashboard`, which run beside the plugin because they manage the host
process and the workspace on disk.

Skills ship alongside: `repoql`, `repoql-search`, `repoql-sql`, plus
document-authoring skills (`effective-markdown`, `mermaid-diagrams`, and the
research/design/plan family).

## Configuration

| Key | Default | Description |
|-----|---------|-------------|
| `rqlPath` | `rql` | Path to the `rql` executable. |
| `repoRoot` | workspace | Repository to index/query. Defaults to the agent workspace's enclosing git repository, else its nearest `.repoql` marker. |
| `autoStart` | `true` | Launch `rql serve --implicit-start` when no host is running. |
| `prewarm` | `false` | Connect to the host when the plugin service starts. |
| `startupTimeoutMs` | `120000` | Max wait for a launched host to start serving. |
| `requestTimeoutMs` | `120000` | Default deadline per tool call. Tools with their own timeout argument (`query`, `execute`) get that plus 30s. |

`defaultTokenBudget` and `queryMaxRows` from 1.0.x are accepted and ignored:
each tool now uses the host's defaults.

## Development

```bash
npm install
npm run build      # tsc + copy the protos and catalog snapshot into dist/
npm run typecheck
```

### Keeping up with rql

OpenClaw assembles tools before any host is contacted, and requires every tool
name to be declared in `openclaw.plugin.json`. So the plugin bundles a snapshot
of the host's catalog (`src/catalog.json`) and declares its names; once a host
answers, its live catalog replaces the snapshot. When rql adds or changes tools:

```bash
rql serve &                                   # in any workspace
npm run sync -- --core ../RepoQL.Core         # --core re-vendors the protos too
```

`sync` re-vendors `src/proto/repoql/{host,tools}/v1/*.proto` from a RepoQL.Core
checkout (when `--core` is given), snapshots `DescribeTools` from the host
serving `--workspace` (default: the current directory), and rewrites
`contracts.tools` in the manifest.

`npm run smoke -- --workspace <git repo>` drives every tool path against a real
host without OpenClaw.

## License

MIT
