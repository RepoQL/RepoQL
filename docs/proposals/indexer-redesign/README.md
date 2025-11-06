# Indexer Architecture Redesign

This proposal redesigns RepoQL's indexing pipeline to support:
- **Cross-file semantic analysis** (unused symbols, call graphs, type hierarchy)
- **Incremental embedding refresh** (document-level + node-level)
- **Idle-window batch processing** (expensive work runs during quiet periods)

## Documents

1. **[Overview](01-overview.md)** - Goals, SLOs, and high-level architecture
2. **[Pipeline Architecture](02-pipeline-architecture.md)** - Hot path vs idle path, stages, and flow
3. **[Data Structures](03-data-structures.md)** - IndexItem, schemas, and state tracking
4. **[Idle Processing](04-idle-processing.md)** - Idle detection, semantic analysis, and embeddings
5. **[Workspace Management](05-workspace-management.md)** - Multi-file analyzers and Roslyn integration
6. **[Migration Plan](06-migration-plan.md)** - Phased rollout and testing strategy

## Key Changes from Current System

### What Changes
- **Single flow object** (`IndexItem`) - one object flows through entire pipeline (easier testing)
- **Continuous incremental embeddings** - both document and node level, refreshed on every idle window
- **Cross-file analysis** - Roslyn workspace enables "find all references", unused symbol detection, etc.
- **Idle detection** - system continuously monitors for quiet periods to run expensive work
- **Timestamp-based dirty tracking** - simple `batch_state` table, no extra todo tables

### What Stays the Same
- Queue topology (discovery → parsing → writer → enrichment)
- Worker counts and bounded capacities
- Single-threaded writer (1 doc per transaction)
- All deduplication layers
- Distributed tracing and metrics
- Query gating (wait for idle before queries)

## Quick Reference

### SLOs
- First queries available after parse+commit: **< 5s**
- Semantic analysis runs during idle windows: **500ms quiet window**
- Embeddings stay fresh: **incremental updates every idle period**

### Performance Targets
- Hot path unchanged: **500-2000 files/sec** cold, **5000-20000 files/sec** warm
- Embedding batch: **8 documents/batch**, ~100ms per batch
- Semantic batch: **5000 URIs/batch**, workspace build ~1-5s

### Configuration
```bash
REPOQL_IDLE_QUIET_WINDOW_MS=500      # Idle detection window
REPOQL_EMBED_BATCH_SIZE=8            # Embeddings per transaction
REPOQL_SEMANTIC_BATCH_SIZE=5000      # Max URIs per semantic batch
```

## Design Principles

1. **Hot path stays fast** - No additional work in discovery/parsing/writer stages
2. **Idle path is opportunistic** - Semantic analysis and embeddings run when system is quiet
3. **Incremental by default** - Only process changed files, not full scans
4. **Simple state management** - Timestamps, not complex todo tables
5. **Testable stages** - Single flow object makes unit testing trivial
6. **Observable** - OpenTelemetry spans for all idle-time work

## Status

**Proposed** - This is a design proposal, not yet implemented.

See [Migration Plan](06-migration-plan.md) for implementation phases.
