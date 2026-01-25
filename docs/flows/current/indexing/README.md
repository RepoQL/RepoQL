# Indexing Flows

How files flow through the indexing pipeline from discovery to queryable graph.

## Overview

```
Discovery          Hot Path (per-item)              Idle Processing
─────────────      ─────────────────────            ────────────────
startup-scan   →   catalog-gating                   pruning
file-watcher   →   classification                   embedding-generation
import         →   parsing                          multi-file-analysis
               →   single-file-analysis             index-rebuild
               →   commit-batching

Coordination: epoch-tracking, state-machine
Auxiliary: git-history-indexing, reindex
```

## Flow Index

### Discovery Phase

| Flow | Description |
|------|-------------|
| [startup-scan](startup-scan.md) | Full filesystem enumeration at host start |
| [file-watcher](file-watcher.md) | File change detection and incremental updates |
| [import](import.md) | External repository cloning and mounting |

### Hot Path (per-item processing)

| Flow | Description |
|------|-------------|
| [catalog-gating](catalog-gating.md) | Digest comparison for incremental indexing |
| [classification](classification.md) | File → SemanticMediaType resolution |
| [parsing](parsing.md) | Content → graph structure (nodes, edges, spans) |
| [single-file-analysis](single-file-analysis.md) | Per-file annotations (lint, metrics) |
| [commit-batching](commit-batching.md) | Batched persistence to DuckDB |

### Coordination

| Flow | Description |
|------|-------------|
| [epoch-tracking](epoch-tracking.md) | Work batching and idle detection |
| [state-machine](state-machine.md) | Pipeline stage busy/idle tracking |

### Idle Processing

| Flow | Description |
|------|-------------|
| [pruning](pruning.md) | Stale document detection and removal |
| [embedding-generation](embedding-generation.md) | Vector embeddings for semantic search |
| [multi-file-analysis](multi-file-analysis.md) | Cross-file annotations |
| [index-rebuild](index-rebuild.md) | Database index maintenance |

### Auxiliary

| Flow | Description |
|------|-------------|
| [git-history-indexing](git-history-indexing.md) | Commit and file change indexing |
| [reindex](reindex.md) | Forced re-processing with progress |

## Reading Order

For understanding the system:
1. `startup-scan` → how files enter the system
2. `epoch-tracking` → how work is batched
3. `catalog-gating` → how incremental indexing works
4. `classification` → `parsing` → `single-file-analysis` → `commit-batching` → the hot path
5. `state-machine` → how idle is detected
6. `pruning` → `embedding-generation` → `multi-file-analysis` → the idle path

For debugging:
- File not indexed? Start with `catalog-gating`
- Stale data? Check `pruning` and `reindex`
- Search not working? Check `embedding-generation`
- Missing cross-file data? Check `multi-file-analysis`

## Key Invariants

| Invariant | Flows Involved |
|-----------|----------------|
| Single-writer DuckDB access | `commit-batching` |
| Pruning before embedding | `pruning`, `embedding-generation` |
| Epochs never reused | `epoch-tracking` |
| ReadOnly items skip analysis | `single-file-analysis`, `multi-file-analysis` |
| ReadOnly items get embeddings | `embedding-generation` |

## Related Documentation

- [indexing.md](../indexing.md) - High-level pipeline overview with capsules
- [src/Indexing/RepoQL.Indexing/docs/](../../../../src/Indexing/RepoQL.Indexing/docs/) - Implementation details
  - `JOURNEY.md` - Single file traced through entire pipeline
  - `CONCEPTS.md` - Concept capsules
  - `STATE-MACHINE.md` - Detailed state transition documentation
