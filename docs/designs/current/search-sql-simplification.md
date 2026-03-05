# Search SQL Simplification — Design

## North Star

One search call. One pass through the data. Every query correct. The SQL reads like a description of what it does.

## Context

The search SQL pipeline (`repo_index`, `_search_lexical`, `_search_semantic`, `_search_candidates`, `search()`, `related()`, `hybrid_object_candidates`, `similar()`) has accumulated structural debt. A single `explore` call materializes `repo_index` 3–5 times, each materialization scanning the full `node` + `artifact` + `span` + `document_embedding` tables with 8+ C# UDF calls per row. Scope filtering is copy-pasted in 4 locations. There are correctness bugs (`DISTINCT ON` without `ORDER BY`), case sensitivity inconsistencies, and a granularity mismatch between lexical and semantic scoring.

The user's assessment: *"I don't think it makes sense to use repo_index any more. I suspect it is more complex, slow and buggy than it needs to be."*

---

## Current State

### `repo_index` — The View

**Definition:** `src/RepoQL.Data.DuckDB/Schema/Views/repo_index.sql`

A VIEW (not materialized) that denormalizes the frozen tables into a flat searchable surface. Every reference recomputes everything.

```
document_rows:  node(kind='document') LEFT JOIN artifact, document_embedding
                → 8+ UDF calls per row (repository_uri_container, repository_uri_file_name,
                  media_type_kind, media_type_base, repository_uri_symbol, etc.)
                → DISTINCT ON (uri) without ORDER BY

object_rows:    node(kind≠'document') JOIN span JOIN node(doc) LEFT JOIN artifact, document_embedding
                → same UDF calls, plus URI reconstruction from span data
                → DISTINCT ON (uri) without ORDER BY

result:         UNION ALL of both, 22 columns
```

**Columns produced:** `doc_id`, `node_id`, `uri`, `path`, `search_key`, `basename`, `dirname`, `lang`, `mime`, `kind`, `symbol`, `symbol_key`, `line_start`, `line_end`, `headline`, `structure`, `body` (always NULL), `scope`, `embedding`, `mtime`, `digest`

**Why it exists:** Provides a uniform row shape for both documents and their child objects, with pre-computed search fields (`search_key`, `symbol_key`, `basename`, `dirname`). The `scope` column (`'document'` / `'object'`) is the main discriminator.

### Where `repo_index` Is Referenced

#### SQL Macros (5 references)

| Consumer | File | What it reads | Why |
|----------|------|---------------|-----|
| `_search_lexical` | `search_lexical.sql:33` | Full `ri.*` → uses `node_id`, `doc_id`, `search_key`, `basename`, `headline`, `structure`, `symbol`, `symbol_key` | Scope filter + BM25 scoring on metadata fields |
| `_search_semantic` | `search_semantic.sql:33` | Full `ri.*` → uses only `node_id` (as join key to embeddings) | Scope filter, then join filtered node_ids to embedding tables |
| `_search_candidates` | `search.sql:105` | Full `ri.*` → uses `node_id`, `doc_id`, `uri`, `path`, `scope`, `kind`, `symbol`, `lang`, `mime`, `headline`, `structure`, `line_start`, `line_end`, `digest`, `mtime` | Scope filter + enrichment of scored results + recency fallback |
| `related()` | `search.sql:301,314` | Full `ri.*` → uses all columns including `embedding`, `search_key`, `symbol_key` | Seed lookup + candidate source + cosine similarity |
| `similar()` | `similar.sql:182` | `uri`, `headline`, `node_id`, `scope` | Headline lookup with document-scope preference |

#### C# Code (4 references)

| Consumer | File | Columns read | Rewrite difficulty |
|----------|------|-------------|-------------------|
| `DocumentSearchService` (queries A/B) | `Search/DocumentSearchService.cs` | `headline`, `structure`, `lang`, `mime`, `doc_id` (joined to `search()` results) | Moderate — join `node` → `artifact` |
| `DocumentSearchService` (queries C/D) | `Search/DocumentSearchService.cs` | + `uri`, `mtime`, `scope` filter, ordering logic | Harder — `mtime`, `scope` filter need re-deriving |
| `SimilarHandler` | `Host/SimilarHandler.cs` | `headline` only (join on `node_id`) | Trivial — `node` → `artifact` |
| `ObjectSearchService` | `Search/ObjectSearchService.cs` | `uri`, `kind`, `symbol`, `headline`, `structure`, `line_start`, `line_end`, `lang`, `mime`, `scope`, `doc_id` | Hard — widest projection, needs `node` → `span` → `artifact` |

#### Tests (2 references)

| File | Usage |
|------|-------|
| `RepoQL.Testing/Indexing/GraphAssertions.cs:69` | `SELECT count(*) FROM repo_index WHERE uri = '...'` — existence check |
| `RepoQL.Data.DuckDB.Tests/FindCandidatesMacroTests.cs:132` | `SELECT uri FROM repo_index WHERE matches_glob(...)` — glob matching test |

### Scope Filtering — The 4 Copies

The same CTE pattern is duplicated in 4 locations with cosmetic differences:

```sql
-- Pattern (repeated in _search_lexical, _search_semantic, _search_candidates, related)
filtered_source AS (
    SELECT ri.*,
        split_part(ri.uri, '#', 1) AS uri_container,
        regexp_replace(LOWER(ri.uri), '^[^:]+://+', '') AS uri_local
    FROM repo_index ri
),
scope_uris AS (
    SELECT DISTINCT gf.uri AS scoped_uri, split_part(gf.uri, '#', 1) AS scoped_container_uri
    FROM glob_files(pattern_spec := <uri_glob>) gf
    WHERE <uri_glob> IS NOT NULL
),
filtered AS (
    SELECT fs.*
    FROM filtered_source fs
    WHERE (<uri_glob> IS NULL
        OR EXISTS (SELECT 1 FROM scope_uris su WHERE su.scoped_uri = fs.uri
                                                  OR su.scoped_container_uri = fs.uri_container)
        OR matches_glob(fs.uri, <uri_glob>, TRUE, 'file:///') IS TRUE
        OR matches_glob(fs.uri_local, <uri_glob>, TRUE, NULL) IS TRUE)
      AND (<uri_like> IS NULL OR fs.uri LIKE <uri_like>)
      AND (<mime_glob> IS NULL
           OR repoql_glob_match(COALESCE(fs.mime, ''), <mime_glob>, 'true', NULL) IS TRUE)
)
```

**Differences between the 4 copies:**

| Dimension | `_search_lexical` | `_search_semantic` | `_search_candidates` | `related()` |
|-----------|-------------------|--------------------|---------------------|-------------|
| Param names | `p.uri_filter` | `p.uri_filter` | `bp.uri_glob_filter` | `bp_filter.uri_glob_filter` |
| `uri_like` | present | present | present | **absent** |
| Seed exclusion | none | none | none | `WHERE ri.uri <> seed` |
| CTE names | `filtered_source`, `scope_uris` | identical | identical | `related_source`, `related_scope_uris` |

The logic is identical. The only real behavioral difference is `related()` excludes the seed URI.

**Computed-but-unused columns:** `uri_container` and `uri_local` are computed in `filtered_source` for every row, but are only used inside the `filtered` WHERE clause. No downstream CTE references them. They could be internal to the scope filter.

### The `search()` Macro — A Parallel Universe

`search()` in `hybrid_search.sql` is the public API used by explore's `search()` SQL macro. It does not directly read `repo_index`. Instead:

1. Builds its own `docs_outline` CTE from raw `node` + `artifact` (bypassing `repo_index`)
2. Uses `LIKE` for scope filtering (not `glob_files()`)
3. Calls `_search_candidates()` but only reads `doc_id`, `doc_semn`, `bm25_score`
4. Has its own tiered scoring (semantic/bm25/search/outline/body) separate from `_search_candidates`' RRF

This means `search()` pays the full cost of `_search_candidates` (which materializes `repo_index` 3×) but discards most columns. It then re-joins to `docs_outline` (another base-table scan) and `artifact` (for body content).

### Semantic vs Lexical Granularity Mismatch

- **Lexical** scores at `node_id` level — precise symbol-level matching
- **Semantic** scores at `doc_id` level — whole-document matching
- `_search_candidates` joins semantic via `LEFT JOIN sem s ON s.doc_id = ri.doc_id` — all nodes in a semantically-matched document get the same semantic score
- Then `doc_sem` CTE propagates `MAX(dense_score)` across all nodes per `doc_id` anyway

Semantic scoring is effectively always document-granular regardless of the join.

### Correctness Bugs

| Bug | Location | Impact |
|-----|----------|--------|
| `DISTINCT ON (uri)` without `ORDER BY` | `repo_index.sql:87,111` | Non-deterministic: which row survives when URIs collide is random. Could return stale `headline` or wrong `embedding` |
| `body` always NULL | `repo_index.sql:22,75` | Dead column. Wastes space in the SELECT, confuses readers. Lexical search works around it by re-joining `artifact.text_content` |
| Case sensitivity in `search()` | `hybrid_search.sql:70` | `n.uri LIKE c.scope_like` is case-sensitive. All other scope filtering uses case-insensitive `matches_glob`. A scope of `file:///src/Auth/%` would miss `file:///src/auth/%` |
| `LIKE` vs glob inconsistency | `search()` vs `_search_candidates` | `search()` passes scope as `uri_like`. `_search_candidates` uses both `uri_glob` and `uri_like`. Semantics differ (LIKE uses `%`, glob uses `*`) |

### `matches_glob` vs `repoql_glob_match`

Two different UDFs with different capabilities:

| | `repoql_glob_match` | `matches_glob` (→ `repoql_matches_glob`) |
|---|---|---|
| Underlying | `RepoUriGlobMatcher.IsMatch()` | `UriPatternMatcher.Matches()` |
| Compound patterns (`;`) | No | Yes |
| Exclusion (`!`) | No | Yes |
| Fragment patterns | No | Yes |
| NULL pattern | Returns NULL | Returns `"true"` (matches all) |

In scope filtering: `matches_glob` is used for URI filtering, `repoql_glob_match` for MIME filtering. The distinction is functional — MIME types don't need compound patterns.

### Schema Loading Order

Controlled by `DuckDbDataStore.cs` (array around line ~1462):

```
... (tables, early macros) ...
Macros/glob_match.sql
Macros/matches_glob.sql
Macros/glob_files.sql
Tables/document_embedding.sql
Tables/vss_indexes.sql
Views/repo_index.sql              ← position 18
Views/files.sql                   ← independent of repo_index
Views/types.sql                   ← independent
Views/functions.sql               ← independent
Macros/snippet.sql
Macros/node_primary_fragment.sql
Macros/search_helpers.sql         ← position 24
Macros/search_lexical.sql         ← position 25, reads repo_index
Macros/find_candidates.sql
Macros/search_semantic.sql        ← reads repo_index
Macros/search_debug.sql
Macros/search.sql                 ← reads repo_index
Macros/hybrid_search.sql          ← reads repo_index
...
Macros/similar.sql                ← reads repo_index
```

The user-facing views (`Files`, `Types`, `Functions`) do NOT depend on `repo_index` — they join base tables directly.

---

## Constraints

- **Schema frozen** — 5 tables (`artifact`, `node`, `edge`, `span`, `annotation`) never change
- **Single writer** — all DuckDB writes through `DuckDbDataStore`
- **Budget is contract** — search performance directly affects token economics
- **Errors never cascade** — a scope filter failure must not prevent search from returning results
- **Transport parity** — changes must work for both MCP and CLI paths

---

## Design

### Principle: Don't Abstract, Inline

The original `repo_index` was designed as a reusable abstraction — a uniform surface for all search consumers. In practice, each consumer needs a different subset of columns, and the abstraction costs 3–5 full materializations per search. The abstraction isn't earning its keep.

Rather than replacing `repo_index` with another abstraction (`_scope_filter` returning full columns), we should:
1. Inline the base-table joins into each consumer, selecting only the columns needed
2. Extract scope filtering into a reusable macro (this IS reuse that earns its keep — identical logic in 4 places)
3. Keep `repo_index` as a compatibility view (agents query it directly) but stop using it internally

### 1. Scope Filter Macro

**New file:** `src/RepoQL.Data.DuckDB/Schema/Macros/scope_filter.sql`

A table macro that returns filtered `node_id`s (and optionally `doc_id`s) matching scope criteria. It does NOT project the full `repo_index` column set — callers join the returned IDs to whatever base tables they need.

```sql
CREATE OR REPLACE MACRO _scope_filter(
    uri_glob := NULL,
    mime_glob := NULL,
    uri_like := NULL,
    exclude_uri := NULL,    -- for related() seed exclusion
    scope := NULL           -- 'document', 'object', or NULL for both
) AS TABLE (
WITH
params AS (
    SELECT
        NULLIF(TRIM(CAST(uri_glob AS VARCHAR)), '') AS uri_filter,
        NULLIF(TRIM(CAST(uri_like AS VARCHAR)), '') AS like_filter,
        NULLIF(TRIM(CAST(mime_glob AS VARCHAR)), '') AS mime_filter,
        NULLIF(TRIM(CAST(exclude_uri AS VARCHAR)), '') AS exclude_filter,
        NULLIF(TRIM(CAST(scope AS VARCHAR)), '') AS scope_filter
),

-- Base: all nodes with document-level info
all_nodes AS (
    SELECT
        n.id AS node_id,
        CASE WHEN n.kind = 'document' THEN n.id
             ELSE s.document_id
        END AS doc_id,
        COALESCE(n.uri, doc.uri) AS uri,
        n.kind,
        CASE WHEN n.kind = 'document' THEN 'document' ELSE 'object' END AS node_scope,
        media_type_base(a.media_type) AS mime
    FROM node n
    LEFT JOIN span s ON s.id = n.span_id
    LEFT JOIN node doc ON doc.id = s.document_id
    LEFT JOIN artifact a ON a.id = COALESCE(
        CASE WHEN n.kind = 'document' THEN n.artifact_id END,
        doc.artifact_id
    )
),

-- Scope URIs from glob
scope_uris AS (
    SELECT DISTINCT
        gf.uri AS scoped_uri,
        split_part(gf.uri, '#', 1) AS scoped_container_uri
    FROM params p
    CROSS JOIN glob_files(pattern_spec := p.uri_filter) gf
    WHERE p.uri_filter IS NOT NULL
),

-- Apply all filters
filtered AS (
    SELECT an.node_id, an.doc_id, an.uri, an.kind, an.node_scope
    FROM all_nodes an
    JOIN params p ON TRUE
    WHERE
        -- Scope filter (document/object/both)
        (p.scope_filter IS NULL OR an.node_scope = p.scope_filter)
        -- URI glob filter
        AND (
            p.uri_filter IS NULL
            OR EXISTS (
                SELECT 1 FROM scope_uris su
                WHERE su.scoped_uri = an.uri
                   OR su.scoped_container_uri = split_part(an.uri, '#', 1)
            )
            OR matches_glob(an.uri, p.uri_filter, TRUE, 'file:///') IS TRUE
            OR matches_glob(
                regexp_replace(LOWER(an.uri), '^[^:]+://+', ''),
                p.uri_filter, TRUE, NULL
            ) IS TRUE
        )
        -- URI LIKE filter
        AND (p.like_filter IS NULL OR an.uri LIKE p.like_filter)
        -- MIME filter
        AND (p.mime_filter IS NULL
             OR repoql_glob_match(COALESCE(an.mime, ''), p.mime_filter, 'true', NULL) IS TRUE)
        -- Seed exclusion (for related())
        AND (p.exclude_filter IS NULL OR an.uri <> p.exclude_filter)
)

SELECT node_id, doc_id, uri, kind, node_scope FROM filtered
);
```

**What this achieves:**
- Single source of truth for scope filtering
- Returns slim rows (`node_id`, `doc_id`, `uri`, `kind`, `scope`) — callers join to get whatever columns they need
- Reads base tables once per call (not through `repo_index`)
- No UDF calls for columns that won't be used downstream
- `exclude_uri` handles `related()`'s seed exclusion
- `scope` parameter handles `ObjectSearchService`'s `scope = 'object'` filter

### 2. Rewrite `_search_lexical`

Replace the `filtered_source → scope_uris → filtered` block (lines 24–66) with:

```sql
-- Scope-filtered nodes
scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_filter FROM params),
        mime_glob := (SELECT mime_filter FROM params),
        uri_like := (SELECT uri_like_filter FROM params)
    )
),

-- Join to get scoring columns
filtered AS (
    SELECT
        sf.node_id,
        sf.doc_id,
        sf.uri,
        LOWER(REPLACE(repository_uri_container(sf.uri), '\\', '/')) AS search_key,
        repository_uri_file_name(sf.uri) AS basename,
        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) AS headline,
        COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')) AS structure,
        COALESCE(
            repository_uri_symbol(n.uri),
            json_extract_string(n.properties, '$.symbol'),
            json_extract_string(n.properties, '$.name')
        ) AS symbol,
        LOWER(COALESCE(
            repository_uri_symbol(n.uri),
            json_extract_string(n.properties, '$.symbol'),
            json_extract_string(n.properties, '$.name'), ''
        )) AS symbol_key
    FROM scope sf
    JOIN node n ON n.id = sf.node_id
    LEFT JOIN node doc ON doc.id = sf.doc_id AND doc.kind = 'document'
    LEFT JOIN artifact a ON a.id = COALESCE(
        CASE WHEN n.kind = 'document' THEN n.artifact_id END,
        doc.artifact_id
    )
),
```

Only computes the columns `_search_lexical` actually uses: `node_id`, `doc_id`, `search_key`, `basename`, `headline`, `structure`, `symbol`, `symbol_key`. No `embedding`, no `mtime`, no `digest`, no `path`, no `dirname`, no `body`.

### 3. Rewrite `_search_semantic`

The semantic path only needs `node_id` from the scope filter (as a join key to embedding tables):

```sql
scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_filter FROM params),
        mime_glob := (SELECT mime_filter FROM params),
        uri_like := (SELECT uri_like_filter FROM params)
    )
),
```

Then replace all `JOIN filtered ri ON ri.node_id = ...` with `JOIN scope sf ON sf.node_id = ...`. No additional base-table joins needed — the embedding tables already have `doc_id` and `node_id`.

### 4. Rewrite `_search_candidates` Enrichment

The `filtered_source → scope_uris → filtered` block at lines 97–138 is the 3rd copy, used for:
- **Fallback:** recency-based results when both scorers return nothing (needs `node_id`, `mtime`)
- **Enrichment:** joining scored `node_id`s back to get `uri`, `headline`, etc. (needs most columns)

Replace with `_scope_filter` for the filter, then a targeted join for enrichment:

```sql
-- Re-use scope filter for fallback
scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        mime_glob := (SELECT mime_glob_filter FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

-- Fallback uses node_id + mtime from scope + node table
fallback_nodes AS (
    SELECT sf.node_id
    FROM scope sf
    JOIN node n ON n.id = sf.node_id
    QUALIFY ROW_NUMBER() OVER (ORDER BY n.updated_at DESC, sf.node_id)
        <= (SELECT result_k FROM base_params)
),

-- Enrichment: only for nodes that survived scoring
scored AS (
    SELECT
        sf.doc_id,
        sf.node_id,
        sf.uri,
        REPLACE(repository_uri_container(sf.uri), '\\', '/') AS path,
        sf.node_scope AS scope,
        n.kind,
        COALESCE(repository_uri_symbol(n.uri), ...) AS symbol,
        media_type_kind(a.media_type) AS lang,
        media_type_base(a.media_type) AS mime,
        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) AS headline,
        COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')) AS structure,
        ...
    FROM final_nodes fn
    JOIN scope sf ON sf.node_id = fn.fn_node_id
    JOIN node n ON n.id = fn.fn_node_id
    LEFT JOIN node doc ON doc.id = sf.doc_id AND doc.kind = 'document'
    LEFT JOIN artifact a ON a.id = COALESCE(...)
    LEFT JOIN lex l ON l.node_id = fn.fn_node_id
    LEFT JOIN sem s ON s.doc_id = sf.doc_id
)
```

The key win: UDF calls (`repository_uri_container`, `media_type_kind`, etc.) now only execute for the ~50 scored results, not the entire repo_index.

### 5. Rewrite `related()`

Replace the 4th scope filter copy. Use `exclude_uri` parameter:

```sql
scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        mime_glob := (SELECT mime_glob_filter FROM base_params),
        exclude_uri := (SELECT seed FROM base_params)
    )
),
```

The seed lookup (`seed` CTE at line 299) currently reads `repo_index` for one row. Replace with a direct join:

```sql
seed AS (
    SELECT
        n.id AS node_id,
        n.uri,
        de.embedding
    FROM node n
    LEFT JOIN document_embedding de
        ON de.node_id = n.id AND de.embedding_type = 'full' AND de.chunk_index = 0
    WHERE n.uri = (SELECT seed FROM base_params)
    LIMIT 1
),
```

### 6. Rewrite `hybrid_object_candidates`

Currently reads `repo_index ri` filtered by `JOIN target_docs td ON td.doc_id = ri.doc_id WHERE ri.scope = 'object'`. Replace with direct base-table joins:

```sql
candidates AS (
    SELECT
        doc.id AS doc_id,
        child.id AS node_id,
        COALESCE(child.uri, repository_uri_join(doc.uri, ...)) AS uri,
        td.document_uri,
        child.kind,
        COALESCE(repository_uri_symbol(child.uri), ...) AS symbol,
        COALESCE(NULLIF(child.headline, ''), ...) AS headline,
        child.structure,
        COALESCE(span.start_line, ...) AS line_start,
        span.end_line AS line_end,
        media_type_kind(a.media_type) AS lang,
        media_type_base(a.media_type) AS semantic_type,
        ...
    FROM node child
    JOIN span ON span.id = child.span_id
    JOIN node doc ON doc.id = span.document_id
    JOIN target_docs td ON td.doc_id = doc.id
    LEFT JOIN artifact a ON a.id = doc.artifact_id
    CROSS JOIN cfg
    WHERE child.kind <> 'document'
    ...
)
```

No scope filtering needed — the scope is pre-filtered by `target_docs`.

### 7. Rewrite `similar()`

Currently reads `repo_index` only for `headline` lookup with document-scope preference. Replace with:

```sql
repo_headlines AS (
    SELECT
        n.uri,
        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) AS headline,
        ROW_NUMBER() OVER (
            PARTITION BY n.uri
            ORDER BY CASE WHEN n.kind = 'document' THEN 0 ELSE 1 END, n.id
        ) AS rn
    FROM node n
    LEFT JOIN artifact a ON a.id = n.artifact_id
),
```

### 8. Rewrite C# SQL Consumers

| Consumer | Change |
|----------|--------|
| `SimilarHandler` | Replace `LEFT JOIN repo_index ri ON ri.node_id = bp.node_id` with `LEFT JOIN node n ON n.id = bp.node_id LEFT JOIN artifact a ON a.id = n.artifact_id`. Read `a.headline` instead of `ri.headline`. |
| `DocumentSearchService` (queries A/B) | Replace `LEFT JOIN repo_index ri ON ri.uri = hs.uri AND ri.scope = 'document'` with joins to `node` + `artifact`. Compute `lang`/`mime` from `media_type_kind(a.media_type)` / `media_type_base(a.media_type)`. |
| `DocumentSearchService` (queries C/D) | Replace `FROM repo_index ri WHERE ri.scope = 'document'` with `FROM node n JOIN artifact a ON a.id = n.artifact_id WHERE n.kind = 'document'`. Derive `mtime` from `n.updated_at`. |
| `ObjectSearchService` | Replace `FROM repo_index ri WHERE ri.scope = 'object'` with the `node child → span → node doc → artifact` join chain. This is the widest projection — compute `uri`, `symbol`, `line_start/end` from base tables. |

### 9. `repo_index` — Keep as Compatibility View

Don't delete `repo_index`. Agents use it in ad-hoc SQL queries (`SELECT * FROM repo_index WHERE ...`). Tests reference it. Keep it as a queryable view, but nothing internal depends on it for search. Fix the `DISTINCT ON` bug by adding `ORDER BY node_id`.

### 10. Fix Case Sensitivity

With scope filtering centralized in `_scope_filter`, fix once:
- All URI comparisons use case-insensitive `matches_glob` (already `TRUE` for `ignore_case`)
- Remove the `LIKE`-based scope filtering in `search()`. Pass `uri_glob` instead of `uri_like` to `_search_candidates`
- Or: normalize `_search_candidates` to accept both `uri_glob` and `uri_like`, converting `LIKE` patterns to globs at the boundary

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Inline base-table joins per consumer | Single abstraction (`_scope_filter` returning full columns) | Each consumer needs different columns. A full-projection macro is just `repo_index` with a different name |
| Scope filter returns IDs only | Scope filter returns full rows | Slim return = callers join only what they need. No wasted UDF calls |
| Keep `repo_index` as compatibility view | Delete entirely | Agents use it in ad-hoc queries. Breaking change not justified |
| Fix `DISTINCT ON` with `ORDER BY` | Remove `DISTINCT ON` | View contract may depend on one-row-per-URI. Safer to make it deterministic |
| Centralize scope filtering in SQL macro | Move scope filtering to C# | SQL macro keeps the optimization in DuckDB's query planner. C# would require round-trips |

## Alternatives Considered

**Materialized table refreshed at index time.** Would eliminate repeated computation, but requires refresh coordination with the indexing pipeline — when to rebuild, how to handle partial updates, what happens during re-index. The indexing pipeline already has epoch tracking and operation management; adding a materialized search index is a second cache to invalidate. The inline approach achieves the same performance gain (no redundant computation) without cache invalidation complexity.

**Replace `repo_index` with a single macro that returns full columns.** This is just `repo_index` with a different name. The problem isn't the shape — it's that the full column set is computed for every consumer regardless of what they need.

## Risks

| Risk | Mitigation |
|------|------------|
| Inlining base-table joins makes SQL longer and harder to read | Each consumer's SQL is self-contained and only joins what it needs. More lines but less hidden work |
| `_scope_filter` macro overhead from DuckDB macro expansion | Profile before/after. DuckDB's optimizer should push predicates through the macro boundary |
| Breaking ad-hoc SQL queries that reference `repo_index` columns | Keep `repo_index` view unchanged. Fix `DISTINCT ON` determinism (strictly better) |
| C# SQL string changes are error-prone | Run full test suite. The test helpers in `GraphAssertions` still use `repo_index` and validate presence |

---

## Files to Modify

### New
| File | Purpose |
|------|---------|
| `src/RepoQL.Data.DuckDB/Schema/Macros/scope_filter.sql` | Reusable scope filtering macro |

### SQL Macros
| File | Change |
|------|--------|
| `Schema/Macros/search_lexical.sql` | Replace scope filter block with `_scope_filter` + targeted joins |
| `Schema/Macros/search_semantic.sql` | Replace scope filter block with `_scope_filter` (ID-only) |
| `Schema/Macros/search.sql` | Replace scope filter in `_search_candidates` and `related()` |
| `Schema/Macros/hybrid_search.sql` | Replace `repo_index` in `hybrid_object_candidates` with base-table joins |
| `Schema/Macros/similar.sql` | Replace `repo_index` headline lookup with `node` + `artifact` join |
| `Schema/Views/repo_index.sql` | Add `ORDER BY node_id` inside `DISTINCT ON` blocks |

### C# Code
| File | Change |
|------|--------|
| `DuckDbDataStore.cs` | Add `scope_filter.sql` to loading order (between `search_helpers.sql` and `search_lexical.sql`) |
| `Search/DocumentSearchService.cs` | Replace `repo_index` joins with base-table joins in 4 queries |
| `Host/SimilarHandler.cs` | Replace `repo_index` join with `node` → `artifact` join |
| `Search/ObjectSearchService.cs` | Replace `repo_index` query with `node` → `span` → `artifact` joins |

### Tests
| File | Change |
|------|--------|
| `GraphAssertions.cs` | Keep using `repo_index` (it's still a valid view) |
| `FindCandidatesMacroTests.cs` | Keep using `repo_index` (compatibility) |
| New test | Verify `_scope_filter` returns correct IDs for glob, LIKE, MIME, exclude, and scope params |

---

## Verification

1. `dotnet build RepoQL.sln` — 0 errors
2. `dotnet test RepoQL.sln` — all passing
3. Publish host, reconnect MCP
4. Test searches (compare results before/after):
   - `search('authentication', k := 10)` — basic keyword search
   - `search('parser', scope := 'file:///src/%')` — scoped search
   - `explore(keywords="cache", tokenBudget=2000)` — explore uses `_search_candidates`
   - `explore(breadth=9, tokenBudget=1500)` — no keywords, fallback path
   - `related(seed_uri := '<uri>')` — related documents
   - `SELECT * FROM repo_index LIMIT 5` — compatibility view still works
5. Profile: count of UDF invocations per search call should drop ~3×
