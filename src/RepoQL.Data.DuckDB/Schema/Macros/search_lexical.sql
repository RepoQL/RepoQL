-- Lexical search module: BM25 heuristics + fuzzy subsequence scoring.
-- This is the "lexical" half of hybrid search, focusing on keyword/pattern matching.

CREATE OR REPLACE MACRO _search_lexical(
    q,
    uri_glob := NULL,
    max_cand := 5000,
    uri_like := NULL
) AS TABLE (
WITH
-- Normalize parameters
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        LOWER(COALESCE(TRIM(q), '')) AS keywords_lc,
        CASE WHEN COALESCE(TRIM(q), '') = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand
),

-- Scope-filtered nodes via centralized scope filter
_lex_scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := uri_glob,
        uri_like := uri_like
    )
),

-- Enrich scope results with columns needed for lexical scoring
filtered AS (
    SELECT
        sf.node_id,
        sf.doc_id,
        -- search_key: lowercase document path
        LOWER(REPLACE(repository_uri_container(
            COALESCE(n.uri, doc.uri, 'repoql://unknown')
        ), '\\', '/')) AS search_key,
        -- basename: document filename
        repository_uri_file_name(COALESCE(doc.uri, n.uri)) AS basename,
        -- headline: node-specific computation
        CASE WHEN n.kind = 'document'
            THEN COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, ''))
            ELSE COALESCE(
                NULLIF(n.headline, ''),
                json_extract_string(n.properties, '$.name'),
                repository_uri_file_name(doc.uri)
            )
        END AS headline,
        -- structure: node-specific
        CASE WHEN n.kind = 'document'
            THEN COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, ''))
            ELSE NULLIF(n.structure, '')
        END AS structure,
        -- symbol: from URI or properties
        COALESCE(
            repository_uri_symbol(n.uri),
            json_extract_string(n.properties, '$.symbol'),
            json_extract_string(n.properties, '$.name')
        ) AS symbol,
        -- symbol_key: lowercase symbol
        LOWER(COALESCE(
            repository_uri_symbol(n.uri),
            json_extract_string(n.properties, '$.symbol'),
            json_extract_string(n.properties, '$.name'),
            ''
        )) AS symbol_key
    FROM _lex_scope sf
    JOIN node n ON n.id = sf.node_id
    LEFT JOIN node doc ON doc.id = sf.doc_id
    LEFT JOIN artifact a ON a.id = COALESCE(
        CASE WHEN n.kind = 'document' THEN n.artifact_id END,
        doc.artifact_id
    )
),

-- Body content matches via grep (reads live files, no artifact materialization)
grep_hits AS (
    SELECT DISTINCT n.id AS doc_id
    FROM params p, grep_matches(p.keywords_lc, '**', 500) g
    JOIN node n ON n.uri = g.uri AND n.kind = 'document'
    WHERE p.keywords_empty = FALSE
),

-- Phase 1: cheap heuristic scoring (position checks + grep)
heur_scored AS (
    SELECT
        ri.node_id,
        ri.doc_id,
        ri.search_key,
        -- Concatenate searchable text for fuzzy matching (metadata only, no body)
        concat_ws(' ',
            COALESCE(ri.search_key, ''),
            COALESCE(ri.basename, ''),
            COALESCE(ri.headline, ''),
            COALESCE(ri.structure, ''),
            COALESCE(ri.symbol, '')
        ) AS text_target,
        -- BM25-style heuristic scoring
        CASE
            -- Exact symbol match
            WHEN COALESCE(ri.symbol_key, '') = p.keywords_lc THEN 4.0
            -- Symbol contains query
            WHEN p.keywords_lc <> '' AND position(p.keywords_lc IN COALESCE(ri.symbol_key, '')) > 0 THEN 3.2
            -- Exact basename match
            WHEN LOWER(COALESCE(ri.basename, '')) = p.keywords_lc
              OR LOWER(regexp_replace(COALESCE(ri.basename, ''), '\.[^.]*$', '')) = p.keywords_lc THEN 3.0
            -- Body contains query (via grep on live files — ranked high, between basename and search key)
            WHEN ri.doc_id IN (SELECT doc_id FROM grep_hits) THEN 2.5
            -- Basename contains query
            WHEN position(p.keywords_lc IN LOWER(COALESCE(ri.basename, ''))) > 0 THEN 2.0
            -- Headline or structure contains query
            WHEN position(p.keywords_lc IN LOWER(COALESCE(ri.headline, '') || ' ' || COALESCE(ri.structure, ''))) > 0 THEN 1.5
            -- Search key contains query
            WHEN position(p.keywords_lc IN ri.search_key) > 0 THEN 1.0
            ELSE NULL
        END AS bm25_heur
    FROM filtered ri
    CROSS JOIN params p
    WHERE p.keywords_empty = FALSE
),

-- Phase 2: fuzzy match_score only where heuristic didn't match (avoids 286K UDF calls)
score_source AS (
    SELECT
        h.node_id,
        h.doc_id,
        FALSE AS keywords_empty,
        h.text_target,
        h.bm25_heur,
        -- Fuzzy fallback only for rows without a heuristic score
        IF(h.bm25_heur IS NULL,
            TRY_CAST(match_score((SELECT keywords_lc FROM params), h.text_target) AS DOUBLE),
            NULL) AS bm25_fallback,
        -- Fuzzy on search_key (cheap, always useful for ranking)
        TRY_CAST(match_score((SELECT keywords_lc FROM params), h.search_key) AS DOUBLE) AS fuzz
    FROM heur_scored h
),

-- Rank by best available BM25 signal and apply limit via QUALIFY
limited AS (
    SELECT
        node_id,
        doc_id,
        -- Use best available score
        COALESCE(
            bm25_heur,
            bm25_fallback,
            CASE WHEN keywords_empty THEN 0 ELSE 0.05 END
        ) AS bm25,
        fuzz,
        ROW_NUMBER() OVER (
            ORDER BY
                COALESCE(bm25_heur, bm25_fallback, 0) DESC,
                fuzz DESC,
                node_id
        ) AS lex_rank
    FROM score_source
    QUALIFY lex_rank <= (SELECT limit_cand FROM params)
),

normalized AS (
    SELECT
        node_id,
        doc_id,
        bm25,
        fuzz,
        lex_rank,
        zero_one(bm25) AS bm25_norm,
        zero_one(fuzz) AS fuzz_norm,
        rrf_score(lex_rank) AS rrf_lex
    FROM limited
)

SELECT
    node_id,
    doc_id,
    bm25 AS bm25_score,
    fuzz AS fuzzy_score,
    bm25_norm,
    fuzz_norm,
    lex_rank,
    rrf_lex
FROM normalized
);
