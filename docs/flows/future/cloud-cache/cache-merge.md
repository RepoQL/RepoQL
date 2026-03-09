# Cache Merge Flow

How newly computed embeddings move from the staging bucket into the permanent embeddings bucket — the write-behind path that keeps cache and compute on separate IAM boundaries.

## Why This Matters

| Without separate merge | With separate merge |
|------------------------|---------------------|
| Workers need write access to embeddings bucket | Workers write staging only — blast radius contained |
| Concurrent writes to same shard corrupt parquet | Writer appends independent part files — no conflicts |
| Failed writes leave partial files in production | Failed writes leave orphaned staging files, cleaned by lifecycle |

## Trigger

Cloud Tasks delivers a message containing a staging file path. The message was enqueued by the embedding service after computing new vectors.

```json
{ "path": "staging/source={hash}/model={model}/instance-abc-3f9a1c.parquet" }
```

---

## Stages

### 1. Message Receipt

**Actor**: Writer service (Cloud Run)
**Action**: Receive Cloud Tasks HTTP callback with staging file path
**Output**: Staging path to process
**Failure**: Cloud Tasks retries on non-2xx response (exponential backoff, max 1 hour)

### 2. Staging File Read

**Actor**: Writer service → GCS staging bucket
**Action**: Read the parquet file from staging
**Output**: Batch of `(sha256, vector, created_at)` rows (sha256 = hash of context + chunk content) with source and model metadata
**Failure**: File missing (already processed or expired) → acknowledge message, done

The source and model are encoded in the staging file path (`source={hash}/model={model}/`) — the writer extracts them to determine the target embeddings shard. No metadata parsing needed.

### 3. Shard Identification

**Actor**: Writer service
**Action**: Determine target shard path from source and model
**Output**: `gs://embeddings/source={source}/model={model}/`
**Failure**: N/A (path computation)

### 4. Append Part File

**Actor**: Writer service → GCS embeddings bucket
**Action**: Sort staging rows by sha256, write as a new part file in the target shard
**Output**: New part file `part-{timestamp}.parquet` in the shard
**Failure**: GCS write fails → Cloud Tasks retries the message

```
Existing shard:     part-0001.parquet (10K rows)
                    part-0002.parquet (8K rows)
New staging file:   42 rows

Result:             part-0003.parquet (42 rows, sorted by sha256)
```

The writer does NOT read or rewrite existing parts — it appends a new part file containing only the staging rows. This keeps the write small and fast. Compaction consolidates parts later.

Deduplication happens at read time (cache lookup takes first match via `DISTINCT ON`) and at compaction time (merge + dedupe into single file). The append only needs to ensure its own rows are sorted.

On first write to a new shard, the writer also creates `_source.json` containing `{ "origin": "{normalized_url}" }` for debugging and diagnostics. This is a best-effort write — missing metadata doesn't affect correctness.

### 5. Staging Cleanup

**Actor**: Writer service → GCS staging bucket
**Action**: Delete the processed staging file
**Output**: Staging file removed
**Failure**: Delete fails → file remains, cleaned by 24h lifecycle policy. No correctness issue.

### 6. Message Acknowledgement

**Actor**: Writer service → Cloud Tasks
**Action**: Return 2xx to acknowledge successful processing
**Output**: Message removed from queue
**Failure**: If any prior stage failed and we didn't acknowledge, Cloud Tasks retries

---

## Termination

Flow completes when:
- New vectors appended as a part file in the embeddings shard
- `_source.json` created if this is a new shard (best-effort)
- Staging file deleted (best-effort)
- Cloud Tasks message acknowledged

## Flow Diagram

```mermaid
sequenceDiagram
    participant Tasks as Cloud Tasks
    participant Writer as Writer Service
    participant Staging as GCS Staging
    participant Embeddings as GCS Embeddings

    Tasks->>Writer: HTTP callback (staging path)
    Writer->>Staging: Read parquet file

    alt File exists
        Writer->>Writer: Sort rows by sha256
        Writer->>Embeddings: Write new part file
        Writer->>Staging: Delete staging file
        Writer-->>Tasks: 200 OK
    else File missing
        Writer-->>Tasks: 200 OK (already processed)
    end
```

## Failure Semantics

Every failure mode resolves cleanly:

| Failure point | What happens | Resolution |
|---------------|-------------|------------|
| Writer crashes mid-read | Message not acknowledged | Cloud Tasks retries → writer re-reads staging file |
| Writer crashes after append, before staging delete | Message not acknowledged | Cloud Tasks retries → re-append is idempotent (sha256 dedup at read/compaction) |
| Writer crashes after staging delete, before ack | Message not acknowledged | Cloud Tasks retries → staging file gone → ack immediately |
| Staging file expired (24h lifecycle) | File missing on retry | Ack message → embedding will be cached on next request |
| Embeddings write fails (GCS error) | Exception → non-2xx | Cloud Tasks retries with backoff |
| Duplicate messages (Cloud Tasks at-least-once) | Same staging file processed twice | sha256 dedup at compaction → no corruption |

The key property: **at-least-once delivery + idempotent append + sha256 dedup = exactly-once semantics for the cache.**

## Error Handling

| Error | Behaviour |
|-------|-----------|
| Staging file not found | Acknowledge message (already processed or expired) |
| Staging file corrupted | Log error, acknowledge message (don't retry corrupt data) |
| GCS embeddings write timeout | Don't acknowledge → Cloud Tasks retries |
| Concurrent writers to same shard | Each writes a separate part file — no conflict. Compaction merges later. |

## Timing

| Phase | Duration |
|-------|----------|
| Staging file read | ~50-100ms |
| Sort + write new part | ~50-200ms (depends on batch size) |
| Staging delete | ~30-50ms |
| End-to-end | ~150-400ms per message |
| Cloud Tasks delivery latency | ~100-500ms from enqueue |

## Verification

| Environment | How |
|-------------|-----|
| **Local** | Writer watches a local directory instead of Cloud Tasks. Moves files from staging/ to embeddings/. Same logic, no GCS. |
| **Automated tests** | Enqueue known staging file. Assert part file appears in correct shard. Assert staging file deleted. Assert duplicate message produces no corruption. |
| **Production** | Messages processed/second. Staging bucket depth (should trend to zero). Failed message count. Shard part file count (input to compaction trigger). |

## Related

- `embedding-request.md` — upstream flow that produces staging files
- `compaction.md` — downstream flow that consolidates part files
