-- Lexical search module: two-phase document-first scoring.
-- Phase 1: Score documents only (11K rows vs 286K) using document-level signals.
-- Phase 2: Expand top documents to child objects with symbol scoring.
-- Output contract unchanged: (node_id, doc_id, bm25_score, fuzzy_score, bm25_norm, fuzz_norm, lex_rank, rrf_lex)

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

-- =====================================================================
-- PHASE 1: DOCUMENT-LEVEL SCORING (~11K rows instead of ~286K)
-- =====================================================================

-- Get document nodes only
_lex_doc_scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := uri_glob,
        uri_like := uri_like,
        scope := 'document'
    )
),

-- Enrich documents with scoring columns
doc_filtered AS (
    SELECT
        sf.node_id,
        sf.doc_id,
        LOWER(REPLACE(repository_uri_container(
            COALESCE(n.uri, 'repoql://unknown')
        ), '\\', '/')) AS search_key,
        repository_uri_file_name(n.uri) AS basename,
        COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')) AS headline,
        COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')) AS structure
    FROM _lex_doc_scope sf
    JOIN node n ON n.id = sf.node_id
    LEFT JOIN artifact a ON a.id = n.artifact_id
),

-- Body content matches via grep (reads live files, already document-scoped)
grep_hits AS (
    SELECT DISTINCT n.id AS doc_id
    FROM params p, grep_matches(p.keywords_lc, '**', 500) g
    JOIN node n ON n.uri = g.uri AND n.kind = 'document'
    WHERE p.keywords_empty = FALSE
),

-- Document heuristic scoring
doc_heur_scored AS (
    SELECT
        d.node_id,
        d.doc_id,
        d.search_key,
        d.basename,
        d.headline,
        d.structure,
        concat_ws(' ',
            COALESCE(d.search_key, ''),
            COALESCE(d.basename, ''),
            COALESCE(d.headline, ''),
            COALESCE(d.structure, '')
        ) AS text_target,
        CASE
            WHEN LOWER(COALESCE(d.basename, '')) = p.keywords_lc
              OR LOWER(regexp_replace(COALESCE(d.basename, ''), '\.[^.]*$', '')) = p.keywords_lc THEN 3.0
            WHEN d.doc_id IN (SELECT doc_id FROM grep_hits) THEN 2.5
            WHEN position(p.keywords_lc IN LOWER(COALESCE(d.basename, ''))) > 0 THEN 2.0
            WHEN position(p.keywords_lc IN LOWER(COALESCE(d.headline, '') || ' ' || COALESCE(d.structure, ''))) > 0 THEN 1.5
            WHEN position(p.keywords_lc IN d.search_key) > 0 THEN 1.0
            ELSE NULL
        END AS bm25_heur,
        TRY_CAST(match_score(p.keywords_lc, d.search_key) AS DOUBLE) AS fuzz
    FROM doc_filtered d
    CROSS JOIN params p
    WHERE p.keywords_empty = FALSE
),

-- Fuzzy fallback only for documents without a heuristic match
doc_scored AS (
    SELECT
        h.*,
        IF(h.bm25_heur IS NULL,
            TRY_CAST(match_score((SELECT keywords_lc FROM params), h.text_target) AS DOUBLE),
            NULL) AS bm25_fallback
    FROM doc_heur_scored h
),

-- Top documents by score
doc_ranked AS (
    SELECT
        node_id,
        doc_id,
        COALESCE(bm25_heur, bm25_fallback, 0.05) AS doc_bm25,
        fuzz AS doc_fuzz,
        ROW_NUMBER() OVER (
            ORDER BY COALESCE(bm25_heur, bm25_fallback, 0) DESC, fuzz DESC, node_id
        ) AS doc_rank
    FROM doc_scored
    QUALIFY doc_rank <= (SELECT limit_cand FROM params)
),

-- =====================================================================
-- PHASE 2: OBJECT EXPANSION WITH SYMBOL SCORING
-- =====================================================================

-- Get child objects of top-ranked documents only
obj_scored AS (
    SELECT
        child.id AS node_id,
        dr.doc_id,
        -- Symbol match: object-level signal
        CASE
            WHEN LOWER(COALESCE(
                repository_uri_symbol(child.uri),
                json_extract_string(child.properties, '$.symbol'),
                json_extract_string(child.properties, '$.name'),
                '')) = p.keywords_lc THEN 4.0
            WHEN p.keywords_lc <> '' AND position(p.keywords_lc IN LOWER(COALESCE(
                repository_uri_symbol(child.uri),
                json_extract_string(child.properties, '$.symbol'),
                json_extract_string(child.properties, '$.name'),
                ''))) > 0 THEN 3.2
            -- Object headline/structure match
            WHEN position(p.keywords_lc IN LOWER(
                COALESCE(child.headline, '') || ' ' || COALESCE(child.structure, ''))) > 0
                THEN GREATEST(1.5, dr.doc_bm25)
            -- Inherit document score
            ELSE dr.doc_bm25
        END AS bm25,
        -- Inherit document fuzz (search_key is identical for all children)
        dr.doc_fuzz AS fuzz
    FROM doc_ranked dr
    JOIN span s ON s.document_id = dr.doc_id
    JOIN node child ON child.span_id = s.id AND child.kind <> 'document'
    CROSS JOIN params p
    WHERE p.keywords_empty = FALSE
),

-- =====================================================================
-- PHASE 3: UNION, RANK, NORMALIZE (identical output contract)
-- =====================================================================

all_candidates AS (
    SELECT node_id, doc_id, doc_bm25 AS bm25, doc_fuzz AS fuzz
    FROM doc_ranked
    UNION ALL
    SELECT node_id, doc_id, bm25, fuzz
    FROM obj_scored
),

limited AS (
    SELECT
        node_id,
        doc_id,
        bm25,
        fuzz,
        ROW_NUMBER() OVER (
            ORDER BY bm25 DESC, fuzz DESC, node_id
        ) AS lex_rank
    FROM all_candidates
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
