# Operations Design

## North Star

Know when your files are ready to query. See what's happening. Know what went wrong. Measure how long it took.

## Context

Operations track batches of indexing work to completion. When you import a repository, reindex files, or start the host, you get an operation that tells you when those specific files are queryable.

**Enables:** [Operation Lifecycle Flow](../flows/future/operation-lifecycle.md)

**Built on:** [UriRegistry](../Schema.md#uri-registry) — source of truth for file status

## Prerequisites

- **URIs registered before tracking** — All URIs in scope must be registered in UriRegistry before `CreateOperation` is called. Callers discover files, register them, then create an operation to track them.

## Constraints

- **In-memory only** — operations are transient, not persisted
- **Fixed scope** — URIs defined at creation, deduplicated, immutable
- **Polling-based** — checks UriRegistry every 500ms with re-entrancy guard
- **Structure embedding** — complete when indexed + structure embedded (not full-text)
- **Agnostic** — operations don't know or care what triggered them
- **Retained until restart** — no expiry, no limit on count

**Not operations:** Unimport is a synchronous action (fast delete, no progress to track). It returns when done, no operation needed.

---

## Components

```
┌─────────────────────────────────────────────────────────────┐
│                     Callers                                  │
│  ImportService  |  RepoqlHost  |  IndexingCoordinator       │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼ CreateOperation(scope)
┌─────────────────────────────────────────────────────────────┐
│                   OperationManager                           │
│  - Creates operations                                        │
│  - Tracks active/completed operations                        │
│  - Singleton, injected via DI                               │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                      Operation                               │
│  - Polls UriRegistry every 500ms                            │
│  - Appends entries to its log                               │
│  - Updates progress, fires IProgress<T>                     │
│  - Resolves TaskCompletionSource on completion              │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
                    ┌─────────────────────┐
                    │    UriRegistry      │
                    │                     │
                    │  - File status      │
                    │  - Embedding status │
                    │  - Source of truth  │
                    └─────────────────────┘
```

---

## Contracts

### IOperationManager

```csharp
/// <summary>
/// Creates and tracks operations. Singleton, registered in DI.
/// </summary>
public interface IOperationManager
{
    /// <summary>
    /// Creates a new operation tracking the given URIs.
    /// Scope is deduplicated by URI. All URIs must already be in UriRegistry.
    /// </summary>
    /// <param name="description">Human-readable description (convention: "kind: detail")</param>
    /// <param name="scope">URIs to track (deduplicated and immutable after creation)</param>
    /// <param name="progress">Optional progress callback</param>
    IOperation CreateOperation(
        string description,
        IEnumerable<RepoUri> scope,
        IProgress<OperationProgress>? progress = null);

    /// <summary>Gets operation by ID, or null if not found.</summary>
    IOperation? GetOperation(string id);

    /// <summary>All operations (active and completed, until restart).</summary>
    IReadOnlyList<IOperation> Operations { get; }

    /// <summary>Only operations not yet in terminal state.</summary>
    IReadOnlyList<IOperation> ActiveOperations { get; }
}
```

### IOperation

```csharp
/// <summary>
/// A trackable, awaitable batch of indexing work.
/// Operations are agnostic to what triggered them.
/// </summary>
public interface IOperation
{
    string Id { get; }
    string Description { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset? CompletedAt { get; }

    OperationState State { get; }
    OperationProgress Progress { get; }
    IReadOnlyList<OperationEntry> Log { get; }

    /// <summary>Resolves when all URIs reach terminal state.</summary>
    Task<OperationProgress> Completion { get; }

    /// <summary>Stops tracking. Already-indexed files remain.</summary>
    void Cancel();
}
```

### Supporting Types

```csharp
public enum OperationState
{
    Running,
    Completed,
    CompletedWithFailures,
    Cancelled
}

public record OperationProgress(
    int TotalFiles,
    int IndexedCount,
    int EmbeddedCount,
    int FailedCount,
    int ReadyPercent);  // (EmbeddedCount + FailedCount) * 100 / TotalFiles, or 100 if TotalFiles is 0

public record OperationEntry(
    DateTimeOffset Timestamp,
    string Type,      // see Entry Types below
    string? Message,
    RepoUri? Uri);
```

### Entry Types

| Type | Meaning | Message | Uri |
|------|---------|---------|-----|
| `created` | Operation started | Description and file count | null |
| `file_indexed` | File finished indexing | null | the file |
| `file_embedded` | File has structure embedding | null | the file |
| `file_ready` | File ready (embedding not applicable) | null | the file |
| `file_failed` | Indexing failed | Error message | the file |
| `embedding_failed` | Embedding failed | Error message | the file |
| `completed` | All files terminal | Summary stats, duration | null |
| `cancelled` | Operation cancelled | Progress at cancellation | null |

---

## Data Flow

### Creation

```
Caller:
    // URIs already registered in UriRegistry
    scope = [uri1, uri2, ..., uriN]
    operation = manager.CreateOperation("import: github://foo/bar", scope, progress)

OperationManager:
    dedupedScope = scope.Distinct()
    operation = new Operation(description, dedupedScope, registry, progress)
    operations[operation.Id] = operation
    return operation

Operation (constructor):
    log.Append(timestamp, "created", $"{description} ({scope.Count} files)", null)
    StartPollingTimer()
```

### Polling Cycle (every 500ms)

```
Operation:
    if (polling) return          // re-entrancy guard
    polling = true

    for each uri in scope:
        if not registry.TryGetValue(uri, out entry):
            // URI should exist - treat as failed
            log.Append(timestamp, "file_failed", "URI not found in registry", uri)
            failedCount++
            continue

        if entry.Status == Indexed && not yet logged indexed:
            log.Append(timestamp, "file_indexed", null, uri)
            indexedCount++

        if entry.Status == Failed && not yet logged failed:
            log.Append(timestamp, "file_failed", entry.Error, uri)
            failedCount++

        if entry.EmbeddingStatus == Embedded && not yet logged embedded:
            log.Append(timestamp, "file_embedded", null, uri)
            embeddedCount++

        if entry.EmbeddingStatus == NotApplicable && not yet logged ready:
            log.Append(timestamp, "file_ready", null, uri)
            embeddedCount++

        if entry.EmbeddingStatus == Failed && not yet logged embedding_failed:
            log.Append(timestamp, "embedding_failed", entry.Error, uri)
            failedCount++

    if progress != null:
        progress.Report(new OperationProgress(...))

    if (embeddedCount + failedCount) == totalFiles:
        CompleteOperation()

    polling = false
```

### Completion

```
Operation:
    StopPollingTimer()
    state = failedCount > 0 ? CompletedWithFailures : Completed
    completedAt = now
    log.Append(timestamp, "completed", $"{embeddedCount} ready, {failedCount} failed", null)
    completionSource.SetResult(progress)
```

### Cancellation

```
Caller:
    operation.Cancel()

Operation:
    if state != Running: return    // no-op if already terminal
    StopPollingTimer()
    state = Cancelled
    log.Append(timestamp, "cancelled", $"Cancelled at {embeddedCount}/{totalFiles}", null)
    completionSource.SetCanceled()
```

---

## SQL Surface (UDFs)

```sql
-- List all operations
SELECT * FROM _operations();
-- Returns: id, description, state, total_files, indexed_count, embedded_count, failed_count, ready_percent, created_at, completed_at

-- Get single operation
SELECT * FROM _operation('abc123');

-- Get operation log
SELECT * FROM _operation_log('abc123');
-- Returns: timestamp, type, message, uri

-- Active operations only
SELECT * FROM _operations() WHERE state = 'Running';

-- Failed files for an operation (indexing or embedding)
SELECT uri, message
FROM _operation_log('abc123')
WHERE type IN ('file_failed', 'embedding_failed');

-- Time to completion
SELECT datediff('ms', created_at, completed_at) as duration_ms
FROM _operations()
WHERE id = 'abc123';
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| URI not in registry | Log as `file_failed`, continue |
| UriRegistry throws | Skip poll cycle, retry next tick |
| Poll in progress when timer fires | Skip (re-entrancy guard) |
| Progress callback throws | Catch, log warning, continue |
| Cancel called on non-Running | No-op |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| In-memory storage | DuckDB persistence | Operations are transient; simplicity |
| Fixed scope | Expandable scope | Simpler completion semantics |
| 500ms polling | Event-driven | Simpler; UriRegistry doesn't need events |
| Structure embedding | Full embedding | Faster completion; structure sufficient for search |
| Retain until restart | Time-based expiry | Simpler; memory acceptable for session length |
| Dedup at creation | Reject duplicates | Simpler for callers; no error path |

## Alternatives Considered

**Event-driven completion:** UriRegistry raises events on status change. Rejected: adds coupling, polling is simple and sufficient at 500ms.

**Persisted operations:** Store in DuckDB for historical analysis. Rejected: adds complexity; can add later if needed.

**Cancelable with rollback:** Cancel deletes indexed files. Rejected: wasteful; separate unimport handles cleanup.

## Risks

| Risk | Mitigation |
|------|------------|
| Memory growth with many operations | Operations are small; thousands acceptable |
| Large scope slows polling | O(n) check is fast; 10k URIs in <10ms |
| Progress callback throws | Catch and log, don't fail operation |

## Extension Points

- **IProgress<T>** — Callers can provide custom progress handling
- **Entry types** — New types can be added without breaking existing queries
- **UDF surface** — Additional query patterns via new UDFs
