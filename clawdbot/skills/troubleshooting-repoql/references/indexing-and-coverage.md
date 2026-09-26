# Indexing & coverage layer

This is where "results seem wrong/incomplete", "a file won't index", and "indexing is stuck" live. The registry is the live, per-file source of truth; coverage tells you what made it in.

The canonical, host-served walkthrough is `help:///operations/diagnosing-issues.md` (SQL-first diagnosis of indexing/operation/queue/deferral/file-state) and `help:///operations/indexing-timeouts.md` (how slow work is handled without starving the host). This reference is the offline-safe summary and entry point.

## Start with the rollup

```sql
SELECT state, total_files, complete, failed, discovered, active,
       semantic_percent, semantic_ready
FROM engine_status;
```

`state` is `indexing` (first-pass work pending) / `embedding` (indexed, awaiting embedding) / `ready` (settled — *nothing pending*, though `failed`/`skipped` may be > 0). `ready` means settled, not 100%: read `failed` for the gap. Don't sum `discovered + dirty + active` for "pending" — those overlays overlap.

## Edits aren't reflected: staleness & watching

The footer's `stale: N` is the count of **dirty** files — changed on disk and flagged for re-indexing (`stale` = `registry.Dirty`; list them with `SELECT uri FROM indexing_registry WHERE dirty`). The fix depends on *what* is stale:

- **Your workspace is watched** — edits are flagged dirty and re-swept automatically. A `stale` count that's *dropping* is normal catch-up; wait and re-run. A count that's *stuck* (not falling) means the watcher or sweep is wedged — check `indexing_queue`, and `host restart` if nothing is moving.
- **Imported repos are NOT watched** (`watching = False` in `Filesystems`). They're static snapshots — editing upstream or pulling won't refresh them. **Re-run `import add --uri …`** to refresh an import. Check which mounts are live: `SELECT source_uri, watching FROM Filesystems`.

A `ready` footer with no `stale` segment means the index reflects the working tree.

## Failed files: read the reason

```sql
SELECT uri, stage, reason, error, failures
FROM indexing_registry
WHERE failures > 0
ORDER BY failures DESC, transitioned_at DESC;
```

Common causes: a binary file misclassified as text, a parser crash, a timeout. The `error` text usually names it. There is **no** per-file retry/skip command today — a failed file is re-attempted when the file changes or on `host restart`. If one file reliably crashes the host, that's the case for escalation, not a loop.

For the full history of one file (every transition + diff):

```sql
SELECT ordinal, transitioned_at, stage, reason, error, diff
FROM indexing_file_audit('file:///path/to/file.ext', 50)
ORDER BY ordinal;
```

## Stuck / in-flight work

```sql
-- Stuck candidates: in-progress time first, then waiting time
SELECT * FROM indexing_stuck_candidates(20);

-- Queue shape by status and kind
SELECT queue, status, work_kind, COUNT(*) AS items,
       MAX(waiting_ms) AS oldest_waiting_ms, MAX(in_progress_ms) AS oldest_running_ms
FROM indexing_queue
GROUP BY queue, status, work_kind
ORDER BY oldest_running_ms DESC NULLS LAST;
```

A URI in-flight for minutes with no movement is hung. With no queue-intervention command available, the levers are: wait (it may time out and get re-swept — see `indexing-timeouts.md`), or `host restart` to clear it (medium-risk). If the queue is moving but slow, suspect the database layer (`database-layer.md`).

Registry flags worth knowing: `active` (a worker owns it), `dirty` (will be re-swept), `index_commit_pending` (parsed, awaiting the commit writer), `failures > 0` (terminal until changed). `DESCRIBE SELECT * FROM indexing_registry` for all columns; doc at `help:///schema/views/indexing-registry.md`.

## Slow discovery on Windows: antivirus

If first-time indexing or discovery is *very* slow on Windows — not stuck, not failing, just crawling — suspect the antivirus. Real-time scanners (usually **Windows Defender**) inspect every file RepoQL touches during discovery, which can dominate the wall-clock. The fix is to **exclude the workspace folder from real-time scanning**: Windows Security → Virus & threat protection → Manage settings → Exclusions → add the repo (or its `.repoql/` cache) folder. This is environmental, not a RepoQL bug — `indexing_stuck_candidates`/`graph_lock_stats` will look healthy because nothing is actually hung.

## Coverage: did the content actually get parsed?

"Indexed" ≠ "deeply parsed". Coverage shows how much of a scope is structurally understood, and what's missing:

```sql
SELECT * FROM file_coverage('file:///src/**');   -- per-symbol coverage state within a scope
SELECT * FROM uncovered_symbols('file:///src/**'); -- symbols with no coverage
-- symbol_coverage('file:///one/file.ext') for a single file
```

Use these when search finds a file but not the symbol you expected — the file may be indexed at headline depth only, or the format may be partially supported. Docs: `help:///schema/functions/table/file-coverage.md`, `…/uncovered-symbols.md`, `…/symbol-coverage.md`.

## Boundary

If the *whole* index is empty or a specific imported repo is missing, this is the operations/imports layer, not per-file indexing → `imports-and-operations.md`.
