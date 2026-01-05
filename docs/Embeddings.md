# Embeddings in RepoQL

This document explains how vector embeddings are generated, stored, and queried in RepoQL to enable semantic search.

## Overview

RepoQL uses embeddings to enable semantic search - finding documents by meaning rather than just keywords. The embedding pipeline:

1. **Generates** embeddings during indexing (via ONNX local models or OpenRouter API)
2. **Stores** embeddings in the `document_embedding` table
3. **Indexes** embeddings using HNSW for fast approximate nearest neighbor search
4. **Queries** embeddings via the `search()` and `_search_semantic()` macros

## Embedding Providers

RepoQL supports multiple embedding providers via the `IEmbeddingProvider` interface:

```csharp
public interface IEmbeddingProvider
{
    string Model { get; }
    int Dimension { get; }
    bool Enabled { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
    Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default);
}
```

### OnnxEmbeddingProvider (Default - Local)

**File**: `src/RepoQL.Embeddings/OnnxEmbeddingProvider.cs`

- Uses BGE-small-en-v1.5 model (384 dimensions)
- Runs entirely locally via ONNX Runtime
- WordPiece tokenization with CLS pooling + L2 normalization
- Supports batch processing with efficient memory pooling

**Environment Variables:**
- `REPOQL_ORT_PROVIDER` - Execution provider (CPU, CUDA, DML, COREML). Defaults to CPU on Windows.
- `REPOQL_ORT_INTRA_THREADS` - Intra-op parallelism threads
- `REPOQL_ORT_INTER_THREADS` - Inter-op parallelism threads

### OpenRouterEmbeddingProvider (Cloud)

**File**: `src/RepoQL.LLM.Client/OpenRouterEmbeddingProvider.cs`

- Uses all-MiniLM-L6-v2 via OpenRouter API (384 dimensions)
- Activated when `OPENROUTER_API_KEY` environment variable is set
- Parallel batch processing with configurable concurrency

**Environment Variables:**
- `OPENROUTER_API_KEY` - Required for activation
- `REPOQL_OPENROUTER_CONCURRENCY` - Max concurrent API calls (default: 4, max: 16)

### HashedEmbeddingProvider (Testing)

**File**: `src/RepoQL.Embeddings/HashedEmbeddingProvider.cs`

- Deterministic hash-based embeddings for testing
- Fast, no external dependencies
- Not for production use

## Embedding Storage

### document_embedding Table

**File**: `src/RepoQL.Data.DuckDB/Schema/Tables/document_embedding.sql`

```sql
CREATE TABLE document_embedding (
    doc_id         UUID NOT NULL,
    node_id        UUID NOT NULL,
    chunk_index    INTEGER NOT NULL DEFAULT 0,
    embedding_type VARCHAR NOT NULL CHECK (embedding_type IN ('structure', 'full')),
    uri            VARCHAR NOT NULL,
    scope          VARCHAR NOT NULL CHECK (scope IN ('document', 'object')),
    model          VARCHAR NOT NULL,
    dim            INTEGER NOT NULL,
    embedding      FLOAT[] NOT NULL,
    start_byte     BIGINT,
    end_byte       BIGINT,
    updated_at     TIMESTAMP NOT NULL,
    PRIMARY KEY (doc_id, node_id, chunk_index, embedding_type)
);
```

**Key Fields:**
- `embedding_type`: `'structure'` (headline+structure summary) or `'full'` (full content chunks)
- `scope`: `'document'` or `'object'` (functions, classes, etc.)
- `chunk_index`: 0 for whole content or first chunk; 1+ for subsequent chunks
- `dim`: Embedding dimension (384, 768, or 1024)

### Embedding Types

| Type | Content | Use Case |
|------|---------|----------|
| `structure` | URI + headline + structure summary | Fast initial ranking |
| `full` | Full text content (chunked) | Detailed matching |

## Embedding Generation Pipeline

### VectorIndexCoordinator

**File**: `src/Indexing/RepoQL.Indexing/Indexing/PostProcessing/VectorIndexCoordinator.cs`

Orchestrates embedding generation during indexing:

1. **Structure Embeddings**: Generated from `headline` + `structure` fields
   - Batched (100 items per batch)
   - Progress reporting with ETA
   - Written immediately after each batch

2. **Full-text Embeddings**: Generated via `DuckDbVectorIndexRefresher`
   - Triggered after indexing completes
   - Only for documents without existing embeddings

**Embedding Mode** (controlled via `REPOQL_EMBEDDING_MODE`):
- `None` - No embeddings
- `StructureOnly` - Only structure embeddings
- `Full` - Both structure and full-text embeddings (default)

### Generation Flow

```
IndexItem → VectorIndexCoordinator.GenerateStructureEmbeddingsAsync()
         → IEmbeddingProvider.EmbedBatchAsync()
         → DuckDbDataStore.WriteEmbeddings()

         → VectorIndexCoordinator.RefreshVssIndexAsync()
         → VssIndexManager.RefreshIndexesAsync()
```

## Vector Search (HNSW)

### VssIndexManager

**File**: `src/RepoQL.Data.DuckDB/VssIndexManager.cs`

Manages ephemeral HNSW indexes for fast approximate nearest neighbor search:

- Uses DuckDB's VSS extension
- Creates dimension-specific index tables (`_vss_index_384`, `_vss_index_768`, `_vss_index_1024`)
- Indexes are rebuilt when embeddings change (with 30-second cooldown)
- Falls back to linear scan if VSS unavailable

**Performance**: HNSW reduces search from ~15s (linear scan) to <1s (ANN)

### VSS Index Tables

```sql
-- Created by vss_indexes.sql
CREATE TABLE _vss_index_384 (
    node_id UUID,
    doc_id UUID,
    embedding_type VARCHAR,
    vec FLOAT[384]
);
CREATE INDEX _vss_index_384_hnsw ON _vss_index_384 USING HNSW (vec) WITH (metric = 'cosine');
```

## Semantic Search

### _search_semantic Macro

**File**: `src/RepoQL.Data.DuckDB/Schema/Macros/search_semantic.sql`

The semantic search component:

1. **Query Embedding**: Converts query text to embedding via `embed_text()` UDF
2. **HNSW Fast Path**: Uses VSS index when available (384-dim)
3. **Linear Fallback**: Direct cosine similarity when HNSW unavailable
4. **Multi-source Scoring**: Combines structure and full-text embeddings

**Scoring Logic:**
- Uses whichever embedding type (structure vs full) scored higher
- 5% boost when both agree (reinforcement signal)

### Hybrid Search

**File**: `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql`

Combines lexical and semantic search:

```sql
-- Default weights
bm25_weight := 0.15,    -- BM25 lexical score
fuzzy_weight := 0.15,   -- Fuzzy matching
semantic_weight := 0.70 -- Semantic similarity
```

**Query Routing:**
- Empty query → 80% semantic weight (browse mode)
- Symbol queries → 30% reduction in semantic weight (precision mode)
- Regular queries → full semantic weight

## Diagnostics

### Check Embedding Status

```sql
-- Count embeddings by type
SELECT embedding_type, scope, dim, COUNT(*) as count
FROM document_embedding
GROUP BY embedding_type, scope, dim;

-- Check VSS index status
SELECT * FROM _vss_index_384 LIMIT 1;

-- Debug semantic search
SELECT * FROM _search_semantic('your query', k := 10);
```

### Verify Embedding Availability

```sql
-- Documents with embeddings
SELECT COUNT(DISTINCT doc_id) FROM document_embedding WHERE scope = 'document';

-- Documents without embeddings
SELECT COUNT(*) FROM node n
WHERE n.kind = 'document'
  AND NOT EXISTS (SELECT 1 FROM document_embedding de WHERE de.doc_id = n.id);
```

## Configuration Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `REPOQL_EMBEDDING_MODE` | `Full` | Embedding mode: None, StructureOnly, Full |
| `REPOQL_EMBED_CONCURRENCY` | `2` | Concurrent embedding refresh operations |
| `REPOQL_ORT_PROVIDER` | `CPU` (Win) | ONNX execution provider |
| `OPENROUTER_API_KEY` | - | Enables OpenRouter embeddings |
| `REPOQL_OPENROUTER_CONCURRENCY` | `4` | Max concurrent OpenRouter API calls |

## Architecture Diagram

```
┌─────────────────┐      ┌──────────────────┐      ┌─────────────────┐
│  IndexingEngine │──────▶│ VectorIndexCoord │──────▶│ IEmbeddingProv  │
└─────────────────┘      └──────────────────┘      └─────────────────┘
                                  │                         │
                                  ▼                         ▼
                         ┌──────────────────┐      ┌─────────────────┐
                         │ DuckDbDataStore  │      │ ONNX / OpenRouter│
                         └──────────────────┘      └─────────────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │document_embedding│
                         └──────────────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │  VssIndexManager │
                         └──────────────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │ _vss_index_384   │ (HNSW)
                         └──────────────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │ _search_semantic │
                         └──────────────────┘
                                  │
                                  ▼
                         ┌──────────────────┐
                         │    search()      │
                         └──────────────────┘
```
