---
description: Plan for web UI Query view - SQL execution and results
tags: [ui, plan, query, sql]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Query View

Implements: [Web UI Design](../designs/web-ui.md) — Query View, IQueryService

## Scope

**Covers:**
- `IQueryService` interface and implementation
- Query view component with SQL input
- Results grid component
- Row limiting and truncation display
- Error display inline
- Help panel with available macros/views

**Does not cover:**
- Monaco editor integration (deferred per design)
- Saved queries persistence (deferred per design)
- Query autocomplete (deferred)
- Export to CSV (stretch goal, not in done criteria)

## Enables

Once Query view exists:
- **Direct SQL testing** — Developers can verify macros, views, UDFs work
- **Debugging capability** — Can inspect index state with arbitrary queries
- **Foundation for other views** — Annotations, Git views reuse query patterns

## Prerequisites

- Plan: web-ui-1-foundation complete
- gRPC `ExecuteRawQuery` method operational on host

## North Star

Type SQL, press Run, see results. Errors visible where you're looking. Sub-second response for simple queries.

## Done Criteria

### IQueryService
- The QueryService shall accept SQL string and optional row limit
- The QueryService shall return `QueryResult` with columns, rows, timing, error
- The QueryService shall support cancellation via `CancellationToken`
- When query succeeds, the result shall include column names and typed values
- When query fails, the result shall include error message (no exception thrown)
- When row limit applied and more rows exist, `Truncated` shall be true

### Query View
- The Query view shall be accessible via navigation (route: `/query`)
- The Query view shall display a textarea for SQL input
  - Default content: `SELECT * FROM Files LIMIT 20;`
  - Textarea shall preserve content when navigating away and back
- The Query view shall display a row limit input (default: 200)
- The Query view shall display a Run button
  - When clicked, execute query via QueryService
  - When query running, button text shall change to "Running..."
  - When query running, button shall be disabled

### Execution Flow
- When Run clicked, the view shall show loading state
- When query completes successfully, the view shall render results grid
- When query fails, the view shall display error message inline (red background)
- When user presses `Ctrl+Enter`, the view shall execute query (same as Run click)

### Results Grid
- The grid shall display column headers from query result
- The grid shall display rows with values formatted appropriately
  - NULL displayed as `null` (italic)
  - Strings displayed as-is
  - Numbers displayed as-is
  - Booleans displayed as `true`/`false`
- When results truncated, the grid shall show "{n} rows shown, {total} total"
- When no results, the grid shall show "No results"

### Cancellation
- When query is running, a Cancel button shall appear
- When Cancel clicked, the query shall be cancelled
- When cancelled, the view shall return to ready state (no error shown)

### Help Panel
- The view shall include a collapsible help panel
- The help panel shall list common views: `Files`, `Types`, `Functions`, `Annotations`
- The help panel shall list common macros: `search()`, `snippet()`, `search_symbol()`
- The help panel shall show example queries for each

### Timing Display
- When query completes, duration shall be displayed (e.g., "23ms")
- Duration displayed near results header

## Constraints

- **No syntax highlighting** — Textarea only; Monaco deferred per design
- **No persistence** — Query text preserved in component state only, lost on refresh
- **Row limit enforced** — Default 200, maximum 10000
- **No streaming results** — Single response, not chunked

## References

- [Web UI Design](../designs/web-ui.md) — Query View section, IQueryService contract
- [Query Execution Flow](../flows/ui/query-execution.md) — Detailed stage descriptions
- [Schema.md](../Schema.md) — Available views and macros

## Error Policy

Query errors displayed inline:
1. Show error message in results area with red background
2. Include full error text from DuckDB
3. Clear error when new query executed
4. Do not show toast or modal — error appears where user is looking

Connection errors:
1. StatusStore will show offline state
2. Query execution will fail with connection error
3. Display "Connection lost" in results area
4. When reconnected, user can retry

## Verification

| Scenario | How to verify |
|----------|---------------|
| Simple query | Run `SELECT 1`, verify `1` appears in grid |
| View query | Run `SELECT * FROM Files LIMIT 5`, verify columns and rows |
| Macro query | Run `SELECT * FROM search('auth', k:=5)`, verify results |
| Error | Run `SELECT * FROM nonexistent`, verify error message appears |
| Timing | Run query, verify duration shown (e.g., "23ms") |
| Truncation | Run query returning >200 rows with limit 200, verify truncation message |
| Cancel | Run slow query, click Cancel, verify returns to ready state |
| Keyboard | Type SQL, press Ctrl+Enter, verify query executes |
| Help panel | Expand help, verify views and macros listed |
