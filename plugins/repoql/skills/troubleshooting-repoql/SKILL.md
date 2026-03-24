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
Tool call fails with connection error. Run `command(command="diagnostics.fast")` (seconds, small output). If it shows the host is healthy, the problem is elsewhere. If the host is down, you now know — without spending 10 seconds on a full probe suite.
//BOUNDARY: Never run `diagnostics` (full) as your first step. Never `host.restart` without diagnostics first.

---

## Escalation Path

This order is load-bearing. Each step either explains the symptom or tells you to go deeper. The path is mostly linear, but the Quick Reference table below can point you to the right step directly if you already know the symptom.

### 1. Is the host reachable?

```
command(command="diagnostics.fast")
```

If **this command itself fails** with a connection error: the host is down and auto-relaunch didn't trigger. Try any other RepoQL tool call (e.g., `query(sql="SELECT 1")`) to trigger auto-relaunch, then retry `diagnostics.fast`. If it still fails, read `.repoql/host.log` directly with your file reading tools for crash output.

Look for: socket connectable, health status SERVING, host PID present and running.

If **socket not connectable** or **host not running**: the host crashed or wasn't started. It should auto-relaunch on the next tool call. If it doesn't, check `.repoql/host.log` for crash output.

If **health NOT_SERVING**: check the `repoql-reason` in output. `initial_indexing` means it's still starting up — wait. `unhealthy` means a service failed — go to step 4.

If **healthy**: the connection layer is fine. Your problem is higher up.

### 2. Is indexing working?

```
command(command="diagnostics.index")
```

Returns: file counts (total/indexed/pending/failed), stuck files, failed files, slow files, duration distribution by extension.

If **files stuck** (age > 60s): something is hung in the pipeline.
```
command(command="queue.cancel", args="file:///path/to/stuck-file.ext")
```

If **files failed**: read the error message. Common causes: binary file misclassified, parser crash, timeout.
```
command(command="queue.retry", args="file:///path/to/failed-file.ext")
```

If a file always fails, skip it permanently:
```
command(command="queue.skip", args="file:///path/to/bad-file.ext")
```

If **pending count is high but not moving**: the pipeline may be stalled. Check step 4 for degraded services.

### 3. Are cloud services working?

```
command(command="diagnostics.cloud")
```

Shows: auth status, inference endpoint, embedding provider/model/progress/reachability.

If **not authenticated**: `command(command="auth.login")` to authenticate.

If **embedding not reachable**: cloud embedding service is down or network issue. Local ONNX embeddings still work — semantic search degrades in quality but doesn't break entirely.

If **embedding progress shows 0/N embedded**: embeddings haven't been computed yet. This happens during initial indexing or if the embedding service was never configured. Semantic search relies on BM25 and fuzzy matching until embeddings are ready.

If **inference unavailable**: explain tool and question modifier won't work. Everything else is unaffected.

### 4. Full picture

```
command(command="diagnostics")
```

The expensive option. Runs all probes: socket, host process, health for 12 named services, database lock detection, disk space, node count, indexing diagnostics, host logs, startup artifacts.

Look for:
- **`repoql-degraded: embeddings,mcp`** — lists which services are degraded. Degradation is sticky — the service failed at startup and stays marked until restart. If the underlying cause was transient (network blip, auth token expired), `host.restart` clears it. This is the correct fix for transient degradation, not a nuclear option.
- **`repoql-rpc-hanging: 2`** — hanging gRPC calls. The oldest request method tells you what's stuck.
- **DB locked** with lock holder PID/name — another process holds the database.
- **Startup artifacts** (`socket-bind.json`, `database-init.json`, `services-start.json`) — what happened at host startup.
- **Host stderr tail** — last 50 lines, often contains the root cause.

### 5. SQL inspection

For surgical investigation when you know what layer is broken:

```sql
-- Single-row health summary: status, queue depth, workers, failures, memory, disk
query(sql="SELECT * FROM system_health()")

-- What's in-flight right now
query(sql="SELECT * FROM processing_queue() WHERE age_seconds > 30 ORDER BY age_seconds DESC")

-- What failed and why
query(sql="SELECT * FROM failed_files()")

-- Full registry state
query(sql="SELECT * FROM indexing_diagnostics()")
```

### 6. Restart

`host.restart` is appropriate in two situations:
- **Sticky degradation**: a service failed at startup but the cause is now resolved (auth refreshed, network restored). Restart clears the sticky state.
- **Undiagnosable bad state**: diagnostics show something wrong but the cause isn't actionable.

```
command(command="host.restart")
```

Verify with `command(command="diagnostics.fast")` after restart.

### When to stop

If you've run through these steps and can't determine the cause, tell the user what you found and what you tried. Include the output of `command(command="diagnostics")` so they have the full picture. Don't loop.

---

## Capsule: ErrorClassification

**Invariant**
RepoQL classifies errors automatically. Infrastructure errors trigger auto-diagnostics. User errors get enriched with recovery hints.

**Example**
SQL `Binder Error: column "foo" not found` → user error. Gets enriched with `Tip: Use DESCRIBE table_name` and a `help://` doc link. No diagnostics triggered.

`SocketException` / gRPC `Unavailable` / `TimeoutException` → infrastructure error. Auto-diagnostics run and results appear alongside the error.
//BOUNDARY: If you see auto-diagnostics in an error response, the system already ran them. Read that output before running diagnostics manually.

**Depth**
- Infrastructure: `SocketException`, `TimeoutException`, gRPC `Unavailable`/`Internal`, `ObjectDisposedException`, HTTP/2 failures, host launch failures
- User: SQL errors (`Parser Error`, `Binder Error`, `Catalog Error`, `Conversion Error`), gRPC `InvalidArgument`/`FailedPrecondition`
- SQL errors are enriched: table names extracted from "Candidate bindings", `DESCRIBE` hints added, `help://` links included

---

## Capsule: NoMatchIsNotFailure

**Invariant**
When a read or explore returns nothing, the error message tells you why and what to try next.

**Example**
`read("file:///src/Auth.cs#symbol=ValidateToken", 2000)` returns "File exists but no symbols matched 'ValidateToken'." with suggestions: try `#symbol=*` to see all symbols, or `=> structure` for signatures.
//BOUNDARY: "No results" with pending files means the target may not be indexed yet — wait and retry.

---

## Quick Reference

| Symptom | First action |
|---------|-------------|
| Tool call connection error | `command(command="diagnostics.fast")` |
| Results seem incomplete | `command(command="diagnostics.index")` — check pending/failed counts |
| Semantic search returns nothing | `command(command="diagnostics.cloud")` — check embedding status |
| Everything is slow | `query(sql="SELECT * FROM system_health()")` — check queue_depth, host_memory_mb |
| Specific file won't index | `query(sql="SELECT * FROM failed_files()")` then `queue.retry` or `queue.skip` |
| Host seems stuck | `command(command="diagnostics")` — check rpc-hanging count |
| Need to start fresh | `command(command="host.restart")` then `command(command="diagnostics.fast")` |

## Other Diagnostic Commands

| Command | Purpose |
|---------|---------|
| `command(command="memory")` | Host memory usage |
| `command(command="heap-memory")` | Detailed heap breakdown |
| `command(command="dashboard")` | Open real-time monitoring UI (browser) |
| `command(command="reindex")` | Re-index all files from scratch |
| `command(command="?")` | List all available commands |

---

*Lower layers cause upper-layer symptoms. Cheap diagnostics before expensive ones. Read the error before you restart.*
