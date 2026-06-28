# Database layer (DuckDB)

The index lives in a single DuckDB file, `.repoql/cache/index.duckdb` (+ a `.wal` write-ahead log). The host holds it open. Two failure shapes show up here: **someone else holds the lock**, and **writes are contended or slow**.

## Lock contention with another process

DuckDB allows one writer. If you opened `index.duckdb` in an external tool (DBeaver, a `duckdb` CLI, another RepoQL host), the host can't open it and you'll see a lock / IO error at startup.

- Confirm a foreign holder: a host that won't start whose `host.log` names a lock or "could not set lock on file".
- Fix: close the external tool, then `command("host restart")`. Killing another process or deleting the database is **high-risk** — escalate with evidence first.

## Memory and storage footprint

```
command("diagnostics memory")
```

Host memory, DuckDB, graph, and embedding footprint in one shot — the right call for "everything is slow / is it swapping?". For raw DuckDB internals you can also query the engine's own catalog:

```sql
SELECT * FROM pragma_database_size();   -- db size, WAL, block counts
SELECT * FROM duckdb_memory();          -- per-tag memory use
SELECT * FROM duckdb_temporary_files(); -- spilling to disk == memory pressure
```

A large, growing `index.duckdb.wal` that never checkpoints, or temp files appearing under load, point at memory pressure rather than a logic bug.

## Write contention: graph lock & write phases

When indexing crawls but nothing is *failing*, the graph writer may be the bottleneck. Two macros split where the time goes (both accumulate since host start):

```sql
-- Per connection role: is time spent WAITING for the lock or HOLDING it?
SELECT role, operations, avg_wait_ms, max_wait_ms, avg_hold_ms, max_hold_ms
FROM graph_lock_stats();

-- For the writer's hold time, WHICH phase owns it?
SELECT write_kind, phase, operations, avg_ms, max_ms
FROM graph_write_phase_stats();
```

Read them as a pair: `graph_lock_stats` tells you a write op's time is in *hold* not *wait*; `graph_write_phase_stats` tells you which phase of the hold is expensive. High `wait` across roles ⇒ contention for the single writer; high `hold` concentrated in one phase ⇒ that phase is the cost.

`DESCRIBE SELECT * FROM graph_lock_stats()` (and `…graph_write_phase_stats()`) for the full column set; deeper docs at `help:///schema/functions/table/graph-lock-stats.md` and `…/graph-write-phase-stats.md`.

## Recovering from memory pressure or OOM

If the host OOMs (the log shows an OOM / exit 137) or thrashes during embedding/indexing, `config` is the lever — these are real, tunable keys. Discover the full set with `command("config list")`; the load-relevant ones:

| Key | Effect |
|---|---|
| `duckdb.memory_limit` | Hard cap on DuckDB memory (e.g. `4GB`); unset = DuckDB's own default |
| `indexing.workers` / `indexing.analysis_workers` | Fewer concurrent indexing workers → lower peak memory |
| `embedding.batch_size` / `embedding.concurrency` | Smaller embedding batches → lower peak memory |
| `dotnet.analysis` | Deep Roslyn analysis is expensive and **off** by default — leave it off under pressure |

```
command("config set --key duckdb.memory_limit --value 4GB")
command("host restart")          # config changes take effect on restart
```

Changing config is **medium-risk** — state what you're changing and why before you do it, then verify by re-running the work that OOM'd. Repeated OOMs across a session are worth surfacing to the user, not silently restarting through.

## Boundary

These are *symptoms-of-load* tools, not correctness tools. If files are outright **failing**, that's the indexing layer (`indexing-and-coverage.md`), not the database.
