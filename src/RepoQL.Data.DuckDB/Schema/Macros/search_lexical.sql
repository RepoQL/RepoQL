-- Lexical search module: BM25 heuristics + fuzzy subsequence scoring.
-- This is the "lexical" half of hybrid search, focusing on keyword/pattern matching.

CREATE OR REPLACE MACRO _search_lexical(
    q,
    uri_glob := NULL,
    mime_glob := NULL,
    max_cand := 5000
) AS TABLE (
WITH
-- Normalize parameters
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        LOWER(COALESCE(TRIM(q), '')) AS keywords_lc,
        CASE WHEN COALESCE(TRIM(q), '') = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        NULLIF(TRIM(uri_glob), '') AS uri_filter,
        NULLIF(TRIM(mime_glob), '') AS mime_filter,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand
),

-- Pre-filter repo_index with URI/MIME filters
filtered_source AS (
    SELECT
        ri.*,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(LOWER(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
),
filtered AS (
    SELECT fs.*
    FROM filtered_source fs
    JOIN params p ON TRUE
    WHERE (
            p.uri_filter IS NULL
            OR repoql_glob_match(fs.uri, p.uri_filter, 'true','file:///') IS TRUE
            OR repoql_glob_match(fs.uri_local, p.uri_filter, 'true',NULL) IS TRUE
        )
      AND (
            p.mime_filter IS NULL
            OR repoql_glob_match(COALESCE(fs.mime, ''), p.mime_filter, 'true',NULL) IS TRUE
        )
),

-- Score each document/object against the query
-- Join to artifact for full-text body position check (streaming, no materialization)
score_source AS (
    SELECT
        ri.node_id,
        ri.doc_id,
        p.keywords_empty,
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
            WHEN p.keywords_empty THEN 0.0
            -- Exact symbol match
            WHEN COALESCE(ri.symbol_key, '') = p.keywords_lc THEN 4.0
            -- Symbol contains query
            WHEN p.keywords_lc <> '' AND position(p.keywords_lc IN COALESCE(ri.symbol_key, '')) > 0 THEN 3.2
            -- Exact basename match
            WHEN LOWER(COALESCE(ri.basename, '')) = p.keywords_lc
              OR LOWER(regexp_replace(COALESCE(ri.basename, ''), '\.[^.]*$', '')) = p.keywords_lc THEN 3.0
            -- Basename contains query
            WHEN position(p.keywords_lc IN LOWER(COALESCE(ri.basename, ''))) > 0 THEN 2.0
            -- Search key contains query
            WHEN position(p.keywords_lc IN ri.search_key) > 0 THEN 1.0
            -- Body contains query (full text_content, no truncation)
            -- Score 2.5: higher than fuzzy matches, between basename-contains (2.0) and exact-basename (3.0)
            WHEN position(p.keywords_lc IN LOWER(COALESCE(art.text_content, ''))) > 0 THEN 2.5
            ELSE NULL
        END AS bm25_heur,
        -- Fallback fuzzy match on full text
        TRY_CAST(match_score(p.keywords_lc, text_target) AS DOUBLE) AS bm25_fallback,
        -- Fuzzy subsequence score on search_key
        TRY_CAST(match_score(p.keywords_lc, ri.search_key) AS DOUBLE) AS fuzz
    FROM filtered ri
    CROSS JOIN params p
    -- Join to artifact for body position check (documents have artifact_id via doc node)
    LEFT JOIN node doc ON doc.id = ri.doc_id
    LEFT JOIN artifact art ON art.id = doc.artifact_id
    WHERE p.keywords_empty = FALSE
),

-- Rank by best available BM25 signal
ranked AS (
    SELECT
        node_id,
        doc_id,
        keywords_empty,
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
),

-- Apply limit and normalize scores
limited AS (
    SELECT r.*
    FROM ranked r
    JOIN params p ON TRUE
    WHERE r.lex_rank <= p.limit_cand
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
