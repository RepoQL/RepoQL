---
name: troubleshooting-repoql
description: "Diagnose and fix RepoQL issues. Use when a RepoQL tool call fails or errors, results seem wrong or incomplete, indexing or queries are slow, a directory isn't being indexed or isn't recognized as a workspace, an imported repo won't appear, semantic search or explain returns nothing, or the host is unresponsive."
tags: ["skill", "diagnostics", "troubleshooting", "health", "errors", "slow", "embeddings", "host", "indexing", "mcp", "imports", "repoql"]
---

# Diagnose RepoQL

RepoQL is layered. A problem in a lower layer surfaces as a symptom in a higher one — a dead host looks like a connection error, a locked database looks like indexing failure. **Diagnose bottom-up: confirm the layer below before investigating the one you suspect.**

```
Connection  →  Host  →  Database  →  Indexing  →  Services
(socket)       (process)  (DuckDB)    (pipeline)   (MCP bridge, embeddings, cloud)
```

This page is the router. It frontloads the few things you cannot derive, then points you at one granular reference per layer. The references live beside this file under `references/` — read them with your native file tools (they work even when RepoQL itself does not).

---

## Know these first

### 1. One shared host per workspace — and your working directory chooses it

The RepoQL tools are a **thin client**. The work happens in a long-running **host process**, and there is **one host per workspace**, shared by every client pointed at it. The workspace is resolved from your **working directory**: the host walks *up* the tree from the cwd to the nearest `.git` repo or `.repoql/.gitignore` marker and indexes from there. **No marker found ⇒ not a workspace ⇒ nothing is indexed** (RepoQL refuses to silently auto-index a stray dir like a home or Downloads folder).

So the working directory is a top root cause. A wrong cwd — a parent dir, a sibling repo, a home folder — means you're querying a *different* index, or none; "returns nothing / the wrong repo's results" is often just this. Confirm what you're attached to with `SELECT * FROM Filesystems` — the local mount's `local_path` is the workspace root the host resolved. Designate a non-repo dir with `command("init")`, or move into the git repo; harnesses can pin the root with the `REPOQL_CWD` env var. **If the workspace is right but results bleed in from *imported* repos, that's not a cwd bug — scope the query (a `uriGlob` / `source` filter) or see `references/imports-and-operations.md`.** Detail → `references/host-and-connection.md`.

### 2. The trust footer is a free signal — read it before assuming anything broke

Every `query` / `read` / `explore` response ends with a footer:

```
[2.9k tok | 239 ms | ready]                                  ← settled, trust the result
[108 tok | 8 ms | index: 57% (2823 pending) | semantic: 79% | stale: 1376]   ← still filling
```

"Results look incomplete" while the footer says `index: 57%` is the index catching up, **not** a bug. The same rollup is queryable as a single row: `SELECT state, semantic_percent, failed FROM engine_status`.

### 3. The host auto-launches; `restart` is deliberate, not reflexive

Any RepoQL tool call revives a dead host on demand — you rarely need `host start`. The host also shuts *itself* down after ~45s idle (`host.idle_grace_seconds`), so "it was running, now it's gone" is usually a normal idle exit, not a crash — the next call relaunches it. `host restart` is **medium-risk** (it kills in-flight indexing), so diagnose first. After any fix, **verify by re-running the original call** — "I restarted it" is hope, not evidence.

### 4. Where things live: the `.repoql/` two-zone layout

- **`.repoql/`** (committable): `.gitignore` — *its presence is the workspace marker* — plus `concepts/` and `mounts.json` (the import registry).
- **`.repoql/cache/`** (gitignored, transient): `host.log`, `host.lock`, `repoql.sock`, `index.duckdb`, `dashboard-bind.json`, `otel/`, `imports/`.
- **Migration trap:** old `repoql` wrote runtime files to the `.repoql/` *root*; current `rql` writes them under `.repoql/cache/`. A stale root `host.log`, `index.duckdb`, or `diagnostics/` folder will mislead you — **the live files are under `cache/`.** Full anatomy → `references/repoql-folder.md`.

### 5. The diagnostic SQL surface is the source of truth — and self-describing

`engine_status` (one-row health) · `indexing_registry` / `indexing_queue` · `indexing_stuck_candidates()` · `indexing_file_audit()` · `Operations` / `indexing_operations` / `operation_deferrals()` · `Filesystems` · `graph_lock_stats()` / `graph_write_phase_stats()` · `mcp_servers()` / `mcp_bridge_errors()` / `mcp_tools()` · `file_coverage()` / `uncovered_symbols()`.

The exhaustive, always-current catalog is host-served at `help:///schema/views/**` and `help:///schema/functions/table/**`; run `DESCRIBE SELECT * FROM <name>` for live columns. Prefer these over any signature memorized here.

### 6. Controls are narrow — discover them, don't memorize them

`command()` exposes only a **thin slice** of the full `rql` CLI (host lifecycle, account, config, import, and a bit more), and that surface evolves — so **let `command("help")` be the source of truth** for what's callable in-session, and `command("<cmd> --help")` for one command's detail. Broader or missing recovery — MCP-bridge ops, updates — lives in the CLI: run it yourself with `! rql …` (use `! rql --help` or `help:///commands/` to list it).

You can **see** far more than you can **change**. At the time of writing there's no queue cancel/retry/skip and no reindex command, so a stuck or failed file is fixed by waiting or `host restart` — but confirm the current surface with `command("help")` before concluding a command does or doesn't exist. And don't resurrect names from old docs or design notes (`queue.cancel`, `system_health()`, `reindex`, `failed_files()`) without checking — much of that was aspirational and never shipped.

### 7. When the host is down, SQL and `help://` are down too

`query`, `read`, `explore`, `command`, and every `help:///**` doc travel through the MCP→host bridge. If the host won't answer, none of them will — fall back to **native file reads** of `.repoql/cache/host_*.log` and friends. That playbook is the one reference that never touches the bridge → `references/host-wont-respond.md`.

---

## Diagnose by symptom

**First, split a *user* error from an *infrastructure* error — they have opposite fixes.** A malformed query is the single most common RepoQL failure, and RepoQL *enriches* it: a bad column lists `Candidate bindings:`, a bad view says `Did you mean …?`, a no-match read explains what didn't match. `Parser` / `Binder` / `Catalog` / `Conversion Error` and gRPC `InvalidArgument` are **your input** — read the enriched message, `DESCRIBE` the view or check `help:///schema/**`, and fix it; do **not** start diagnosing the host. (The read-only SQL surface also refuses `SET`, `PRAGMA`, writes, and multiple statements *by design* — tune with `command("config set …")`, not SQL `SET`.) Only connection / `Unavailable` / timeout / socket / OOM / "host not running" errors are infrastructure — those are what the table below is for.

Then start at the cheapest move; escalate only if it doesn't explain the symptom, and open the reference for depth.

| Symptom | First move | Reference |
|---|---|---|
| A `query` / `read` *errors* (Binder / Catalog / Parser / InvalidArgument) | Read the enriched error (candidate bindings / did-you-mean); `DESCRIBE` the view or see `help:///schema/**` | your input — not an infra issue |
| Nothing indexed / the *wrong* repo's results | Check your working directory; `SELECT * FROM Filesystems` | `references/host-and-connection.md` |
| A tool call fails with a *connection* / `Unavailable` / timeout error | `command("host status")` — if *it* errors too, the host is down | `references/host-wont-respond.md`, then `references/host-and-connection.md` |
| Results look incomplete / partial | Read the trust footer; `SELECT * FROM engine_status` | `references/indexing-and-coverage.md` |
| Recent edits aren't reflected / results show old code | Footer `stale:` count; `SELECT uri FROM indexing_registry WHERE dirty` | `references/indexing-and-coverage.md` |
| A specific file won't index / keeps failing | `SELECT uri, reason, error FROM indexing_registry WHERE failures > 0` | `references/indexing-and-coverage.md` |
| Indexing stuck or slow | `SELECT * FROM indexing_stuck_candidates(20)` | `references/indexing-and-coverage.md`, `references/database-layer.md` |
| Imported repo isn't queryable | `SELECT * FROM Operations`; `SELECT * FROM Filesystems` | `references/imports-and-operations.md` |
| An MCP tool is missing / a tool call fails | `SELECT * FROM mcp_servers()`; `SELECT * FROM mcp_bridge_errors()` | `references/mcp-bridge.md` |
| Semantic search empty / `explain` shallow | `command("account whoami")`; check `semantic_*` in `engine_status` | `references/cloud-auth-and-search.md` |
| Everything slow / OOM / database locked | `SELECT * FROM graph_lock_stats()`; `command("diagnostics memory")` | `references/database-layer.md` |
| Host crashed / won't respond at all | Native read newest `.repoql/cache/host_*.log` | `references/host-wont-respond.md` |
| "Where does RepoQL keep X on disk?" | — | `references/repoql-folder.md` |

A `read` / `explore` that returns *no match* is usually not a failure — the response says why (symbol-not-found vs file-not-found, or still pending) and what to try. Re-read it before escalating.

---

## Recovery discipline

- **Cheapest depth first.** Footer (free) → `engine_status` (one row) → the targeted view for the layer → `host restart` (last resort).
- **Match action to risk.** Reading diagnostics is free. Restarting an *idle* host is low-risk — act, then mention it. Restarting a *busy* host or changing `config` is medium — say so first (`config` is also your lever for OOM / resource pressure — see `references/database-layer.md`). Deleting `index.duckdb` or killing an external process holding the DB is high — escalate with evidence, don't do it silently.
- **Verify by re-running the failed operation**, not by watching health return.
- **Escalate well.** State what you tried, what you observed, and what you recommend; point to `.repoql/cache/host_*.log`. Don't loop; don't dump a raw stack trace.

---

*Read the footer. Confirm the layer below before the one you suspect. See with SQL, recover with the few real controls, verify by re-running. When the host is dark, your file tools still work.*
