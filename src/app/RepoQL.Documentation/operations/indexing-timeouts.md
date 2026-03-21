---
description: "How RepoQL handles slow or stuck indexing work without starving the host."
tags: ["indexing", "timeouts", "dashboard", "operations", "diagnostics"]
audience: ["LLMs"]
categories: ["Operations[100%]", "Indexing[100%]"]
---

# Indexing Timeouts

RepoQL now treats hot-path timeouts as a survivability event, not a reason to create more hidden work.

- Hot-path items default to a `45s` timeout.
- A timed-out hot-path item is deferred into a bounded idle-retry backlog.
- Idle retry runs with a single worker.
- If the same file times out again during idle retry, RepoQL marks it `Failed`.

This keeps the indexing worker budget real and leaves capacity for the host, dashboard, and gRPC API.

---

## What You Should Expect

When a file gets stuck:

1. The file leaves the hot path quickly.
2. Other files keep moving.
3. The dashboard and diagnostics show the stuck file, stage, elapsed time, and timeout counts.
4. The file is retried later during idle processing.
5. A second timeout turns into a visible failure instead of an endless retry loop.

---

## Tuning

These settings control the default behavior:

- `REPOQL_INDEXING_WORKERS`
- `REPOQL_ANALYSIS_WORKERS`
- `REPOQL_HOT_PATH_TIMEOUT_SECONDS`

Defaults:

- `IndexingWorkers = Environment.ProcessorCount`
- `AnalysisWorkers = min(Environment.ProcessorCount, 8)`
- `HotPathTimeoutSeconds = 45`

Use lower worker counts when the machine is also serving dashboard, MCP, or gRPC traffic and you want to preserve responsiveness under load.

---

## Diagnostics

Look for:

- active workers with URI, stage, worker id, and elapsed time
- hot-path timeout count
- deferred-to-idle count
- deferred retry pending/active counts
- files that eventually failed after retry

Useful entry points:

```text
::diagnostics.fast
::diagnostics
```

Dashboard operators should watch the `Stuck Items` panel for the exact file and stage currently consuming time.
