# Host & connection layer

The two lowest layers. The **connection** is a gRPC domain socket (`.repoql/cache/repoql.sock`); the **host** is the long-running process that owns the socket, the database, and every service. Most "RepoQL is broken" reports bottom out here.

The RepoQL MCP server is a **thin client**: it resolves a workspace from the working directory, dials (or launches) the one host for that workspace, and relays. Many "wrong/empty results" issues are really *wrong workspace* issues — start here when the data looks off.

## Which workspace am I attached to?

The host resolves a workspace by walking **up** the directory tree from the working directory to the first of:

1. a `.git` repository root, or
2. a `.repoql/.gitignore` marker (written by `init` / first serve).

The nearest match wins; from any subdirectory you get the same workspace. **No marker found ⇒ the directory is not a workspace and is not indexed** — clients deliberately refuse, so a stray cwd (home, Downloads) never silently auto-indexes.

There is **one shared host per workspace**. Every client pointed at that workspace — this MCP bridge, `rql` CLI invocations, the dashboard — talks to the same host and the same `index.duckdb`. Point two different working directories at two different repos and you are talking to two different hosts.

Common working-directory failures:

| Symptom | Cause | Fix |
|---|---|---|
| Queries return nothing, no errors | cwd is not under any `.git`/`.repoql` workspace | `command("init")` to designate it, or move into the repo |
| Results are from the *wrong* repo | cwd resolved to a parent or sibling workspace | move into the intended repo; confirm with `Filesystems` |
| Right repo, but agent's cwd is unreliable | process cwd drifts | pin it with the `REPOQL_CWD` env var |

Confirm what you're actually attached to:

```sql
SELECT scheme, authority, source_uri, local_path, file_count FROM Filesystems;
```

The local mount's `local_path` is the workspace root the host resolved. If it isn't the repo you meant, the working directory is your bug — nothing downstream will be right until it is.

## The cheap first check

```
command("host status")
```

Returns readiness, phase, and file counts, e.g.:

```
Host: ready (Idle)
Files: 6701 complete, 0 indexed, 0 embedded, 0 failed of 6701 total
```

- First word (`ready`, `searchable`, …) = can it answer queries.
- Phase in parentheses (`Idle`, `Sweep`, …) = what it's doing now. Not `Idle` + files in flight ⇒ the connection is fine, indexing is just catching up (see `indexing-and-coverage.md`).
- `failed > 0` ⇒ pipeline is rejecting files (see `indexing-and-coverage.md`).

For a queryable, one-row version of readiness use `SELECT * FROM engine_status` (`state` = `indexing` | `embedding` | `ready`; plus `semantic_percent`, `semantic_ready`, `failed`). `host_meta('<key>')` exposes individual host-runtime values the host has published; `dashboard_url()` returns the live dashboard URL.

## Auto-launch: the host comes back by itself

The host launches **on demand**. If it's dead, the *next* RepoQL tool call relaunches it — you rarely need `host start` explicitly.

- `command("host status")` itself **errors with a connection error** → the bridge can't reach a host. Make any other call (`query("SELECT 1")`) to trigger relaunch, then re-check. Still failing ⇒ the host can't start; go to `host-wont-respond.md`.
- Status says **not running** → `command("host start")`, then re-check.

## When restart is the right move (and when it isn't)

`command("host restart")` is a real fix in two cases:

1. **Sticky degradation** — a service failed at startup but the cause is now resolved (auth refreshed, network back). Restart clears the sticky state.
2. **Undiagnosable bad state** — the registry shows something wrong, the host won't move it forward, and nothing in `host.log` points at a cause.

It is **medium-risk**: it kills in-flight indexing. Before restarting a *busy* host (phase not `Idle`), say so. After restarting, **verify by re-running the original failing call**, not by trusting that `host status` came back green. If the symptom returns immediately, restart was not the fix — go back to the layer the symptom points at.

`command("host stop")` shuts down without relaunch (the next tool call will start a fresh one).

## Version / protocol mismatch

If the running host predates the `rql` binary the plugin tracks, you can see confusing failures. Symptoms: stale `.repoql/host.version` (old layout), a host log mentioning a protocol or contract mismatch. Fix: `command("host restart")` to relaunch on the current binary; if a client/binary update is owed, `! rql update` from the terminal.

## Escalate (host layer)

If two restarts don't hold, escalate with: `host status` output, the tail of `.repoql/cache/host_*.log`, and what you changed between attempts. Note that you can still read files directly even while structural queries are down.
