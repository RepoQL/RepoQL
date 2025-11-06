# Overview

## Goals

Enable RepoQL to provide rich semantic understanding of code repositories through:

1. **Cross-file semantic analysis** - Understand relationships between files (call graphs, unused symbols, type hierarchies)
2. **Node-level semantic search** - Find specific methods, classes, and code blocks by semantic meaning
3. **Fresh embeddings** - Keep vector representations up-to-date as code changes
4. **Fast feedback** - Basic indexing completes in <5s; semantic analysis runs when system is idle

## SLOs

| Metric | Target | Current |
|--------|--------|---------|
| Parse → queryable | < 5s | < 5s ✅ |
| Embedding freshness | Every idle window | One-time after initial scan ❌ |
| Cross-file analysis | Enabled via workspace snapshots | Not supported ❌ |
| Hot path performance | Unchanged | N/A |

## Non-Goals

- **Real-time semantic analysis** - We use idle windows, not inline processing
- **HNSW vector indexes** - Current full-scan search is fast enough
- **Complex snapshot versioning** - Timestamps are sufficient for dirty tracking
- **Writer micro-batching** - Keep 1 document per transaction
- **Priority lanes** - FIFO processing is fine

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                       Hot Path (< 5s)                       │
├─────────────────────────────────────────────────────────────┤
│  File Edit → Discovery → Parsing → Writer → First-Pass    │
│              (hash)      (load)     (commit)   (analyze)    │
│                                                              │
│  Result: File is queryable with basic annotations          │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                  Idle Detection (500ms)                     │
└─────────────────────────────────────────────────────────────┘
                            ↓
        ┌───────────────────┴───────────────────┐
        ↓                                       ↓
┌──────────────────────┐            ┌────────────────────────┐
│  Semantic Analysis   │            │  Embedding Refresh     │
│  (cross-file)        │            │  (doc + node level)    │
├──────────────────────┤            ├────────────────────────┤
│ • Build workspace    │            │ • Find changed docs    │
│ • Find references    │            │ • Find changed nodes   │
│ • Detect unused APIs │            │ • Embed in batches     │
│ • Write annotations  │            │ • Update both tables   │
└──────────────────────┘            └────────────────────────┘
```

## Key Concepts

### Two-Tier Processing

**Hot Path (Phases 1-3)**
- Must complete quickly (< 5s target)
- Single-file operations only
- Gets documents queryable ASAP
- No blocking on expensive operations

**Idle Path (Phases 4-5)**
- Runs during quiet periods (500ms idle window)
- Multi-file operations allowed
- Expensive workspace builds OK
- Incremental: only process changed files

### Single Flow Object

One `IndexItem` object flows through the entire pipeline, accumulating state:

```
IndexItem {
  uri, path              // Identity
  → digest, type         // Discovery
  → document, records    // Parsing
  → committedAt          // Writer
  → annotations          // Analysis
}
```

Benefits:
- **Easier testing** - trace one object through stages
- **Better observability** - single trace ID for entire flow
- **Simpler debugging** - inspect object state at any stage

### Timestamp-Based Dirty Tracking

No complex todo tables or commit sequences. Simple approach:

```sql
-- Track last batch run
CREATE TABLE batch_state (
  name VARCHAR PRIMARY KEY,        -- 'embeddings', 'semantic_analysis'
  last_run_at TIMESTAMP NOT NULL
);

-- Find changed documents
SELECT n.id FROM node n
JOIN artifact a ON a.id = n.artifact_id
WHERE a.updated_at > (SELECT last_run_at FROM batch_state WHERE name='embeddings');
```

Tradeoffs:
- ✅ Simple to understand and implement
- ✅ Crash-safe (timestamp persisted in DB)
- ✅ Idempotent batches (re-processing is safe)
- ⚠️ No atomic batch claiming (not needed for single-writer model)

## Design Decisions

### 1. No HNSW Vector Indexes

**Decision:** Use JSON embeddings with full table scans (current approach).

**Rationale:**
- Current search performance is acceptable
- HNSW adds complexity (index builds, compaction, FLOAT arrays)
- Can add later if needed (non-breaking change)

**Performance:** 10-100ms for 10K documents is fine for MCP/CLI use cases.

### 2. Incremental Embeddings for Both Levels

**Decision:** Support document-level AND node-level embeddings.

**Use cases:**
- **Document embeddings** - "Find files about authentication" → file search
- **Node embeddings** - "Find methods that validate JWTs" → code search

**Tables:**
```sql
document_embedding(doc_id, embedding, ...)  -- Whole files
node_embedding(node_id, embedding, ...)     -- Methods, classes, headings
```

### 3. Simple Workspace Manager

**Decision:** Rebuild workspace on every semantic batch (optimize later).

**Rationale:**
- Ship first, measure performance
- Caching adds complexity (invalidation logic, memory usage)
- Roslyn workspace builds are reasonably fast (1-5s for medium repos)

**Future optimization:** Add caching with incremental updates once we have metrics.

### 4. Timestamp Comparison for Dirty Tracking

**Decision:** Compare `artifact.updated_at` vs `batch_state.last_run_at`.

**Alternatives considered:**
- ❌ Todo tables with `batch_id` - too complex for single-writer
- ❌ Global commit sequences - adds write overhead
- ✅ Timestamps - simple, works with existing schema

### 5. 500ms Quiet Window

**Decision:** Wait 500ms after all queues idle before triggering batches.

**Rationale:**
- Matches existing file watcher debounce (consistency)
- Prevents thrashing during rapid edits
- Long enough to batch multiple changes
- Short enough for responsive feedback

## Success Metrics

### Performance
- Hot path latency unchanged: < 5s for edit → queryable
- Embedding batch latency: < 1s for 100 documents
- Semantic batch latency: < 5s for 1000 documents (with warm workspace)

### Correctness
- No missing embeddings: all documents/nodes embedded within 10s of change
- No stale annotations: semantic analysis runs within 10s of idle
- No data loss: crash during batch doesn't lose committed changes

### Observability
- OpenTelemetry spans for all idle work
- Metrics for batch sizes, durations, success/failure rates
- Clear logs for idle detection and batch triggers

## Next Steps

See individual architecture documents:
1. [Pipeline Architecture](02-pipeline-architecture.md) - Detailed stage flows
2. [Data Structures](03-data-structures.md) - Schemas and objects
3. [Idle Processing](04-idle-processing.md) - Batch logic
4. [Workspace Management](05-workspace-management.md) - Multi-file analysis
5. [Migration Plan](06-migration-plan.md) - Implementation phases
