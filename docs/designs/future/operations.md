# Operations Design

## North Star

Know when your files are ready to query. See what's happening. Know what went wrong. Measure how long it took.

## Context

Operations track batches of indexing work to completion. When you import a repository, reindex files, or start the host, you get an operation that tells you when those specific files are queryable.

**Enables:** [Operation Lifecycle Flow](../flows/operation-lifecycle.md)

**Built on:** [UriRegistry](../Schema.md#uri-registry) — source of truth for file status

## Constraints

- In-memory only — operations are transient, not persisted
- Fixed scope — URIs defined at creation, immutable
- Polling-based — checks UriRegistry every 500ms
- Structure embedding — complete when indexed + structure embedded (not full-text)
- Agnostic — operations don't know or care what triggered them; behavior is identical regardless of use case

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
│  - Appends entries to log                                   │
│  - Updates progress, fires IProgress<T>                     │
│  - Resolves TaskCompletionSource on completion              │
└─────────────────────────────────────────────────────────────┘
         │                                    │
         ▼                                    ▼
┌─────────────────────┐            ┌─────────────────────┐
│    UriRegistry      │            │   OperationLog      │
│                     │            │                     │
│  - File status      │            │  - Append-only      │
│  - Embedding status │            │  - Timestamped      │
│  - Source of truth  │            │  - Queryable        │
└─────────────────────┘            └─────────────────────┘
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
    /// </summary>
    /// <param name="description">Human-readable description (convention: "kind: detail", e.g., "import: github://foo/bar")</param>
    /// <param name="scope">URIs to track (immutable after creation)</param>
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
    int ReadyPercent);

public record OperationEntry(
    DateTimeOffset Timestamp,
    string Type,
    string? Message,
    RepoUri? Uri);
```

---

## Data Flow

### Creation

```
Caller:
    scope = [uri1, uri2, ..., uriN]
    operation = manager.CreateOperation("import: github://foo/bar", scope, progress)

OperationManager:
    operation = new Operation(description, scope, registry, progress)
    operations[operation.Id] = operation
    return operation

Operation:
    log.Append(timestamp, "created", $"{description} ({scope.Count} files)", null)
    StartPollingTimer()
```

### Polling Cycle (every 500ms)

```
Operation:
    for each uri in scope:
        entry = registry.TryGetValue(uri)

        if entry.Status == Indexed && not yet logged:
            log.Append(timestamp, "file_indexed", null, uri)
            indexedCount++

        if entry.EmbeddingStatus == Embedded && not yet logged:
            log.Append(timestamp, "file_embedded", null, uri)
            embeddedCount++

        if entry.Status == Failed && not yet logged:
            log.Append(timestamp, "file_failed", entry.Error, uri)
            failedCount++

    progress.Report(new OperationProgress(...))

    if (embeddedCount + failedCount) == totalFiles:
        CompleteOperation()
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
    if state != Running: return
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
-- Returns: id, description, state, total_files, indexed, embedded, failed, created_at, completed_at

-- Get single operation
SELECT * FROM _operation('abc123');

-- Get operation log
SELECT * FROM _operation_log('abc123');
-- Returns: timestamp, type, message, uri

-- Active operations only
SELECT * FROM _operations() WHERE state = 'Running';

-- Failed files for an operation
SELECT uri, message
FROM _operation_log('abc123')
WHERE type = 'file_failed';

-- Time to completion
SELECT datediff('ms', created_at, completed_at) as duration_ms
FROM _operations()
WHERE id = 'abc123';
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| UriRegistry unavailable | Skip poll cycle, continue |
| URI disappears from registry | Log as failed, continue |
| Caller disposes operation | Cancel gracefully |
| Poll takes >500ms | Skip next tick, don't queue |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| In-memory storage | DuckDB persistence | Operations are transient; simplicity |
| Fixed scope | Expandable scope | Simpler completion semantics |
| 500ms polling | Event-driven | Simpler; UriRegistry doesn't need events |
| Structure embedding | Full embedding | Faster completion; structure sufficient for search |
| Retain until restart | Time-based expiry | Simpler; memory acceptable for session length |

## Alternatives Considered

**Event-driven completion:** UriRegistry raises events on status change. Rejected: adds coupling, polling is simple and sufficient at 500ms.

**Persisted operations:** Store in DuckDB for historical analysis. Rejected: adds complexity; log if needed for analytics.

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
