# Operations Plans

Implementation plans for tracking indexing batches to completion.

## Overview

These plans implement the [Operations Design](../../designs/future/operations.md) to provide awaitable completion, progress visibility, and failure surfacing for indexing work.

## Dependency Order

```
┌─────────────────────┐
│  01-core-types      │  Foundation: enums, records, interfaces
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│  02-implementation  │  Operation + OperationManager classes
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     ▼           ▼
┌─────────┐ ┌─────────────┐
│ 03-sql  │ │ 04-         │
│ surface │ │ integration │  Can proceed in parallel
└─────────┘ └─────────────┘
```

## Plans

| # | Plan | What it delivers |
|---|------|------------------|
| 01 | [Core Types](01-core-types.md) | `OperationState`, `OperationProgress`, `OperationEntry`, `IOperation`, `IOperationManager` |
| 02 | [Implementation](02-implementation.md) | `Operation` class with polling, `OperationManager` singleton, DI registration |
| 03 | [SQL Surface](03-sql-surface.md) | `_operations()`, `_operation(id)`, `_operation_log(id)` UDFs |
| 04 | [Integration](04-integration.md) | Wire into `ImportService`, `RepoqlHost`, `IndexingCoordinator` |

## Execution Strategy

**Phase 1: Foundation (01)**
- Define contracts
- No runtime behavior yet

**Phase 2: Core (02)**
- Implement Operation and OperationManager
- Full test coverage of lifecycle

**Phase 3: Exposure (03 + 04)**
- SQL surface and caller integration can proceed in parallel
- Both depend only on Phase 2

## Success Criteria

When complete:

```csharp
// Caller registers URIs first, then creates operation
var operation = operationManager.CreateOperation(
    "import: github://foo/bar",
    discoveredUris);  // must already be in UriRegistry

// Await completion
var result = await operation.Completion;

// Check for failures
if (result.FailedCount > 0)
{
    var failures = operation.Log.Where(e =>
        e.Type == "file_failed" || e.Type == "embedding_failed");
}
```

```sql
-- Query active operations
SELECT * FROM _operations() WHERE state = 'Running';

-- Get all failures for an operation (indexing or embedding)
SELECT uri, message FROM _operation_log('abc123')
WHERE type IN ('file_failed', 'embedding_failed');

-- Measure time to completion
SELECT datediff('ms', created_at, completed_at) as duration_ms
FROM _operations()
WHERE id = 'abc123';
```

## Related

- [Operations Design](../../designs/future/operations.md) — architecture and contracts
- [Operation Lifecycle Flow](../../flows/future/operation-lifecycle.md) — how it works
- [UriRegistry](../../Schema.md#uri-registry) — source of truth for file status
