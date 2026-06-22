# @repoql/repoql

An [OpenClaw](https://github.com/openclaw/openclaw) plugin that gives agents
**queryable repository intelligence** through a local [RepoQL](https://repoql.ai)
`rql` host. It exposes the same surface as the RepoQL MCP server — the same
tools, the same descriptions — so an agent works the index the same way over
OpenClaw as it does over MCP.

## How it works

The plugin is a thin gRPC client. It connects to (and, by default, starts) a
local `rql serve` host over a Unix socket and forwards each tool call to the
host's `ToolService` / `ImportService` / `ManagementCommandService`. The host
owns indexing, embeddings, and query execution; the plugin owns lifecycle and
shaping results for the agent.

```
agent ── OpenClaw ── @repoql/repoql ──gRPC/unix socket──► rql serve (host)
```

A background service manages one host per workspace and shuts them down when the
gateway stops.

## Requirements

- The `rql` binary on `PATH` (or set `rqlPath`). See https://repoql.ai for install.
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

| Tool | What it does |
|------|--------------|
| `repoql_explore` | Search the indexed graph for relevant files and symbols (start here). |
| `repoql_keywords` | Reshape rough terms into the repository's real vocabulary. |
| `repoql_query` | Execute DuckDB SQL over the graph. |
| `repoql_read` | Read indexed content by URI, with fragments and modifiers. |
| `repoql_explain` | Synthesized answer with citations for a scoped question. |
| `repoql_execute` | Run sandboxed JavaScript over the graph — diagrams, conversions, artifacts. |
| `repoql_import` | Import or remove an external source (`github://owner/repo`, SARIF…). |
| `repoql_capture_concept` | Write a durable invariant into the repository's concept memory. |
| `repoql_command` | Management commands — config, diagnostics, account, host lifecycle, imports. |
| `repoql_watch` | Run a process under the host OTEL collector and query its telemetry. |
| `repoql_status` | Check plugin, repository root, socket, and host reachability. |

Each tool's description carries the full guidance from the RepoQL MCP server.
Five skills ship alongside: `repoql`, `repoql-search`, `repoql-sql`, plus
`effective-markdown` and `mermaid-diagrams` for authoring docs and diagrams.

## Configuration

| Key | Default | Description |
|-----|---------|-------------|
| `rqlPath` | `rql` | Path to the `rql` executable. |
| `repoRoot` | workspace | Repository root to index/query. Defaults to the agent workspace or its nearest git root. |
| `autoStart` | `true` | Start `rql serve --implicit-start` when the host socket is unavailable. |
| `prewarm` | `false` | Connect to the host when the plugin service starts. |
| `startupTimeoutMs` | `120000` | Max wait for `rql serve` to become reachable. |
| `requestTimeoutMs` | `120000` | Default timeout for gRPC tool requests. |
| `defaultTokenBudget` | `1500` | Default token budget for budgeted rendering. |
| `queryMaxRows` | `0` | Max rows returned by `repoql_query` (0 = no cap). |

## Development

```bash
npm install
npm run build      # tsc + copy proto/descriptions into dist/
npm run typecheck
```

The gRPC contract (`src/proto/hosting.proto`) and the tool descriptions
(`src/descriptions/*.md`) are vendored verbatim from the upstream RepoQL host;
keep them in sync with the host.

## License

MIT
