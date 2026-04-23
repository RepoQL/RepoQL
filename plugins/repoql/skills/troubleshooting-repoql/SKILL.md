---
description: "Diagnose and fix RepoQL issues. Use when tool calls fail, results seem wrong, indexing is stuck, the host is unresponsive, or things are slow."
tags: ["skill", "diagnostics", "troubleshooting", "health", "errors", "slow", "embeddings"]
---

# Diagnose

When something breaks, you have the tools to find and fix it. This skill tells you what they are and when to reach for each one.

## The Mental Model

RepoQL is layered. Problems at lower layers cause symptoms at higher layers. Diagnose bottom-up.

```
Connection  →  Host  →  Database  →  Indexing  →  Services
(socket)      (process)  (DuckDB)    (pipeline)   (embeddings, MCP, etc.)
```

A dead host looks like a connection error. A locked database looks like indexing failure. Always verify the layer below before investigating the layer you suspect.

---

## Capsule: CheapFirst

**Invariant**
Start with the cheapest diagnostic. Escalate only when the cheap one doesn't explain the symptom.

**Example**
Tool call fails with connection error. Run `command(command="host status")` (milliseconds, small output). If it shows the host is healthy, the problem is elsewhere. If the host is down, `command(command="host start")` fixes it — no need for SQL probes yet.
//BOUNDARY: Never open `.repoql/host.log` as your first step. Never `host restart` without knowing what's wrong.

---

## Escalation Path

This order is load-bearing. Each step either explains the symptom or tells you to go deeper. The Quick Reference table below can jump you to the right step directly.

### 1. Is the host reachable?

```
command(command="host status")
```

Output looks like `Host: ready (Idle)` followed by `Files: X complete, Y indexed, Z embedded, 0 failed of N total`. The phase word in parentheses (`Idle`, `Sweep`, etc.) tells you what the host is currently doing; the first word (`ready`, `searchable`) tells you whether it can answer queries.

If **the command itself fails** with a connection error: the MCP bridge can't reach the host. Try any other RepoQL tool call (e.g., `query(sql="SELECT 1")`) — the host auto-launches on demand. If it still fails, jump to step 5 (on-disk artifacts).

If **status reports the host is not running**: `command(command="host start")`, then re-check status.

If **phase is not `Idle`** and file counts show work in flight: the connection layer is fine, indexing is catching up. Results may be incomplete until the phase settles back to `Idle`. Either wait, or scope queries to files you know are `complete`.

If **`failed` count > 0**: something in the pipeline is rejecting files. Go to step 3.

### 2. Is the query layer sane?

```sql
query(sql="SELECT 1 AS ok")
```

If this round-trips, DuckDB is alive. If it doesn't, the host is not fully up — go back to step 1.

### 3. What does the registry say?

The `indexing_registry` view is the live source of truth for every file the engine knows about.

```sql
-- Failed files with the reason
SELECT uri, stage, reason, error, failures
FROM indexing_registry
WHERE failures > 0
ORDER BY failures DESC, transitioned_at DESC;

-- Files still in flight (dirty, active, or pending commit)
SELECT uri, stage, active, dirty, indexing_dirty, embedding_dirty, index_commit_pending, reason
FROM indexing_registry
WHERE active OR dirty OR index_commit_pending
ORDER BY transitioned_at DESC
LIMIT 50;

-- Aggregate health across the whole registry
SELECT stage, COUNT(*) AS files,
       SUM(CASE WHEN failures > 0 THEN 1 ELSE 0 END) AS failed,
       SUM(CASE WHEN active THEN 1 ELSE 0 END) AS active,
       SUM(CASE WHEN dirty THEN 1 ELSE 0 END) AS dirty
FROM indexing_registry
GROUP BY stage
ORDER BY files DESC;
```

Interpretation:
- `active = true` with a growing `in_progress_ms` in `indexing_queue` means a worker still owns the file. Either genuinely slow or hung.
- `dirty = true` with no queue entry means the next dirty sweep should pick it up.
- `index_commit_pending = true` means parsing finished and it's waiting on the commit writer.
- `failures > 0` with a populated `error` — read the error. Common causes: binary file misclassified, parser crash, timeout.

### 4. What's stuck in the queue?

```sql
-- Top stuck work items (in-progress time first, then waiting time)
query(sql="SELECT * FROM indexing_stuck_candidates(20)")

-- Queue shape by status and work kind
query(sql="SELECT queue, status, work_kind, COUNT(*) AS items,
                  MAX(waiting_ms) AS oldest_waiting_ms,
                  MAX(in_progress_ms) AS oldest_running_ms
           FROM indexing_queue
           GROUP BY queue, status, work_kind
           ORDER BY oldest_running_ms DESC NULLS LAST, oldest_waiting_ms DESC NULLS LAST")
```

If the same URI has been in-flight for minutes with no movement and no log lines mention it, it is hung. Capture `indexing_file_audit('uri', 30)` for the transition history before restarting.

### 5. On-disk artifacts (when the host can't talk)

When the host won't respond to the `command` tool or to `query`, the artifacts on disk still tell the story. The repo-local path is `.repoql/` in whatever directory has a bound host.

- `.repoql/host.log` — rolling log, newest at the end. `tail -100` usually contains the cause.
- `.repoql/host.stderr.log` — stderr capture. Crashes and native-library failures land here.
- `.repoql/diagnostics/socket-bind.json` — socket path, bind success, platform limits.
- `.repoql/diagnostics/existing-host.json` — did startup find a prior host? did it shut it down?
- `.repoql/diagnostics/database-init.json` — DuckDB open result, lock holder (if any), temp-dir writability, disk free.
- `.repoql/diagnostics/services-start.json` — `Issues: []` is healthy; anything else names what failed at startup.
- `.repoql/diagnostics/dashboard-bind.json` — dashboard HTTP bind result (port and success).
- `.repoql/host.lock` — contains the host PID; useful for a `ps` check when the socket looks dead.
- `.repoql/host.version` — version marker; interesting only when stale after a crash.

Read these directly with your file tools. They are JSON and small.

### 6. Operations and deferrals

Most users will never need this — it's for investigating why an import or reindex completed but a scope still isn't queryable.

```sql
-- Active and recent operations
query(sql="SELECT operation_id, name, status, total, discovered, indexed, complete, failed, deferred_count
           FROM indexing_operations
           ORDER BY created_at DESC
           LIMIT 10")

-- Deferred work for a specific operation
query(sql="SELECT * FROM operation_deferrals('PUT-OPERATION-ID-HERE') ORDER BY deferred_at")

-- Per-file history (duplicate work, cancellations, races)
query(sql="SELECT ordinal, version, transitioned_at, reason, stage, diff, error
           FROM indexing_file_audit('file:///path/to/file.cs', 50)
           ORDER BY ordinal")
```

Deferral is not failure. A deferred file is dirty and will be re-attempted by the next sweep. A failed file has `failures > 0` and an `error`.

### 7. Restart

`command(command="host restart")` is appropriate in two situations:

- **Sticky degradation**: a service failed at startup (see `services-start.json`) but the cause is now resolved (auth refreshed, network restored). Restart clears the sticky state.
- **Undiagnosable bad state**: the registry shows something wrong, the host won't move it forward, and nothing in the logs points at a cause.

Verify with `command(command="host status")` after restart. If the problem returns immediately, restart is not the fix — go back to step 3.

### When to stop

If you've run through these steps and can't determine the cause, tell the user what you found and what you tried. Include:

- The output of `command(command="host status")`.
- The `indexing_registry` aggregate from step 3.
- The `indexing_stuck_candidates(20)` from step 4.
- The tail of `.repoql/host.log` and `.repoql/host.stderr.log`.

Don't loop.

---

## Capsule: NoMatchIsNotFailure

**Invariant**
When `read` or `explore` returns nothing, the response itself tells you why and what to try next. Read it before assuming the tool is broken.

**Example**
`read("file:///src/Auth.cs#symbol=ValidateToken", 2000)` returning "File exists but no symbols matched 'ValidateToken'" is not a bug. The suggestion — try `#symbol=*` or `=> structure` — is actionable.
//BOUNDARY: If `host status` shows files still in flight, a not-found result for a recently added file may mean it's not indexed yet. Wait, or scope to files already `complete`.

---

## Capsule: AuthBeforeCloud

**Invariant**
Cloud-backed features (inference for `explain`, remote embeddings for semantic search) require an active session. If they silently fall back or return shallow results, check auth before blaming the feature.

**Example**
```
command(command="account whoami")
```

If it reports no session or an expired one, `command(command="account login")` — browser flow by default, `--device-code` for SSH / containers. Local ONNX embeddings still work without cloud; `explain` does not.
//BOUNDARY: Auth state is read from the local session store directly — `account whoami` works even if the host is down.

---

## Quick Reference

| Symptom | First action |
|---------|-------------|
| Tool call connection error | `command(command="host status")` |
| `Host: not running` | `command(command="host start")` |
| Results seem incomplete | `command(command="host status")` — check in-flight counts |
| Specific file won't index | Query `indexing_registry WHERE uri = '...'` then `indexing_file_audit` |
| Semantic search returns nothing | `command(command="account whoami")`; then check `structure_embedded` / `full_text_embedded` in registry |
| `explain` fails or is shallow | `command(command="account whoami")` — inference requires auth |
| Host seems stuck | `query(sql="SELECT * FROM indexing_stuck_candidates(20)")` |
| Host won't respond at all | Read `.repoql/host.stderr.log` and `.repoql/host.log` directly |
| Need to start fresh | `command(command="host restart")` then `command(command="host status")` |

## Other Commands

| Command | Purpose |
|---------|---------|
| `command(command="host stop")` | Graceful shutdown without relaunch |
| `command(command="dashboard")` | Open real-time monitoring UI in browser |
| `command(command="account login --device-code")` | Login flow for SSH / containers where a browser isn't available |
| `command(command="help")` | List every command the MCP tool exposes |
| `command(command="<cmd> --help")` | Per-command help — e.g. `account login --help` |

For anything not listed here (queue intervention, reindex, memory breakdown): those are not currently exposed through the MCP `command` tool. The data they would have surfaced is available in the SQL views above.

Schemas for the views are introspectable: `query(sql="DESCRIBE indexing_registry")` (and likewise for `indexing_queue`, `indexing_operations`) lists every column if the examples above don't cover what you need.

---

*Lower layers cause upper-layer symptoms. Cheap diagnostics before expensive ones. Read the error before you restart.*
