# Imports & operations

"I imported a repo and can't query it" and "is the reindex done?" live here. An **operation** is a unit of bulk work (an import, a reindex/sweep); a **mount** is an imported source registered in `.repoql/mounts.json`.

## Is the import even registered?

```
command("import list")
```

…or, with more detail, the per-source rollup:

```sql
SELECT scheme, authority, source_uri, file_count, indexed_count, embedded_count,
       embed_pct, watching, mounted_at
FROM Filesystems
ORDER BY mounted_at DESC;
```

`Filesystems` aggregates each mount: how many files, how many indexed/embedded, whether it's watched. A mount that's present but `indexed_count = 0` is registered but not yet processed — that's an *operation* question, below. A mount that's entirely **absent** means the import didn't register; re-run `import add --uri …` and watch for an error, and confirm `.repoql/mounts.json` gained an entry.

## Track the operation

```sql
-- Caller-facing surface: parsed kind/scope, unified state, honest progress
SELECT * FROM Operations ORDER BY created_at DESC LIMIT 10;
```

`Operations` is the high-level view (state, `ready_percent`, `runtime_s`, kind, scope) — use it first. `indexing_operations` is the raw engine ledger underneath (every progress bucket and lifecycle boolean) if you need column-level detail.

A scope becomes queryable only once its operation has progressed enough — a freshly added import is often still `discovered`/`indexing`. Poll `Operations` (or the trust footer's `index: N%`) rather than assuming the import failed.

## Deferred work ≠ failed work

```sql
SELECT * FROM operation_deferrals('PUT-OPERATION-ID-HERE') ORDER BY deferred_at;
```

A **deferred** file is dirty and will be re-attempted by the next sweep — it is not a failure. A **failed** file has `failures > 0` and an `error` in `indexing_registry` (see `indexing-and-coverage.md`). Don't treat deferrals as errors.

## Removing / refreshing an import

`command("import remove --uri github://owner/repo")` deletes the mount and its indexed data. Re-running `import add` on an existing URI refreshes it. The cached clone lives under `.repoql/cache/imports/…` (gitignored, disposable).

## Boundary

If imports register and operations complete but individual files are wrong/missing, that's per-file indexing (`indexing-and-coverage.md`). If `import`/`query` calls themselves error, that's the host or MCP-bridge layer.

Docs: `help:///schema/views/operations.md`, `…/indexing-operations.md`, `…/filesystems.md`, `help:///schema/functions/table/operation-deferrals.md`, `help:///operations/diagnosing-issues.md`.
