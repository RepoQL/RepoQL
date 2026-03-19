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
            ELSE NULL
        END AS bm25_heur,
        -- Fallback fuzzy match on full text
        TRY_CAST(match_score(p.keywords_lc, text_target) AS DOUBLE) AS bm25_fallback,
        -- Fuzzy subsequence score on search_key
        TRY_CAST(match_score(p.keywords_lc, ri.search_key) AS DOUBLE) AS fuzz
    FROM filtered ri
    CROSS JOIN params p
    WHERE p.keywords_empty = FALSE
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
