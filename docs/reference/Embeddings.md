# Embeddings in RepoQL

This document explains how embeddings are generated, stored, and queried in RepoQL to enable semantic search.

## Overview

RepoQL uses embeddings to enable semantic search by meaning rather than keywords. The embedding pipeline:

1. Generates structure and full-content embeddings during indexing and idle refresh
2. Stores them in the `document_embedding` table
3. Queries them with exact cosine similarity over `document_embedding`
4. Combines semantic scores with lexical search in `search()`

## Embedding Providers

RepoQL supports multiple embedding providers through `IEmbeddingProvider`.

### OnnxEmbeddingProvider

**File**: `src/RepoQL.Embeddings/OnnxEmbeddingProvider.cs`

- Local ONNX model execution
- Default provider for developer-laptop usage
- Batch embedding support

### OpenRouterEmbeddingProvider

**File**: `src/RepoQL.LLM.Client/OpenRouterEmbeddingProvider.cs`

- Cloud-backed embeddings when configured
- Parallel batch processing with configurable concurrency

### HashedEmbeddingProvider

**File**: `src/RepoQL.Embeddings/HashedEmbeddingProvider.cs`

- Deterministic testing provider
- Not intended for production relevance quality

## Embedding Storage

Embeddings are stored in `document_embedding` with:

- `embedding_type`: `structure` or `full`
- `scope`: `document` or `object`
- `chunk_index`: `0..N` for chunked full embeddings
- `dim`: embedding dimension for mixed-model safety

Structure embeddings use URI + headline + structure. Full embeddings use document content, chunked when needed.

## Generation Pipeline

### EmbeddingCoordinator

**File**: `src/Indexing/RepoQL.Indexing/Indexing/PostProcessing/EmbeddingCoordinator.cs`

Coordinates post-index embedding work:

1. Generates structure embeddings eagerly and during idle catch-up
2. Runs targeted or full content embedding refresh via `DuckDbEmbeddingRefreshRunner`
3. Syncs embedding status back into `UriRegistry`
4. Performs startup content embedding catch-up for already-indexed repositories

### DuckDbEmbeddingRefreshRunner

**File**: `src/Indexing/RepoQL.Indexing/Indexing/PostProcessing/DuckDbEmbeddingRefreshRunner.cs`

- Refreshes full-content embeddings from DuckDB-backed documents
- Supports targeted document refresh and full refresh
- Removes dangling embeddings after refresh

### Generation Flow

```text
IndexItem
  -> EmbeddingCoordinator.GenerateStructureEmbeddingsAsync()
  -> DuckDbDataStore.WriteEmbeddings()
  -> EmbeddingCoordinator.ApplyAsync()
  -> DuckDbEmbeddingRefreshRunner.RefreshAsync()
```

## Semantic Search

### `_search_semantic`

**File**: `src/RepoQL.Data.DuckDB/Schema/Macros/search_semantic.sql`

The semantic search macro:

1. Embeds the query text
2. Filters stored embeddings to the matching dimension
3. Computes exact cosine similarity with `list_cosine_similarity`
4. Merges structure and full-content scores
5. Keeps the best chunk match for full embeddings
6. Calibrates scores before hybrid ranking

There is no ANN or HNSW layer. Semantic search is now exact linear similarity over `document_embedding`.

### Hybrid Search

**File**: `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql`

`search()` combines lexical and semantic scoring. Semantic results still participate in the same public APIs; only the internal execution path changed.

## Diagnostics

Useful checks:

```sql
SELECT embedding_type, scope, dim, COUNT(*) AS count
FROM document_embedding
GROUP BY embedding_type, scope, dim;

SELECT * FROM _search_semantic_explain('your query');
SELECT * FROM _search_linear_direct('your query', k := 10);
```

## Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `REPOQL_EMBEDDING_MODE` | `Full` | Embedding mode: `None`, `StructureOnly`, `Full` |
| `REPOQL_EMBED_CONCURRENCY` | `2` | Concurrent embedding refresh operations |
| `REPOQL_ORT_PROVIDER` | `CPU` on Windows | ONNX execution provider |
| `OPENROUTER_API_KEY` | unset | Enables OpenRouter embeddings |

## Architecture

```text
IndexingEngine
  -> EmbeddingCoordinator
  -> IEmbeddingProvider
  -> DuckDbDataStore
  -> document_embedding
  -> _search_semantic
  -> search()
```
