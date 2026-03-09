---
description: Cloud Run writer service — processes staging files into permanent embedding shards, triggered by Eventarc.
tags: [plan, cloud-cache, writer, cloud-run, eventarc]
audience: { human: 35, agent: 65 }
categories: ["Plan[95%]", "Design[5%]"]
---

# Plan: Writer Service

Implements: [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Writer Service contract, GCS Path Convention

## Scope

**Covers:**
- Cloud Run service triggered by Eventarc (GCS OBJECT_FINALIZE → Pub/Sub → Cloud Run)
- `CacheMergeHandler` — read staging parquet, append part file to embeddings shard
- `_source.json` creation on first write to a new shard
- Staging file cleanup after successful append
- Compaction threshold check — enqueue compaction task when part count exceeds 20
- Dockerfile and Cloud Run deployment configuration
- Tests for append, dedup safety, and idempotent retry

**Does not cover:**
- GCS bucket creation or IAM (Plan: 01-infrastructure — prerequisite)
- Staging file creation (Plan: 03-cache-layer — upstream)
- Compaction logic (Plan: 05-compaction — downstream)
- Writer scaling or sharding by source prefix (extension point, not v1)

## Enables

Once the writer exists:
- **Staging → permanent path complete** — new vectors move from staging to queryable embeddings
- **Cache hits on subsequent requests** — vectors appended by the writer are found by cache lookups
- **Plan: 05-compaction** can proceed — operates on the part files this plan creates
- **IAM separation enforced** — embedding service never writes to embeddings bucket; writer does

## Prerequisites

- Plan 01 complete — GCS buckets, IAM service accounts and Eventarc IAM grants exist
- Plan 03 in progress or complete — staging files being produced (writer can be deployed before cache layer is live; it simply has no events to process)
- deploy-embedding-writer workflow creates the Eventarc trigger (references the Cloud Run service)
- .NET Cloud Run project template
- `Google.Cloud.Storage.V1` NuGet package for GCS operations
- Parquet read/write library compatible with the staging schema

## North Star

A staging file becomes a queryable cache entry within seconds. The writer is invisible — it never fails in a way that loses data, never requires manual intervention, never blocks the hot path.

## Done Criteria

### Merge Endpoint

- The writer shall expose an HTTP POST merge endpoint
- The endpoint shall accept both CloudEvent payloads (production, detected via `ce-type` header) and direct JSON payloads (local dev via `DirectWriterUrl`)
- For CloudEvent payloads: extract the staging object path from the GCS OBJECT_FINALIZE event data (`bucket` + `name` fields)
- For direct JSON payloads: extract the staging path from `{ "path": "source={hash}/model={model}/instance-{id}-{uuid}.parquet" }`
- The writer shall return 200 to acknowledge successful processing
- When processing fails with a retryable error, the writer shall return non-2xx to trigger Pub/Sub retry
- The writer shall validate the staging path format before processing
  - If the path doesn't match the expected pattern, return 200 (acknowledge, don't retry bad events)

### Staging File Read

- The writer shall read the parquet file from the staging bucket at the path in the event
- The writer shall extract source hash and model from the staging path (no metadata parsing needed)
- When the staging file is missing (already processed or expired), the writer shall return 200
- When the staging file is corrupted (invalid parquet), the writer shall log error and return 200 (don't retry corrupt data)

### Part File Append

- The writer shall sort staging rows by sha256
- The writer shall write a new part file as `part-{timestamp}.parquet` in the target embeddings shard
  - Path: `gs://embeddings/source={source_hash}/model={model}/part-{timestamp}.parquet`
- The writer shall NOT read or rewrite existing part files — append only
- The parquet file shall use zstd compression
- When GCS write fails, the writer shall return non-2xx for Pub/Sub retry

### `_source.json` Metadata

- On first write to a new shard (no existing part files), the writer shall create `_source.json`
- The file shall contain `{ "origin": "{normalized_url}" }` where the origin is extracted from the staging file metadata or a separate header
  - If origin is unavailable, skip `_source.json` creation (best-effort, not correctness)
- The `_source.json` write shall be best-effort — failure does not affect part file append

### Staging Cleanup

- After successful part file append, the writer shall delete the staging file
- When staging delete fails, log warning and continue — 24h lifecycle policy handles cleanup
- The staging delete shall happen after the part file write, never before

### Compaction Trigger

- After appending a part file, the writer shall list part files in the shard
- When the part count exceeds 20, the writer shall enqueue a Cloud Tasks compaction message: `{ "source": "{hash}", "model": "{model}" }`
- When the compaction enqueue fails, log warning and continue — nightly scheduled compaction catches it

### Idempotency

- When the same staging file is processed twice (Pub/Sub at-least-once delivery), the writer shall produce a duplicate part file
  - Duplicate sha256 entries are harmless — resolved by `DISTINCT ON` at read time and dedup at compaction
- The writer shall not attempt deduplication — it only appends

### Cloud Run Configuration

- The writer shall scale to zero when idle
- The writer shall use the writer service account (read staging, write embeddings, delete staging)
- The writer shall have HMAC keys for DuckDB if DuckDB is used for parquet I/O, or use direct parquet library otherwise

## Constraints

- **Append-only** — design explicitly chose no merge with existing parts; compaction handles consolidation
- **Writer never reads existing parts** — this is critical for concurrency safety; multiple writers can append to the same shard without conflict
- **Source hash from path, not content** — the staging path encodes source and model; the writer extracts them without parsing the parquet file's content
- **Timestamp in part file name** — must be high-resolution enough to avoid collisions from concurrent writers (e.g., Unix timestamp with milliseconds or UUID suffix)

## References

- [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Writer Service contract, GCS Path Convention
- [Cloud Cache Flows: Cache Merge](../../../flows/future/cloud-cache/cache-merge.md) — stage-by-stage flow
- [Eventarc GCS triggers](https://cloud.google.com/eventarc/docs/run/create-trigger-storage-gcloud) — OBJECT_FINALIZE events to Cloud Run
- [Cloud Tasks](https://cloud.google.com/tasks/docs/creating-http-target-tasks) — used for compaction dispatch (not merge trigger)

## Error Policy

The writer is designed for safe retry. Every failure mode resolves:

| Failure | Response code | Resolution |
|---------|--------------|------------|
| Staging file missing | 200 | Already processed or expired — acknowledge |
| Staging file corrupt | 200 | Don't retry bad data — acknowledge, log |
| GCS embeddings write fails | 500 | Pub/Sub retries with backoff |
| Staging delete fails | 200 | Part file written successfully — 24h lifecycle handles cleanup |
| Writer crashes mid-processing | N/A | No ack — Pub/Sub retries — re-append is idempotent |
