# DuckDB Search Macros

These notes live alongside the DuckDB implementation so macro changes stay co-located with the SQL. Everything referenced here sits under `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql`.

## Architecture Overview

RepoQL search combines three scoring strategies into a unified ranking:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                            search(q, ...)                                │
├─────────────────────────────────────────────────────────────────────────┤
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐                │
│  │   Lexical    │   │    Fuzzy     │   │   Semantic   │                │
│  │  (BM25-ish)  │   │ (subsequence)│   │ (embeddings) │                │
│  └──────┬───────┘   └──────┬───────┘   └──────┬───────┘                │
│         │                  │                  │                         │
│         ▼                  ▼                  ▼                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                    combine(bm25n, fuzzn, semn)                   │   │
│  │            score = wb*bm25n + wf*fuzzn + ws*semn                 │   │
│  └─────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────┘
```

### Why `repo_index` Instead of FTS

The search macros query the `repo_index` VIEW rather than using DuckDB's FTS extension on `document_search`. Here's why:

| Requirement | `repo_index` + custom scoring | `document_search` + FTS |
|-------------|------------------------------|------------------------|
| Search objects (functions/classes) | ✅ Has both `document` and `object` scope | ❌ Documents only |
| Search code content | ✅ `body` field with actual code | ❌ Path components only |
| Semantic similarity | ✅ `embedding` column for cosine similarity | ❌ No embeddings |
| Fuzzy subsequence matching | ✅ `match_score()` UDF | ❌ Token-based BM25 only |
| Symbol exact-match boost | ✅ Custom heuristics (4.0 for exact) | ❌ No special handling |
| Multi-chunk documents | ✅ Scores all chunks, takes MAX | ❌ N/A |

The `document_search` table exists for data integrity (tracks document URIs for cleanup) but is not used for search queries.

### Data Model

**`repo_index` VIEW** - Unified searchable index:
```sql
SELECT
    doc_id, node_id,           -- Identity
    uri, path, search_key,     -- Path components
    basename, dirname,
    symbol, symbol_key,        -- Function/class names
    headline, structure,       -- Metadata summaries
    body,                      -- Actual code/text content
    embedding,                 -- Semantic vector (chunk_index=0)
    scope,                     -- 'document' | 'object'
    lang, mime, kind,          -- Type info
    line_start, line_end,      -- Location in file
    mtime, digest
FROM document_rows UNION ALL object_rows
```

**Key insight**: `repo_index` contains BOTH files (documents) AND symbols within files (objects). This enables searching for `AuthController` and finding the class definition, not just files containing "auth".

## Scoring Model

### Lexical Score (`bm25_score`)

Position-based heuristics that reward exact and partial matches:

| Match Type | Score |
|------------|-------|
| Exact symbol match | 4.0 |
| Symbol contains query | 3.2 |
| Basename equals query | 3.0 |
| Basename contains query | 2.0 |
| Path contains query | 1.0 |
| Body contains query | 0.5 |
| No match (fallback) | 0.05 |

Normalized to `[0,1]` via `zero_one()` window function.

### Fuzzy Score (`fuzzy_score`)

The `match_score(pattern, text)` UDF computes subsequence matching:
- Finds pattern characters in order within text
- Rewards consecutive matches (+1.5)
- Rewards word boundary matches (+0.8)
- Penalizes gaps between matches
- Returns 0-5, clamped

This catches typos and partial matches that lexical heuristics miss.

### Semantic Score (`dense_score`)

Embedding-based similarity using cosine distance:
1. Query is embedded via `embed_text_json()`
2. All document embeddings scored via `cosine_similarity_json()`
3. Multi-chunk documents: score ALL chunks, take MAX
4. Normalized with power transform: `POWER(GREATEST(sem/max_sem, 0), 1.5)`

The `GREATEST(..., 0)` clamp prevents NaN from negative cosine similarities (opposite vectors).

### Combined Score

```sql
score = combine(bm25n, fuzzn, semn, wb, wf, ws)
     = wb*bm25n + wf*fuzzn + ws*semn
```

Default weights vary by route:
- **auto**: 45% lexical, 35% fuzzy, 20% semantic
- **heavy** (questions): 20% lexical, 0% fuzzy, 80% semantic
- **symbol**: Reduced semantic weight (symbols are best found lexically)

## `search(...)`

Primary macro that blends lexical heuristics, fuzzy subsequence matching, and semantic embeddings.

1. **`base_params`** – trims/lowercases every input (query text, mode, k, glob filters, weights). All downstream CTEs reference these normalized scalars instead of macro parameters; this prevents DuckDB from flagging correlated LIMIT/OFFSET expressions.
2. **`classified`** – infers the route (`symbol`, `heavy`, `error`, etc.) and builds lowercase keywords. Simple heuristics (presence of `::`, stack traces, length) keep this cheap.
3. **`config`** – maps the route to weights (`bm25_w`, `fuzzy_w`, `effective_sem_weight`) and candidate limits (`lex_limit`, `dense_limit`). Heavy queries double dense candidates; symbol queries clamp both.
4. **`filtered_source` / `filtered`** – reads `repo_index`, materializes a lowercased `uri_local`, and filters by the normalized `uri_glob_filter` / `mime_glob_filter`. When a URI glob is provided we constrain scope to `document` to avoid object spam.
5. **Lexical scorer** (`score_source`, `ranked_lex`, `normalized_lex`, `lex_rrf`) – computes heuristic BM25-style boosts, fuzzy subsequence scores, normalizes them into `[0,1]`, and produces an RRF component.
6. **Semantic scorer** (`semantic_seed`, `qv`, `sem_all_chunks`, `sem_scored`, `sem_top`, `sem_norm`, `sem_rrf`) – embeds the query once, scores ALL chunks per document (not just chunk 0), aggregates to MAX, rescales to `[0,1]` with power transform, and yields its own RRF value.
7. **`union_nodes` / `fallback_nodes` / `final_nodes`** – unions lexical+dense hits, falls back to most-recent documents when both sets are empty, and enforces the caller's `k` using row numbers obtained from deterministic `base_params` values.
8. **Projection** – joins `filtered` rows with `classified`/`config` so diagnostics travel with the results: `bm25_score`, `fuzzy_score`, `dense_score`, combined score, confidence bucket, and JSON metadata (`boosts_json`, `explain_json`).

## `related(...)`

Helper macro for "more like this" queries.

- `base_params` normalizes the seed URI, mode, limit, and glob filters.
- `seed` loads the target row once; `related_source` excludes it and materializes `uri_local`.
- `filtered` reuses the exact glob checks from `search`, guaranteeing the same semantics.
- `scored` combines cosine similarity (when embeddings exist) with `match_score` as a lexical fallback.
- `final` ranks by the blended score and records `rel_row`; `limited` enforces `k` by slicing on that row number rather than issuing `LIMIT k` (again avoiding correlated expressions).
- Final projection joins `base_params` so the explain JSON reports the normalized mode + seed.

## `file_search(...)`

Thin wrapper that concatenates keyword + question text, switches to `heavy` mode when a question is present, and delegates directly to `search(...)`. Because `search` owns input normalization, this macro simply passes `k` / `max_cand` through.

## `object_search(...)`

Two-phase search optimized for finding functions/classes, with chunk-guided filtering and dynamic scaling to minimize JIT embedding cost.

**Parameters:**
| Parameter | Default | Description |
|-----------|---------|-------------|
| `q` | required | Search query |
| `k` | 20 | Max results to return |
| `file_candidates` | 10 | Max files to consider from semantic search |
| `uri_glob` | NULL | Optional URI pattern filter |
| `mime_glob` | NULL | Optional MIME type filter |
| `chunks_per_file` | 10 | Max document chunks per file (high, cheap pre-computed check) |
| `max_embed_candidates` | 20 | Base max objects to JIT embed (scaled dynamically) |

**Algorithm:**

1. **Phase 1a**: Find candidate files using `file_search()` with pre-computed document embeddings
2. **Phase 1b/1c**: Add files with lexical matches (symbol/headline/structure/body)
3. **Phase 1d**: Score document chunks within candidate files against the query
4. **Phase 1e**: Calculate dynamic embed limit based on chunk strength
5. **Phase 2a**: Filter objects to those whose byte ranges overlap with high-scoring chunks
6. **Phase 2b**: JIT-embed the filtered objects (headline + body), rank by similarity

**Chunk-guided optimization:**
The key optimization is using pre-computed document chunk embeddings as "clues" to identify relevant regions within files. Instead of embedding all objects in candidate files (up to 200), we:
- Score each document chunk against the query (cheap, pre-computed)
- Keep top N chunks per file (default 10, just a safety valve)
- Only embed objects whose span byte range overlaps these "hot zones"

**Dynamic scaling:**
The JIT embedding budget scales based on chunk hit quality:
| Max Chunk Score | Effective Limit | Rationale |
|-----------------|-----------------|-----------|
| ≥ 0.6 (strong)  | 2× base (max 50) | High confidence, worth exploring more |
| 0.35-0.6 (moderate) | base (20) | Normal search |
| 0.2-0.35 (weak) | 0.5× base (min 10) | Low confidence, save JIT cost |
| < 0.2 (very weak) | 10 | Rely on lexical matching |

This typically reduces JIT embeddings from ~200 to ~10-50, dramatically improving search latency while maintaining accuracy.

**Fallback behavior:**
- Objects with strong lexical matches (priority >= 60) bypass chunk filtering
- Files without chunk embeddings (small/non-chunked files) include all their objects
- Objects in files with no matching chunks still included if they have lexical matches

## Debugging Tips

- Run the macros as subqueries, e.g. `SELECT * FROM search('sample', uri_glob := '*/docs/*') LIMIT 5;` DuckDB will inline the CTEs; comment out the final projection to inspect intermediate stages.
- Inspect `boosts_json` / `explain_json` to see which route triggered and how many lexical/dense candidates were considered.
- When investigating glob behavior, query `filtered_source` to compare `uri` vs. `uri_local` and confirm the normalized filters in `base_params` are what you expect.
- Check for NaN/infinity in scores: `SELECT * FROM search(...) WHERE isnan(score) OR isinf(score)`
