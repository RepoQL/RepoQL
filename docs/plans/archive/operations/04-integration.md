# Plan: Integration

Implements: [Operations Design](../../designs/future/operations.md) — Integration section

## Scope

**Covers:**
- `ImportService` — create operation when importing repository
- `RepoqlHost` — create operation on startup for initial scan
- `IndexingCoordinator.ReindexAsync` — create operation when reindexing

**Does not cover:**
- Core types (Plan: Core Types)
- Implementation (Plan: Implementation)
- SQL surface (Plan: SQL Surface)

## Enables

Once Integration exists:
- Callers know when their files are ready to query
- Import tool can show progress and await completion
- Startup can report indexing progress
- Reindex operations are trackable

## Prerequisites

- Plan: Implementation complete — `IOperationManager` registered in DI

## North Star

Every batch of indexing work is trackable. No more guessing when files are ready.

## Done Criteria

### ImportService

- The `ImportService` shall inject `IOperationManager`
- When `ImportAsync` discovers and registers URIs, it shall create an operation:
  - Description: `"import: {uri}"` (e.g., `"import: github://owner/repo"`)
  - Scope: all discovered URIs (after registering them in UriRegistry)
- The `ImportAsync` method shall return a result type containing the `IOperation`
  - Existing callers that don't use the operation continue working (non-breaking)
- Callers can await `operation.Completion` to know when import is queryable

### RepoqlHost Startup

- The `RepoqlHost` shall inject `IOperationManager`
- When startup scan discovers and registers files, it shall create an operation:
  - Description: `"startup: {repository_path}"`
  - Scope: all discovered URIs from initial scan (after registering them)
- The startup operation shall be accessible via `_operations()` UDF
- The startup operation is fire-and-forget (host doesn't await it)

### IndexingCoordinator.ReindexAsync

- The `IndexingCoordinator` shall inject `IOperationManager`
- When `ReindexAsync` is called with URIs, it shall create an operation:
  - Description: `"reindex: {count} files"` or `"reindex: {pattern}"` if pattern provided
  - Scope: URIs being reindexed (must already be in UriRegistry)
- The `ReindexAsync` method shall return a result type containing the `IOperation`
  - Existing callers that don't use the operation continue working (non-breaking)

### Description Convention

- All descriptions shall follow `"kind: detail"` format
- Kind shall be lowercase: `import`, `startup`, `reindex`
- Detail shall identify the scope (URI, path, count, or pattern)

### Precondition Enforcement

- Callers shall register URIs in UriRegistry before creating the operation
- The sequence is: discover → register → create operation
- This is the caller's responsibility; operations assume URIs exist

### Backward Compatibility

- Existing callers that don't use the returned operation shall continue working
- Operations are fire-and-forget safe — callers don't have to await
- Return types should be result objects containing optional operation, not breaking signature changes

### Tests

- Test: ImportService creates operation with correct description and scope
- Test: RepoqlHost creates startup operation
- Test: IndexingCoordinator.ReindexAsync creates operation
- Test: Existing callers without operation handling still work

## Constraints

- **Agnostic operations** — operations don't know about import vs startup vs reindex; they just track URIs
- **Optional await** — callers can ignore the operation if they don't need to wait
- **No blocking** — creating an operation shall not block; polling happens in background
- **Register before track** — URIs must be in UriRegistry before operation creation

## References

- [Operations Design](../../designs/future/operations.md) — Example Usage section
- [ImportService.cs](../../../src/Indexing/RepoQL.Indexing/) — current import implementation
- [RepoqlHost.cs](../../../src/RepoQL.ConsoleApp/) — startup sequence
- [IndexingCoordinator.cs](../../../src/Indexing/RepoQL.Indexing/) — reindex implementation

## Error Policy

- If operation creation fails, log warning and continue without tracking
- Indexing must not fail because operation tracking failed
- This is observability — it enhances but never blocks core functionality
