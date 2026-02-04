# Plan: Core Types

Implements: [Operations Design](../../designs/future/operations.md) — Contracts section

## Scope

**Covers:**
- `OperationState` enum
- `OperationProgress` record
- `OperationEntry` record
- `IOperation` interface
- `IOperationManager` interface

**Does not cover:**
- Implementation classes (Plan: Implementation)
- SQL surface (Plan: SQL Surface)
- Caller integration (Plan: Integration)

## Enables

Once Core Types exist:
- **Plan: Implementation** can implement `IOperation` and `IOperationManager`
- **Plan: SQL Surface** can reference types for UDF return values
- Contracts are reviewable before implementation begins

This is the foundation. All other operations plans depend on it.

## Prerequisites

- None — this is the first increment

## North Star

Clean contracts that make the two audiences clear: callers create and await operations, observers query progress and logs.

## Done Criteria

### OperationState

- The `OperationState` enum shall define `Running`, `Completed`, `CompletedWithFailures`, `Cancelled`
- The enum shall be in `RepoQL.Contracts` namespace

### OperationProgress

- The `OperationProgress` record shall contain `TotalFiles`, `IndexedCount`, `EmbeddedCount`, `FailedCount`, `ReadyPercent`
- The `TotalFiles` shall be the count of URIs in scope (after deduplication)
- The `IndexedCount` shall be files that reached `Indexed` status
- The `EmbeddedCount` shall be files that reached `Embedded` or `NotApplicable` status
- The `FailedCount` shall be files that failed indexing or embedding (including URIs not found in registry)
- The `ReadyPercent` shall be `(EmbeddedCount + FailedCount) * 100 / TotalFiles` (integer division)
  - When `TotalFiles` is zero, `ReadyPercent` shall be 100

### OperationEntry

- The `OperationEntry` record shall contain `Timestamp`, `Type`, `Message`, `Uri`
- The `Timestamp` shall be `DateTimeOffset`
- The `Type` shall be `string` — one of: `created`, `file_indexed`, `file_embedded`, `file_ready`, `file_failed`, `embedding_failed`, `completed`, `cancelled`
- The `Message` shall be `string?` (nullable — not all entries have messages)
- The `Uri` shall be `RepoUri?` (nullable — not all entries have URIs)

### IOperation

- The `IOperation` interface shall expose `Id` (string)
- The `IOperation` interface shall expose `Description` (string)
- The `IOperation` interface shall expose `CreatedAt` (DateTimeOffset)
- The `IOperation` interface shall expose `CompletedAt` (DateTimeOffset?)
- The `IOperation` interface shall expose `State` (OperationState)
- The `IOperation` interface shall expose `Progress` (OperationProgress)
- The `IOperation` interface shall expose `Log` (IReadOnlyList&lt;OperationEntry&gt;)
- The `IOperation` interface shall expose `Completion` (Task&lt;OperationProgress&gt;)
- The `IOperation` interface shall expose `Cancel()` method returning void

### IOperationManager

- The `IOperationManager` interface shall expose `CreateOperation(description, scope, progress?)` method
  - The `description` parameter shall be `string`
  - The `scope` parameter shall be `IEnumerable<RepoUri>`
  - The `progress` parameter shall be `IProgress<OperationProgress>?`
  - The method shall return `IOperation`
- The `IOperationManager` interface shall expose `GetOperation(id)` method returning `IOperation?`
- The `IOperationManager` interface shall expose `Operations` property (IReadOnlyList&lt;IOperation&gt;)
- The `IOperationManager` interface shall expose `ActiveOperations` property (IReadOnlyList&lt;IOperation&gt;)

## Constraints

- **RepoQL.Contracts location** — contracts live in shared contracts assembly
- **Immutable records** — `OperationProgress` and `OperationEntry` shall be records for immutability
- **RepoUri for URIs** — use `RepoUri` not raw strings, consistent with UriRegistry

## References

- [Operations Design](../../designs/future/operations.md) — Contracts section with full interface definitions
- [UriRegistry](../../../src/RepoQL.Contracts/UriRegistry/) — pattern for contracts in this namespace

## Error Policy

No runtime errors — these are pure type definitions. Compile errors from signature changes indicate contract mismatches to resolve.
