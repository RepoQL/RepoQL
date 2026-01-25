# Operations

URI state tracking and operation management for indexing workflows.

## Overview

This module provides:
- **IndexingState**: Central registry tracking all URIs and their pipeline phase
- **Operations**: Named groupings of related work (startup, reindex, import)
- **Ready Gating**: Scoped waiting for URIs to reach a target phase
- **Progress Streaming**: Real-time progress derived from URI state

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     MCP Tools                                │
│  query(), explore(), read(), import(), reindex()            │
└─────────────────────────────────────────────────────────────┘
                              │
            ┌─────────────────┼─────────────────┐
            ▼                 ▼                 ▼
┌───────────────────┐ ┌──────────────┐ ┌────────────────────┐
│   Ready Gating    │ │  Operations  │ │ Progress Streaming │
│ WaitForPhaseAsync │ │ Named groups │ │ IAsyncEnumerable   │
└───────────────────┘ └──────────────┘ └────────────────────┘
            │                 │                 │
            └─────────────────┼─────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                     IndexingState                            │
│  ConcurrentDictionary<Uri, UriEntry> _uris                  │
│  ConcurrentDictionary<string, Operation> _operations        │
│  • RegisterUri() / RemoveUri() / MarkDirty()                │
│  • CompletePhase() / MarkFailed()                           │
│  • WaitForPhaseAsync(scope, phase)                          │
│  • ComputeProgress(scope)                                   │
└─────────────────────────────────────────────────────────────┘
```

## URI Lifecycle

```
   ┌─────────┐    MarkDirty()    ┌─────────┐
   │         │◄──────────────────│         │
   │ Pending │                   │  Ready  │
   │         │──────────────────►│         │
   └────┬────┘   CompletePhase   └────┬────┘
        │                             │
        │ Start processing            │ File deleted
        ▼                             ▼
   ┌─────────────┐              ┌──────────┐
   │ Processing  │              │ Removed  │
   └─────────────┘              └──────────┘
        │
        │ Error
        ▼
   ┌─────────┐
   │ Failed  │
   └─────────┘
```

## Phase Model

| Phase | Description | Hot Path? |
|-------|-------------|-----------|
| `Discovery` | File found, queued | Yes |
| `Indexing` | Classify → Parse → Commit complete | Yes |
| `SemanticIndexing` | Embeddings generated | No |
| `Analysis` | Multi-file analysis complete | No |

**Hot path** = required for basic queries. Semantic search needs `SemanticIndexing`.

## Key APIs

### Wait for Scope + Phase

```csharp
// Wait for auth files to be indexed
await _indexingState.WaitForPhaseAsync(
    scope: "src/auth/**",
    targetPhase: OperationPhase.Indexing,
    ct);
```

### Check URI State

```sql
SELECT uri, current_phase, is_dirty, status
FROM UriStates
WHERE matches_glob(uri, 'src/**');
```

### Create Operation

```csharp
var operation = _indexingState.CreateOperation(
    type: "import",
    scope: "github://owner/repo/**");
```

### Stream Progress

```csharp
await foreach (var snapshot in coordinator.ReindexAsync(options, ct))
{
    Console.WriteLine($"{snapshot.Ready}/{snapshot.Total}");
}
```

## Flow Documents

| Document | Purpose |
|----------|---------|
| [Indexing State](indexing-state.md) | Central URI registry - **read this first** |
| [Operations](operations.md) | Named operation groupings |
| [Ready Gating](ready-gating.md) | Scoped waiting for phase completion |
| [Progress Streaming](progress-streaming.md) | Real-time progress via IAsyncEnumerable |

## Key Invariants

| Invariant | Consequence of Violation |
|-----------|--------------------------|
| URIs added on queue, removed on prune | Memory leak or missing state |
| Phase transitions are forward-only | Progress would regress |
| IsDirty cleared on Indexing complete | Dirty files skipped |
| Progress computed, not stored | Stale progress if cached |

## SQL Surface

```sql
-- URI states
SELECT * FROM UriStates;
SELECT * FROM UriStates WHERE is_dirty = true;
SELECT * FROM UriStates WHERE status = 'Failed';

-- Operations
SELECT * FROM Operations;

-- Waiting (in query/search)
SELECT * FROM search('auth', scope := 'src/**', waitFor := 'Indexing');
```

## Related

- [Epoch Tracking](../../current/indexing/epoch-tracking.md) - Batch coordination
- [Reindex](../../current/indexing/reindex.md) - Creates reindex operations
- [Import](../../current/indexing/import.md) - Creates import operations
- [Pruning](../../current/indexing/pruning.md) - Removes URIs from state
