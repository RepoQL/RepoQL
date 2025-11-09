# Hybrid Semantic Search (RepoQL Implementation Plan)

> AST-aware querying is intentionally **out of scope** for this document. See `hybrid-semantic-search-ast.md` for the follow-on design.

---

## Goals

1. Deliver object-level search results by default, falling back to file-level hits when structured data is unavailable.
2. Blend lexical (BM25) and dense (embedding) retrieval, plus structural signals such as cross references.
3. Provide a single macro entry point for search and another for "related" queries, both with glob-based scoping and explainability that LLM agents can consume.
4. Keep the plan implementable on top of existing RepoQL indexing infrastructure with incremental schema changes.

---

## Indexing & Storage Requirements

### 1. Dual-layer embeddings
- **Document scope** (existing): keep storing a vector per document so every artifact participates in semantic search.
- **Object scope** (new): when analyzers emit structured nodes (functions/classes/sections/etc.), compute embeddings for the node's "body" (code + docstring + summary) and store them alongside document vectors.
- Store vectors in `document_embedding(doc_id UUID, node_id UUID NULL, uri TEXT, scope TEXT CHECK(scope in ('document','object')), model TEXT, dim INT, embedding JSON, updated_at TIMESTAMP)`.

### 2. Per-object summaries
- Extend analyzers to produce `headline` (one-line summary) and `structure` (outline snippet) for each node.
- Persist these fields on nodes so downstream SQL views can surface them.

### 3. Repo index projection
Create a DuckDB view (or materialized table) `repo_index` with one row per *addressable object* **and** one row per document fallback:
```
repo_index(
  uri TEXT,
  path TEXT,
  lang TEXT,
  mime TEXT,
  kind TEXT,
  symbol TEXT,
  line_start INT,
  line_end INT,
  headline TEXT,
  structure TEXT,
  body TEXT,
  scope TEXT,             -- 'object' or 'document'
  embedding JSON,
  mtime TIMESTAMP,
  digest TEXT
)
```
- `body` combines node text + docstring + file synopsis.
- Document rows use file-level URIs; object rows use precise spans/anchors.

### 4. Catalog/timestamps
- Ensure `DocumentCatalogEntry` captures `LastModifiedUtc` per artifact; object rows can reuse their parent artifact timestamp for recency boosts.

---

## Public Macros

### 1. `search`
```
search(
  q TEXT,
  k INT,
  mode TEXT DEFAULT 'auto',          -- 'auto' | 'symbol' | 'error' | 'heavy'
  uri_glob TEXT DEFAULT NULL,        -- Git-style globs ('**', '!' for negation)
  mime_glob TEXT DEFAULT NULL
)
→ (
  uri, path, lang, mime, kind, symbol,
  scope, line_start, line_end,
  score, bm25_score, dense_score, rrf, confidence,
  boosts_json, explain_json,
  headline, structure, snippet, digest
)
```
- Returns the most specific URI it knows; file-level when object rows are absent.

### 2. `related`
```
related(
  seed_uri TEXT,
  k INT,
  mode TEXT DEFAULT 'mixed',         -- 'mixed' | 'similar' | 'neighbors'
  uri_glob TEXT DEFAULT NULL,
  mime_glob TEXT DEFAULT NULL
)
→ (
  uri, path, lang, mime, kind, symbol,
  scope, line_start, line_end,
  score, sim_score, bm25_score, xref_score, rrf, confidence,
  boosts_json, explain_json,
  headline, structure, snippet, digest
)
```

### Glob semantics
- Input format: `"pat1 pat2, !pat3"` (`**` crosses `/`, prefix `!` excludes, escape literal `!` as `\!`).
- A row is included when it matches **any** positive pattern (or there are none) and **no** negative patterns.

---

## Retrieval & Ranking (Search Macro)

1. **Scope filtering**
   - Apply `gitglob_match(uri, uri_glob)` and `gitglob_match(mime, mime_glob)` before candidate generation.
2. **Router (`mode='auto'`)**
   - Symbol heuristics (`::`, `.`, `()`, CamelCase, snake_case) → `symbol`.
   - Stack-trace looking queries (digits/punct, `at ...`) → `error`.
   - Long/natural language queries default to `auto`.
   - `mode='heavy'` simply inflates candidate pool sizes.
3. **Candidate generation**
   - **Lexical channel**: FTS/BM25 over `symbol` (weight 3) and `body` (weight 1); take `bm_top = 5×k` (10×k in `heavy`).
   - **Dense channel**: cosine similarity over `document_embedding`; same pool sizes as lexical.
4. **Fusion**
   - Reciprocal Rank Fusion (RRF) with `k0=60` across the active channels → aggregate `score` and expose individual `bm25_score`/`dense_score`.
5. **Boosts (additive, monotone)**
   - +2.0 exact symbol match; +0.8 prefix match.
   - +0.25·exp(-λ·age_days) recency (λ=0 for `error`, 0.01 otherwise).
   - +0.10 kind prior (function > class > file).
6. **Diversification**
   - Cap to `per_dir=3` top-level directories (5 in `heavy`) using best `rrf` per folder.
7. **Confidence**
   - `calibrate_confidence(top_score, second_score, local_density)` → `[0,1]` with guidance: ≥0.7 answer directly, 0.4–0.7 read context, <0.4 consider retry/`heavy`.
8. **Explainability**
   - `explain_json` includes `{route, channels, bm_top, dense_top, per_dir_cap, boosts, filters, suggestions}` for agent introspection.

---

## Retrieval & Ranking (Related Macro)

- Use the stored embedding/body for `seed_uri`:
  - `sim_score`: cosine between seed vector and candidates.
  - `bm25_score`: Run BM25 with `seed.body` as the query.
  - `xref_score` (optional): leverage RepoQL’s edge table for cross-reference neighbors.
- Fuse via RRF; apply modest boosts (`+0.6` same language, `+0.4` same repo root, small kind prior).
- Share the same confidence/explain machinery as `search`.

---

## Required UDFs / Helpers

- `gitglob_match(text, patterns) -> BOOLEAN`
- `snippet_by_uri(uri, before_lines, after_lines) -> TEXT`
- `calibrate_confidence(top DOUBLE, second DOUBLE, density DOUBLE) -> DOUBLE`
- `cosine_sim(vec JSON, query JSON) -> DOUBLE` (or perform in SQL if DuckDB vector extensions suffice)
- Existing RepoQL helpers (catalog lookups, snippet extraction) can be reused.

*(AST-related UDFs such as `astgrep_scan` live in the separate AST design.)*

---

## Defaults & Performance Notes

- Embedding dimension: 256–384 with HNSW parameters `M=12–16`, `ef_search=64` (128 in `heavy`).
- Candidate pool sizing: `bm_top = dense_top = 5×k` (10×k in `heavy`).
- Apply globs as early as possible to keep candidate pools tight.
- Deterministic tie-breaking `(score DESC, path ASC, uri ASC)`.

---

## Failure Modes

- Empty result set → return 0 rows with `explain_json.reason="no_candidates"`.
- Dense or lexical channel errors → skip the failing channel, record `explain_json.channels.<name>="error"` and continue.
- When only document-level data exists, `scope='document'` results still satisfy the contract.

---

## Rollout Steps

1. Update analyzers and the committer to emit `headline`, `structure`, and per-node bodies.
2. Extend the vector refresh pipeline to write both scopes to `document_embedding`.
3. Build the `repo_index` view/table.
4. Implement the `search` macro with lexical+dense fusion, glob filters, boosts, diversification, explainability.
5. Implement the `related` macro atop the same primitives.
6. Validate through CLI/API, then expose the macros to LLM agents.

---

## Future Work

- Add the AST-specific channel once we have a storage format for AST matches (see `hybrid-semantic-search-ast.md`).
- Consider offloading heavy retrieval to a dedicated search service if DuckDB proves insufficient.
- Plug explainability/confidence into the RepoQL CLI and API responses for human users.
