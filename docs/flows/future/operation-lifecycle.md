# Operation Lifecycle Flow

Track a batch of files from discovery to query-readiness, with progress visibility and performance telemetry.

## Why This Matters

When you import a repository or reindex files, you need to know when they're ready to query. Without operations, you're guessing.

| Without | With |
|---------|------|
| "Is my import done?" - check manually, hope | Await completion, get notified |
| "Why is it slow?" - no visibility | Progress shows what's happening |
| "What failed?" - grep logs | Query `_operation_log` for failures |
| "How long until ready?" - unknown | Progress shows completion percentage |

## Trigger

Any component that wants to track a batch of work calls `OperationManager.CreateOperation` with:
- **Description**: Human-readable identifier (convention: `"kind: detail"`, e.g., `"import: github://foo/bar"`)
- **Scope**: Set of URIs to track (must already be registered in UriRegistry)

Operations are agnostic — they don't know or care what triggered them. The description is purely for human readability and logging.

The operation starts immediately and begins polling.

## Stages

### 1. Creation
**Actor**: Caller (ImportService, RepoqlHost, etc.)
**Action**: Creates operation with scope
**Output**: Operation handle with ID, Completion task
**Failure**: None - creation is synchronous and infallible

**Precondition**: All URIs in scope are already registered in UriRegistry.

The operation:
1. Deduplicates scope by URI
2. Appends `created` entry to log
3. Starts polling timer

```
| Timestamp | Type | Message | Uri |
|-----------|------|---------|-----|
| 10:00:00.000 | created | "import: github://foo/bar (1,847 files)" | null |
```

### 2. Polling Loop
**Actor**: Operation (internal timer, every 500ms)
**Action**: Checks each URI in scope against UriRegistry
**Output**: Log entries appended, Progress updated
**Failure**: UriRegistry throws - skip cycle, retry next tick

For each URI in scope:

| Registry State | Entry Appended | Counts Toward |
|----------------|----------------|---------------|
| Not found | `file_failed` | FailedCount |
| Status = Indexing | (none) | (in progress) |
| Status = Indexed | `file_indexed` | IndexedCount |
| Status = Failed | `file_failed` | FailedCount |
| EmbeddingStatus = Embedded | `file_embedded` | EmbeddedCount |
| EmbeddingStatus = NotApplicable | `file_ready` | EmbeddedCount |
| EmbeddingStatus = Failed | `embedding_failed` | FailedCount |

Entries are appended only once per URI per transition (tracked internally).

Re-entrancy guard: if a poll is in progress when the timer fires, skip that tick.

### 3. Completion Check
**Actor**: Operation (after each poll)
**Action**: Check if all URIs are in terminal state
**Output**: State transition if complete
**Failure**: None

Terminal states for a file:
- Indexed + Embedded
- Indexed + NotApplicable
- Failed (indexing)
- Failed (embedding)

```
IsComplete = (EmbeddedCount + FailedCount) == TotalFiles
```

### 4a. Termination: Completed
**Actor**: Operation
**Action**: Stop polling, append `completed` entry, resolve TaskCompletionSource
**Output**: Completion task returns final progress

```
| Timestamp | Type | Message | Uri |
|-----------|------|---------|-----|
| 10:00:05.230 | completed | "1,844 ready, 3 failed (5.2s)" | null |
```

State is `Completed` if no failures, `CompletedWithFailures` if any failures.

### 4b. Termination: Cancelled
**Actor**: Caller (via `operation.Cancel()`)
**Action**: Stop polling, append `cancelled` entry, cancel TaskCompletionSource
**Output**: Completion task throws OperationCanceledException

```
| Timestamp | Type | Message | Uri |
|-----------|------|---------|-----|
| 10:00:02.100 | cancelled | "Cancelled at 892/1,847 (48%)" | null |
```

Cancel on non-Running operation is a no-op.

Already-indexed files remain indexed. Caller can:
- Restart later (new operation, indexer skips already-done files)
- Unimport to remove everything

## Flow Diagram

```mermaid
stateDiagram-v2
    [*] --> Running: CreateOperation(scope)

    Running --> Running: Poll (500ms)
    Running --> Completed: All files terminal, none failed
    Running --> CompletedWithFailures: All files terminal, some failed
    Running --> Cancelled: Cancel() called

    Completed --> [*]
    CompletedWithFailures --> [*]
    Cancelled --> [*]
```

```mermaid
sequenceDiagram
    participant Caller
    participant Op as Operation
    participant Reg as UriRegistry

    Caller->>Op: CreateOperation(scope)
    Note over Op: Append "created" entry
    Op-->>Caller: Operation handle

    loop Every 500ms
        Op->>Reg: TryGetValue(uri) for each
        alt File indexed
            Note over Op: Append "file_indexed"
        else File embedded
            Note over Op: Append "file_embedded"
        else File ready (N/A)
            Note over Op: Append "file_ready"
        else File failed
            Note over Op: Append "file_failed"
        else Embedding failed
            Note over Op: Append "embedding_failed"
        end
        Op->>Op: Update progress, check completion
    end

    Note over Op: Append "completed"
    Op-->>Caller: Completion task resolves
```

## Entry Types

| Type | Meaning | Message Contains |
|------|---------|------------------|
| `created` | Operation started | Description, file count |
| `file_indexed` | File finished indexing | (none) |
| `file_embedded` | File has structure embedding | (none) |
| `file_ready` | File ready (embedding not applicable) | (none) |
| `file_failed` | Indexing failed | Error message |
| `embedding_failed` | Embedding failed | Error message |
| `completed` | All files terminal | Summary stats, duration |
| `cancelled` | Operation cancelled | Progress at cancellation |

## Progress Derivation

Progress is computed from tracked counts (not re-derived from log):

```csharp
new OperationProgress(
    TotalFiles: scope.Count,
    IndexedCount: indexedCount,
    EmbeddedCount: embeddedCount,
    FailedCount: failedCount,
    ReadyPercent: totalFiles == 0 ? 100 : (embeddedCount + failedCount) * 100 / totalFiles
)
```

## Error Handling

| Error | Behaviour |
|-------|-----------|
| URI not in registry | Log as `file_failed`, continue |
| UriRegistry throws | Skip poll cycle, retry next tick |
| Poll in progress when timer fires | Skip (re-entrancy guard) |
| Progress callback throws | Catch, log warning, continue |
| Cancel on non-Running | No-op |

## Timing

| Phase | Duration |
|-------|----------|
| Creation | < 1ms |
| Poll cycle | 10-50ms depending on scope size |
| Poll interval | 500ms |
| Completion detection | Within 500ms of last file ready |

## Verification

| Environment | How |
|-------------|-----|
| **Unit tests** | Fake UriRegistry, advance through states, assert entries appended in order, completion fires |
| **Integration tests** | Real indexing, verify operation completes when files ready |
| **Production** | `_operation_log(id)` UDF shows full timeline; `_operations()` lists active |

**Test scenarios:**
1. Happy path - all files complete successfully
2. Partial failure - some files fail, operation completes with failures
3. Cancellation - cancel mid-way, verify state preserved
4. Empty scope - completes immediately with 100% ready
5. Large scope - performance test with 10k+ URIs
6. URI not in registry - logged as failed, operation continues

## SQL Surface

```sql
-- List active operations
SELECT * FROM _operations() WHERE state = 'Running';

-- Get operation details
SELECT * FROM _operation('abc123');

-- Full event log
SELECT * FROM _operation_log('abc123');

-- Failed files with errors (indexing or embedding)
SELECT uri, message, timestamp
FROM _operation_log('abc123')
WHERE type IN ('file_failed', 'embedding_failed');

-- Time to query readiness
SELECT datediff('ms', created_at, completed_at) as duration_ms
FROM _operations()
WHERE id = 'abc123';
```

## Related

- [Operations Design](../../designs/future/operations.md) — Architecture and contracts
- [UriRegistry](../../Schema.md#uri-registry) — Source of truth for file status
