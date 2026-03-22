-- Explore-specific retrieval: delegates scoring to search_pipeline (the single
-- source of truth), then adds explore-specific enrichment:
--   1. Document promotion from best child: max(own, best_child * 0.9)
--   2. Floor-normalized confidence (absolute, not relative)
--
-- Previously this macro duplicated the entire scoring pipeline in SQL with
-- different weights and formulas. Now search_pipeline (C#) is the single
-- scoring authority. This eliminates score divergence between explore and query.

CREATE OR REPLACE MACRO _explore_candidates(
    q,
    uri_glob := NULL,
    k := 100,
    mode := 'auto',
    max_cand := 5000,
    bm25_weight := 0.30,
    fuzzy_weight := 0.15,
    semantic_weight := 0.55,
    uri_like := NULL
) AS TABLE (
WITH
params AS (
    SELECT
        CAST(COALESCE(k, 100) AS BIGINT) AS result_k
),

-- Delegate to search_pipeline for retrieval + scoring.
-- Over-fetch (2x k) so document promotion has enough objects to work with.
pipeline AS (
    SELECT *
    FROM search_pipeline(
        query := q,
        scope := uri_glob,
        k := CAST(COALESCE(k, 100) * 2 AS BIGINT),
        top_doc_limit := 200,
        per_doc_cap := 20
    )
),

-- Document promotion: a strong child symbol can promote its parent document.
-- doc.score = max(own_score, best_child_score * 0.9)
doc_best_child AS (
    SELECT doc_id, MAX(score) AS best_child_score
    FROM pipeline
    WHERE node_scope = 'object'
    GROUP BY doc_id
),

promoted AS (
    SELECT
        p.*,
        CASE
            WHEN p.node_scope = 'document' AND dc.best_child_score IS NOT NULL
            THEN GREATEST(p.score, dc.best_child_score * 0.9)
            ELSE p.score
        END AS promoted_score
    FROM pipeline p
    LEFT JOIN doc_best_child dc ON dc.doc_id = p.doc_id
),

ranked AS (
    SELECT
        p.*,
        -- Floor-normalized confidence: score is already floor-normalized in
        -- search_pipeline macro (floor=0.33 subtracted, rescaled to [0,1]).
        CAST(LEAST(100, GREATEST(1, ROUND(p.promoted_score * 100))) AS INTEGER) AS conf,
        ROW_NUMBER() OVER (ORDER BY p.promoted_score DESC, LENGTH(p.uri)) AS rank_pos
    FROM promoted p
)

SELECT
    doc_id,
    node_id,
    uri,
    path,
    node_scope,
    kind,
    symbol,
    lang,
    mime,
    headline,
    structure,
    snippet,
    line_start,
    line_end,
    digest,
    bm25_score,
    fuzzy_score,
    dense_score AS sem_score,
    promoted_score AS score,
    conf AS confidence,
    rrf,
    NULL::BIGINT AS best_chunk_start,
    NULL::BIGINT AS best_chunk_end,
    dense_score AS chunk_score,
    sem_provenance,
    rank_pos
FROM ranked
WHERE rank_pos <= (SELECT result_k FROM params)
ORDER BY promoted_score DESC, LENGTH(uri)
);
