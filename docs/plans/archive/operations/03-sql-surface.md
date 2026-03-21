# Plan: SQL Surface

Implements: [Operations Design](../../designs/future/operations.md) — SQL Surface section

## Scope

**Covers:**
- `_operations()` UDF — list all operations
- `_operation(id)` UDF — get single operation
- `_operation_log(id)` UDF — get operation log entries
- Tests for UDF queries

**Does not cover:**
- Core types (Plan: Core Types)
- Implementation (Plan: Implementation)
- Caller integration (Plan: Integration)

## Enables

Once SQL Surface exists:
- Agents can query operation status via SQL
- Dashboards can show operation progress
- Failed files can be identified without code

## Prerequisites

- Plan: Implementation complete — `IOperationManager` available in DI

## North Star

Query operations like any other data. Same patterns, same tools. No special API needed.

## Done Criteria

### _operations() UDF

- The `_operations()` UDF shall return a table with columns:
  - `id` (VARCHAR) — operation ID
  - `description` (VARCHAR) — human-readable description
  - `state` (VARCHAR) — Running, Completed, CompletedWithFailures, Cancelled
  - `total_files` (INTEGER) — count of URIs in scope
  - `indexed_count` (INTEGER) — files that reached Indexed
  - `embedded_count` (INTEGER) — files that reached Embedded/NotApplicable
  - `failed_count` (INTEGER) — files that failed
  - `ready_percent` (INTEGER) — completion percentage
  - `created_at` (TIMESTAMP) — when operation started
  - `completed_at` (TIMESTAMP) — when operation completed (null if running)
- The UDF shall return all operations (active and completed)
- The UDF shall be registered via `[UdfClass]` and `[UdfMethod]` attributes

### _operation(id) UDF

- The `_operation(id)` UDF shall accept operation ID as parameter
- The UDF shall return same columns as `_operations()`
- When operation not found, return empty result set
- The UDF shall return at most one row

### _operation_log(id) UDF

- The `_operation_log(id)` UDF shall accept operation ID as parameter
- The UDF shall return a table with columns:
  - `timestamp` (TIMESTAMP) — when entry was logged
  - `type` (VARCHAR) — entry type (created, file_indexed, file_embedded, file_ready, file_failed, embedding_failed, completed, cancelled)
  - `message` (VARCHAR) — optional message (null if none)
  - `uri` (VARCHAR) — optional URI (null if none)
- When operation not found, return empty result set
- Entries shall be ordered by timestamp ascending

### Query Patterns

- `SELECT * FROM _operations() WHERE state = 'Running'` shall return active operations
- `SELECT * FROM _operations() WHERE state IN ('Completed', 'CompletedWithFailures')` shall return finished operations
- `SELECT uri, message FROM _operation_log('id') WHERE type IN ('file_failed', 'embedding_failed')` shall return all failures
- `SELECT datediff('ms', created_at, completed_at) FROM _operations() WHERE id = 'x'` shall return duration

### Tests

- The UDF tests shall verify column names and types
- The UDF tests shall verify empty result when no operations exist
- The UDF tests shall verify empty result when operation ID not found
- The UDF tests shall verify filtering by state works
- The UDF tests shall verify log ordering is by timestamp
- The UDF tests shall verify all entry types appear correctly (including `file_ready` and `embedding_failed`)

## Constraints

- **Underscore prefix** — internal UDFs use `_` prefix convention
- **VARCHAR for enums** — SQL surface uses strings, not integers
- **No write operations** — SQL surface is read-only

## References

- [Operations Design](../../designs/future/operations.md) — SQL Surface section with example queries
- [UriRegistryUdf.cs](../../../src/RepoQL.Data.DuckDB/UdfImplementations/UriRegistryUdf.cs) — pattern for UDF implementation
- [UDF Guide](../../../src/RepoQL.Data.DuckDB/UdfImplementations/README.md) — registration and testing patterns

## Error Policy

- If `IOperationManager` not available, return empty results (don't throw)
- If operation ID is null or empty, return empty results
