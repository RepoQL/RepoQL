# Plan: Implementation

Implements: [Operations Design](../../designs/future/operations.md) — Components, Data Flow sections

## Scope

**Covers:**
- `Operation` class implementing `IOperation`
- `OperationManager` class implementing `IOperationManager`
- DI registration as singleton
- Unit tests for lifecycle, failures, cancellation, empty scope, progress callbacks

**Does not cover:**
- SQL surface (Plan: SQL Surface)
- Caller integration (Plan: Integration)

## Enables

Once Implementation exists:
- **Plan: SQL Surface** can query `IOperationManager` from DI
- **Plan: Integration** can inject `IOperationManager` into callers
- Operations are functional end-to-end (can be tested via code)

## Prerequisites

- Plan: Core Types complete — interfaces and records defined

## North Star

Create an operation, await completion, know exactly what happened. Polling is invisible to callers. Progress callbacks fire reliably.

## Done Criteria

### Operation Class

- The `Operation` class shall implement `IOperation`
- The `Operation` shall generate a unique `Id` on construction (GUID)
- The `Operation` shall store `Description` and `CreatedAt` on construction
- The `Operation` shall store scope as immutable collection (after deduplication)
- The `Operation` shall append `created` entry in constructor
- The `Operation` shall start a polling timer on construction
- The `Operation` shall expose `Completion` via `TaskCompletionSource<OperationProgress>`

### Scope Handling

- The `Operation` shall deduplicate scope by URI at construction
- The `TotalFiles` in progress shall reflect deduplicated count

### Polling Behavior

- The `Operation` shall poll `UriRegistry` every 500ms
- The `Operation` shall use a re-entrancy guard (skip tick if poll in progress)
- When `TryGetValue` returns false, the `Operation` shall append `file_failed` entry with message "URI not found in registry"
- When a URI transitions to `Indexed`, the `Operation` shall append `file_indexed` entry (once per URI)
- When a URI transitions to `Failed` (Status), the `Operation` shall append `file_failed` entry with error message
- When a URI transitions to `Embedded`, the `Operation` shall append `file_embedded` entry (once per URI)
- When a URI transitions to `NotApplicable`, the `Operation` shall append `file_ready` entry (once per URI)
- When a URI has `EmbeddingStatus.Failed`, the `Operation` shall append `embedding_failed` entry with error message
- The `Operation` shall update `Progress` after each poll cycle
- When `IProgress<OperationProgress>` was provided, the `Operation` shall call `Report()` after each update
  - If `Report()` throws, catch and log warning, continue

### Completion Detection

- The `Operation` shall consider a URI terminal when:
  - Status is `Indexed` AND EmbeddingStatus is `Embedded`, OR
  - Status is `Indexed` AND EmbeddingStatus is `NotApplicable`, OR
  - Status is `Failed`, OR
  - EmbeddingStatus is `Failed`, OR
  - URI was not found in registry
- When all URIs are terminal, the `Operation` shall:
  - Stop the polling timer
  - Set `State` to `Completed` (if no failures) or `CompletedWithFailures`
  - Set `CompletedAt` to current time
  - Append `completed` entry with summary (ready count, failed count, duration)
  - Resolve `Completion` task with final `Progress`

### Cancellation

- When `Cancel()` is called on a `Running` operation:
  - Stop the polling timer
  - Set `State` to `Cancelled`
  - Append `cancelled` entry with progress at cancellation
  - Cancel the `TaskCompletionSource` (throws `OperationCanceledException` for awaiters)
- When `Cancel()` is called on a non-Running operation, do nothing (no-op)

### OperationManager Class

- The `OperationManager` class shall implement `IOperationManager`
- The `OperationManager` shall be registered as singleton in DI
- The `OperationManager` shall inject `UriRegistry` for operations to poll
- The `CreateOperation` method shall:
  - Create new `Operation` with provided description and scope
  - Store operation in internal collection
  - Return the operation
- The `GetOperation` method shall return operation by ID or null
- The `Operations` property shall return all operations (active and completed)
- The `ActiveOperations` property shall return operations where `State == Running`

### Empty Scope

- When `CreateOperation` is called with empty scope:
  - The operation shall complete immediately
  - `State` shall be `Completed`
  - `Progress` shall be `(0, 0, 0, 0, 100)`
  - `Completion` task shall be already resolved
  - Log shall contain `created` and `completed` entries

### Tests

- Test: operation lifecycle (creation → file_indexed → file_embedded → completed)
- Test: operation with indexing failure (file_failed entry, CompletedWithFailures state)
- Test: operation with embedding failure (embedding_failed entry, CompletedWithFailures state)
- Test: operation cancellation (cancelled entry, Cancelled state, task cancelled)
- Test: empty scope (immediate completion)
- Test: progress callback fires on each poll
- Test: progress callback throws does not fail operation
- Test: duplicate URIs in scope are deduplicated
- Test: URI not in registry is logged as failed

## Constraints

- **500ms polling interval** — design specifies this balance of responsiveness vs overhead
- **Re-entrancy guard** — prevent overlapping polls
- **UriRegistry dependency** — operations observe, never modify, the registry
- **No persistence** — operations are in-memory only, lost on restart
- **Singleton manager** — one manager per host process
- **Retained until restart** — no expiry or cleanup of completed operations

## References

- [Operations Design](../../designs/future/operations.md) — Data Flow section for polling logic
- [Operation Lifecycle Flow](../../flows/future/operation-lifecycle.md) — sequence diagrams
- [UriRegistry.cs](../../../src/RepoQL.Contracts/UriRegistry/UriRegistry.cs) — status checks
- [FileEntry.cs](../../../src/RepoQL.Contracts/UriRegistry/FileEntry.cs) — Status, EmbeddingStatus, Error fields

## Error Policy

- If `TryGetValue` returns false, log as `file_failed` with "URI not found in registry"
- If UriRegistry throws during poll, skip cycle and retry next tick
- If poll cycle takes longer than 500ms, skip next tick (re-entrancy guard handles this)
- If progress callback throws, catch and log warning, don't fail operation
