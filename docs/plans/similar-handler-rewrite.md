---
description: Rewrite SimilarHandler to work with cloud embeddings and eliminate dimension mismatch bugs
tags: [similar, embeddings, read-modifier, cloud, voyage]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: SimilarHandler Rewrite

Implements: Feedback item #9 — `similar` modifier returns 0.00 similarity with cloud embeddings.

## Scope

**Covers:**
- `SimilarHandler` — complete rewrite
- `SimilarHandlerTests` — new tests covering cloud embedding scenarios
- DI registration (unchanged interface, same `IModifierHandler`)

**Does not cover:**
- `FindHandler` (working, has adaptive threshold already)
- Embedding pipeline or `EmbeddingRefresher` (working correctly)
- `embed_passage()` UDF (cloud-aware query embedding is a separate concern)
- `help://` documentation for the `similar` modifier (update after rewrite lands)

## Enables

- `similar` modifier works with cloud embeddings (voyage-4-lite, voyage-context-3)
- Mixed embedding environments (some files local ONNX, some cloud) produce valid results
- Agents can discover structurally related code without false "no results" responses

## Prerequisites

- `document_embedding` table populated (existing pipeline, working)
- `DuckDbDataStore.Query()` for read queries (existing, working)
- `UriRegistry` for seed validation (existing, working)

## North Star

The `similar` modifier produces useful ranked results whenever the seed and candidates have stored embeddings, regardless of which embedding model generated them. Dimension mismatches are handled gracefully — never silently producing 0.00. The query cost is proportional to the scope, not the entire graph.

## Done Criteria

### Core similarity search

- The SimilarHandler shall compare stored seed embeddings against stored candidate embeddings without re-embedding
- The SimilarHandler shall filter `document_embedding` rows by matching `dim` so that only same-dimension vectors are compared
- When the seed URI has no stored embeddings, the SimilarHandler shall return an actionable error naming the embedding status
- When no candidates share the seed's embedding dimension, the SimilarHandler shall return an actionable error stating the dimension mismatch

### Scope filtering in SQL

- The SimilarHandler shall push the candidate URI scope filter into the SQL query rather than filtering in C# after a full-graph CROSS JOIN
  - Pass `documentUris` as a CTE or temp table joined in the query
  - The `chunk_pairs` CTE shall only scan candidates within scope

### Fragment handling

- When the seed URI has a `#symbol=` fragment, the SimilarHandler shall select seed chunks whose byte ranges overlap the symbol's span
- When the seed URI has a `#line=` fragment, the SimilarHandler shall select seed chunks whose byte ranges overlap the line range
- When the seed URI has no fragment, the SimilarHandler shall use all stored seed chunks
- The SimilarHandler shall never call `embed_passage()` — all comparisons use stored embeddings only

### Adaptive threshold

- The SimilarHandler shall use an adaptive similarity threshold: `max(floor, topScore * fraction)` where floor and fraction are constants
  - Floor: `0.01` (cosine similarity floor — cloud embeddings produce lower absolute values)
  - Fraction: `0.50` (show results within 50% of the best match)
- When all results fall below the adaptive threshold, the SimilarHandler shall report the best similarity score in the response

### Error handling

- If the similarity SQL query throws an exception, the SimilarHandler shall include the exception type and message in the returned content (not silently return empty)
- The SimilarHandler shall log warnings for query failures

### Output format

- The SimilarHandler shall preserve the existing output format: URI with similarity score, headline, snippet with line numbers, footer with counts
- The SimilarHandler shall respect the token budget using the same progressive-fit approach (try adding results until budget exceeded)

## Constraints

- **Interface**: Must implement `IModifierHandler` with the same signature — the modifier dispatch system is not changing
- **Single writer**: Read-only queries against `document_embedding`. No writes.
- **No re-embedding**: Do not call `embed_passage()` or any embedding UDF. The insight: stored embeddings are the source of truth. If a chunk doesn't have a stored embedding, skip it — don't fabricate one at the wrong dimension.
- **Schema frozen**: No changes to `document_embedding` table. The `dim` column already exists for filtering.

## Approach

The rewrite eliminates three CTEs (`seed_range`, `seed_chunks`, `chunk_pairs` with full-graph CROSS JOIN) and replaces them with a targeted query:

```
1. Resolve seed dimension: SELECT DISTINCT dim FROM document_embedding WHERE uri = <seed>
2. Get seed embeddings (filtered by fragment overlap if present)
3. Get candidate embeddings (filtered by scope URIs AND matching dim)
4. Compute list_cosine_similarity between seed × candidate chunks
5. best-per-document aggregation (same as current)
6. Return ranked results
```

For fragment resolution, reuse the existing `span` table lookup (symbol → byte range, line → byte range) but only to filter which stored seed chunks to use — never to re-embed.

The scope filter uses a VALUES list CTE for the candidate URIs (typically < 100 URIs from the read pattern), joined into the query so DuckDB can push down the filter.

## References

- `src/RepoQL.ConsoleApp/Host/SimilarHandler.cs` — current implementation (rewrite target)
- `src/tests/RepoQL.Tests/SimilarHandlerTests.cs` — current tests (expand significantly)
- `src/RepoQL.ConsoleApp/Host/FindHandler.cs` — adaptive threshold pattern to follow
- `src/RepoQL.Read/ModifierDispatcher.cs:277` — `IModifierHandler` interface
- `src/RepoQL.Data.DuckDB/Schema/Tables/document_embedding.sql` — table schema with `dim` column
- `src/RepoQL.Data.DuckDB/EmbeddingRefresher.cs` — how embeddings are stored (byte ranges, dimensions)

## Error Policy

- SQL query failures: catch, log warning, return error message in `ModifierResult.Content` with exception type. Never return empty results silently.
- Seed validation failures (not found, not indexed, no embeddings): return actionable message as today, these paths are fine.
- Dimension mismatch (seed has embeddings but no candidates share the dimension): specific error message, not generic "no results."
