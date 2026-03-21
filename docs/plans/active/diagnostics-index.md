---
description: Plan for diagnostics.index command — surface indexing health to agents
tags: [diagnostics, indexing, observability, command]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: diagnostics.index command

## Scope

**Covers:**
- Store per-file processing duration on `FileEntry`
- Expose stuck, failed, slow, and timing data via existing SQL UDFs
- `diagnostics.index` command that formats this data for agents

**Does not cover:**
- Per-stage timing breakdown (only total hot-path duration)
- Dashboard visualization of this data
- Historical timing across restarts (UriRegistry is in-memory)

## Enables

Agents can self-diagnose when `explain` or `explore` blocks on "scope not ready." Today the only signal is the footer (`index: 98% (140 pending)`) — no way to see *what's* stuck, *what* failed, or *why* indexing is slow. This command turns "something is wrong" into "here's what to do about it."

## Prerequisites

- `FileEntry` record in `src/RepoQL.Contracts/UriRegistry/FileEntry.cs`
- `UriRegistryUdf` in `src/RepoQL.Data.DuckDB/UdfImplementations/UriRegistryUdf.cs`
- `DiagnosticsCommand` in `src/RepoQL.ConsoleApp/CommandImplementations/DiagnosticsCommand.cs`
- `_indexer_status_internal`, `_indexer_errors_internal`, `_indexer_pending_internal` UDFs already exist
- `processing_queue()` UDF exposes in-flight items with age
- `RepoQlClient` gRPC client for MCP-side commands

## North Star

An agent runs `diagnostics.index` and gets a complete picture in one call: what's healthy, what's stuck, what failed and why, what's slow, and whether the distribution of processing times is reasonable. The output is structured enough to act on — retry a failed file, skip a stuck one, or just wait.

## Done Criteria

### FileEntry duration tracking
- When a file completes hot-path indexing, the `IndexingEngine` shall record total elapsed milliseconds on the `FileEntry`
- The `FileEntry` shall expose a `ProcessingDurationMs` field (nullable long — null means not yet processed)
- When a file fails, the duration at time of failure shall still be recorded

### SQL exposure
- The `_indexer_status_internal` UDF shall include `ProcessingDurationMs` in its output
- The `failed_files()` macro shall include `ProcessingDurationMs` in its output

### diagnostics.index command
- The command shall show a summary section with total/indexed/pending/failed/stale counts from `_registry_summary_internal`
- The command shall show stuck files: any file in `processing_queue()` with age > 60 seconds, or any file in `_indexer_pending_internal` with status `Discovered` (never entered pipeline)
- The command shall show failed files with their error messages from `failed_files()`
- The command shall show slow files: any file where `ProcessingDurationMs > 30000`, sorted descending
- The command shall show duration percentiles by extension: min, P5, P50, avg, P95, max, total, count — grouped by file extension, computed from `_indexer_status_internal` where `ProcessingDurationMs IS NOT NULL`
- When no issues exist in a section, the command shall omit that section (don't show "Stuck files: none")

### Command integration
- The command shall be `diagnostics.index` (subcommand of existing `DiagnosticsCommand`)
- The command shall run on the MCP client side, executing SQL queries via the gRPC client
- The command shall format output as plain text tables (not JSON) for agent readability

## Constraints

- **Schema frozen**: No new tables. Duration stored on `FileEntry` (in-memory UriRegistry), exposed via existing UDF pattern
- **Single writer**: Duration set by `IndexingEngine` only, via `UriRegistry` methods
- **Transport parity**: The command runs SQL over gRPC — same data available via `query` tool
- **Two-process architecture**: Command runs MCP-side. All data must be accessible via SQL queries over the gRPC channel. No direct host injection.

## References

- `src/RepoQL.Contracts/UriRegistry/FileEntry.cs` — add `ProcessingDurationMs` field
- `src/RepoQL.Contracts/UriRegistry/UriRegistry.cs` — method to set duration
- `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs` — `overallTimer` Stopwatch already tracks hot-path duration per file (line ~797)
- `src/RepoQL.Data.DuckDB/UdfImplementations/UriRegistryUdf.cs` — `_indexer_status_internal`, `_indexer_errors_internal`
- `src/RepoQL.Data.DuckDB/UdfImplementations/QueueObservabilityUdf.cs` — `processing_queue()`
- `src/RepoQL.ConsoleApp/CommandImplementations/DiagnosticsCommand.cs` — add `diagnostics.index` method
- `docs/knowledge/testing-guidelines.md` — testing conventions

## Error Policy

The command must never fail even if the host is unhealthy. Each SQL query is independent — if one fails, show what succeeded and report the query error inline. An agent running this command is already diagnosing problems; the command must not add to them.
