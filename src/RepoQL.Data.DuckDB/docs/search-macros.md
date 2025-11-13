# DuckDB Search Macros

These notes live alongside the DuckDB implementation so macro changes stay co-located with the SQL. Everything referenced here sits under `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql`.

## `search(...)`

Primary macro that blends lexical heuristics, fuzzy subsequence matching, and semantic embeddings.

1. **`base_params`** – trims/lowercases every input (query text, mode, k, glob filters, weights). All downstream CTEs reference these normalized scalars instead of macro parameters; this prevents DuckDB from flagging correlated LIMIT/OFFSET expressions.
2. **`classified`** – infers the route (`symbol`, `heavy`, `error`, etc.) and builds lowercase keywords. Simple heuristics (presence of `::`, stack traces, length) keep this cheap.
3. **`config`** – maps the route to weights (`bm25_w`, `fuzzy_w`, `effective_sem_weight`) and candidate limits (`lex_limit`, `dense_limit`). Heavy queries double dense candidates; symbol queries clamp both.
4. **`filtered_source` / `filtered`** – reads `repo_index`, materializes a lowercased `uri_local`, and filters by the normalized `uri_glob_filter` / `mime_glob_filter`. When a URI glob is provided we constrain scope to `document` to avoid object spam.
5. **Lexical scorer** (`score_source`, `ranked_lex`, `normalized_lex`, `lex_rrf`) – computes heuristic BM25-style boosts, fuzzy subsequence scores, normalizes them into `[0,1]`, and produces an RRF component.
6. **Semantic scorer** (`semantic_seed`, `qv`, `sem_scored`, `sem_top`, `sem_norm`, `sem_rrf`) – embeds the query once, scores candidates via cosine similarity, rescales to `[0,1]`, and yields its own RRF value.
7. **`union_nodes` / `fallback_nodes` / `final_nodes`** – unions lexical+dense hits, falls back to most-recent documents when both sets are empty, and enforces the caller’s `k` using row numbers obtained from deterministic `base_params` values.
8. **Projection** – joins `filtered` rows with `classified`/`config` so diagnostics travel with the results: `bm25_score`, `fuzzy_score`, `dense_score`, combined score, confidence bucket, and JSON metadata (`boosts_json`, `explain_json`).

## `related(...)`

Helper macro for “more like this” queries.

- `base_params` normalizes the seed URI, mode, limit, and glob filters.
- `seed` loads the target row once; `related_source` excludes it and materializes `uri_local`.
- `filtered` reuses the exact glob checks from `search`, guaranteeing the same semantics.
- `scored` combines cosine similarity (when embeddings exist) with `match_score` as a lexical fallback.
- `final` ranks by the blended score and records `rel_row`; `limited` enforces `k` by slicing on that row number rather than issuing `LIMIT k` (again avoiding correlated expressions).
- Final projection joins `base_params` so the explain JSON reports the normalized mode + seed.

## `file_search(...)`

Thin wrapper that concatenates keyword + question text, switches to `heavy` mode when a question is present, and delegates directly to `search(...)`. Because `search` owns input normalization, this macro simply passes `k` / `max_cand` through.

## Debugging Tips

- Run the macros as subqueries, e.g. `SELECT * FROM search('sample', uri_glob := '*/docs/*') LIMIT 5;` DuckDB will inline the CTEs; comment out the final projection to inspect intermediate stages.
- Inspect `boosts_json` / `explain_json` to see which route triggered and how many lexical/dense candidates were considered.
- When investigating glob behavior, query `filtered_source` to compare `uri` vs. `uri_local` and confirm the normalized filters in `base_params` are what you expect.
