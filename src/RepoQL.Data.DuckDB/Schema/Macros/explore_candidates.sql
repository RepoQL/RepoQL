-- Explore-specific retrieval: unified document + symbol ranking with dampened
-- semantic inheritance and chunk evidence passthrough.
--
-- Unlike _search_candidates (which smears document semantic scores to all children
-- at 1.0x), this macro:
--   1. Dampens inherited semantic scores to 0.5x for objects
--   2. Preserves full semantic score for objects whose span overlaps the best chunk
--   3. Lets documents and objects compete in a single ranked pool
--   4. Promotes document score from best child: max(own, best_child * 0.9)
--   5. Preserves chunk evidence (byte ranges, cosine score) for downstream use
--
-- Reranker slot: output includes snippet + headline + structure per result.
-- The C# layer can send top-N to Voyage reranker (paying users) between
-- SQL retrieval and final ranking. Reranker scores blend with or replace
-- the combined score before budget allocation.
--
-- Parameters:
--   q              - Search query (keywords or question)
--   uri_glob       - Full glob pattern for scope (supports ;, !, #)
--   k              - Max results to return
--   mode           - 'auto', 'symbol', 'error', 'heavy'
--   max_cand       - Internal candidate budget per scorer
--   bm25_weight    - Weight for BM25 lexical score (default 0.15)
--   fuzzy_weight   - Weight for fuzzy match score (default 0.15)
--   semantic_weight - Weight for semantic score (default 0.70)
--   uri_like       - SQL LIKE pattern (case-insensitive, legacy compat)

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
-- ============================================================================
-- PARAMETER NORMALIZATION
-- ============================================================================
base_params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        LOWER(COALESCE(mode, 'auto')) AS requested_mode,
        CAST(COALESCE(k, 100) AS BIGINT) AS result_k,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS max_candidates,
        COALESCE(bm25_weight, 0.15) AS bm25_w,
        COALESCE(fuzzy_weight, 0.15) AS fuzzy_w,
        COALESCE(semantic_weight, 0.70) AS base_sem_w,
        NULLIF(TRIM(uri_glob), '') AS uri_glob_filter,
        NULLIF(TRIM(uri_like), '') AS uri_like_filter
),

classified AS (
    SELECT
        *,
        LOWER(raw_query) AS keywords_lc,
        CASE WHEN raw_query = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        _search_classify_query(raw_query) AS route_mode
    FROM base_params
),

config AS (
    SELECT
        *,
        CASE
            WHEN keywords_empty THEN 0.80
            WHEN route_mode = 'symbol' THEN base_sem_w * 0.7
            ELSE base_sem_w
        END AS effective_sem_weight
    FROM classified
),

-- ============================================================================
-- PHASE 1: SCOPED RETRIEVAL
-- ============================================================================

lex AS (
    SELECT * FROM _search_lexical(
        q := (SELECT raw_query FROM base_params),
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        max_cand := (SELECT max_candidates FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

sem AS (
    SELECT * FROM _search_semantic(
        q := (SELECT raw_query FROM base_params),
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        max_cand := (SELECT max_candidates FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

-- ============================================================================
-- CANDIDATE UNION
-- ============================================================================

union_nodes AS (
    SELECT node_id FROM lex
    UNION
    SELECT node_id FROM sem
),

_ec_scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

-- Recency fallback when both scorers return nothing
fallback_nodes AS (
    SELECT sf.node_id
    FROM _ec_scope sf
    JOIN node n ON n.id = sf.node_id
    QUALIFY ROW_NUMBER() OVER (ORDER BY n.updated_at DESC, sf.node_id)
        <= (SELECT result_k FROM base_params)
),
combined_nodes AS (
    SELECT node_id FROM union_nodes
    UNION ALL
    SELECT node_id FROM fallback_nodes
    WHERE NOT EXISTS (SELECT 1 FROM union_nodes)
),
final_nodes AS (
    SELECT DISTINCT node_id AS fn_node_id FROM combined_nodes
),

-- ============================================================================
-- PHASE 2: ENRICH — metadata for scored candidates only
-- ============================================================================

enriched AS (
    SELECT
        sf.doc_id,
        fn.fn_node_id AS node_id,
        sf.node_scope,
        n.kind,
        COALESCE(
            n.uri,
            repository_uri_join(
                COALESCE(doc.uri, 'repoql://document/' || CAST(sf.doc_id AS VARCHAR)),
                COALESCE(
                    fragment_from_line_range(CAST(sp.start_line AS VARCHAR), CAST(sp.end_line AS VARCHAR)),
                    concat('node/', n.kind, '/', REPLACE(CAST(n.id AS VARCHAR), '-', ''))
                )
            )
        ) AS uri,
        REPLACE(repository_uri_container(COALESCE(doc.uri, n.uri, 'repoql://unknown')), '\\', '/') AS path,
        COALESCE(
            repository_uri_symbol(n.uri),
            json_extract_string(n.properties, '$.symbol'),
            json_extract_string(n.properties, '$.name')
        ) AS symbol,
        media_type_kind(a.media_type) AS lang,
        media_type_base(a.media_type) AS mime,
        CASE WHEN n.kind = 'document'
            THEN COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, ''))
            ELSE COALESCE(
                NULLIF(n.headline, ''),
                json_extract_string(n.properties, '$.name'),
                repository_uri_file_name(doc.uri)
            )
        END AS headline,
        CASE WHEN n.kind = 'document'
            THEN COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, ''))
            ELSE NULLIF(n.structure, '')
        END AS structure,
        COALESCE(sp.start_line, TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER)) AS line_start,
        COALESCE(sp.end_line, TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER)) AS line_end,
        sp.start_byte AS span_start_byte,
        sp.end_byte AS span_end_byte,
        a.digest,
        a.text_content
    FROM final_nodes fn
    JOIN _ec_scope sf ON sf.node_id = fn.fn_node_id
    JOIN node n ON n.id = fn.fn_node_id
    LEFT JOIN span sp ON sp.id = n.span_id
    LEFT JOIN node doc ON doc.id = sf.doc_id
    LEFT JOIN artifact a ON a.id = COALESCE(
        CASE WHEN n.kind = 'document' THEN n.artifact_id END,
        doc.artifact_id
    )
),

-- ============================================================================
-- PHASE 3: UNIFIED SCORING — dampened semantic inheritance
-- ============================================================================
--
-- Key difference from _search_candidates:
--   _search_candidates joins sem by doc_id → all children get document's
--   full semantic score (1.0x). Here:
--     - Documents: full semantic score (direct evidence)
--     - Objects overlapping best chunk: full semantic score (own evidence)
--     - Objects NOT overlapping: 0.5x dampened (inherited, not earned)
--
-- Chunk overlap test uses span byte offsets vs chunk byte offsets:
--   span_start_byte < best_chunk_end AND span_end_byte > best_chunk_start

scored AS (
    SELECT
        e.doc_id,
        e.node_id,
        e.uri,
        e.path,
        e.node_scope,
        e.kind,
        e.symbol,
        e.lang,
        e.mime,
        e.headline,
        e.structure,
        -- Snippet from best chunk when chunk score exceeds noise floor.
        -- Below threshold the "best" chunk is just least-bad noise — fall back to document snippet.
        CASE
            WHEN s.best_chunk_start IS NOT NULL
                 AND s.best_chunk_end IS NOT NULL
                 AND COALESCE(s.sem_score, 0) >= 0.30
                 AND e.text_content IS NOT NULL
                 AND LENGTH(e.text_content) > 0
            THEN array_to_string(
                list_slice(
                    string_split(e.text_content, chr(10)),
                    GREATEST(1, TRY_CAST(line_for_byte_offset(e.text_content, CAST(s.best_chunk_start AS VARCHAR)) AS INTEGER) - 2),
                    LEAST(
                        len(string_split(e.text_content, chr(10))),
                        TRY_CAST(line_for_byte_offset(e.text_content, CAST(s.best_chunk_end AS VARCHAR)) AS INTEGER) + 2
                    )
                ),
                chr(10)
            )
            WHEN e.node_scope = 'document'
            THEN substr(COALESCE(e.text_content, ''), 1, 640)
            -- Objects with line range: extract actual source lines (capped at 20)
            WHEN e.line_start IS NOT NULL
                 AND e.line_end IS NOT NULL
                 AND e.text_content IS NOT NULL
                 AND LENGTH(e.text_content) > 0
            THEN array_to_string(
                list_slice(
                    string_split(e.text_content, chr(10)),
                    GREATEST(1, e.line_start),
                    LEAST(e.line_start + 19, e.line_end)
                ),
                chr(10)
            )
            ELSE NULL
        END AS snippet,
        e.line_start,
        e.line_end,
        e.digest,
        -- Lexical scores: per-node (objects get own lexical evidence)
        COALESCE(l.bm25_norm, 0) AS bm25_score,
        COALESCE(l.fuzz_norm, 0) AS fuzzy_score,
        -- Semantic score with dampened inheritance
        CASE
            -- Documents: full semantic score (direct evidence)
            WHEN e.node_scope = 'document' THEN COALESCE(s.sem_norm, 0)
            -- Objects overlapping best chunk: full score (own evidence)
            WHEN s.best_chunk_start IS NOT NULL
                 AND e.span_start_byte IS NOT NULL
                 AND e.span_end_byte IS NOT NULL
                 AND e.span_start_byte < s.best_chunk_end
                 AND e.span_end_byte > s.best_chunk_start
            THEN COALESCE(s.sem_norm, 0)
            -- Objects without overlap: dampened inheritance (0.5x)
            ELSE COALESCE(s.sem_norm, 0) * 0.5
        END AS sem_score,
        -- Chunk evidence passthrough for downstream (C# ChunkProximityBooster, reranker)
        s.best_chunk_start,
        s.best_chunk_end,
        COALESCE(s.sem_score, 0) AS chunk_score,
        -- Provenance: how did this node get its semantic score?
        CASE
            WHEN e.node_scope = 'document' THEN 'direct'
            WHEN s.best_chunk_start IS NOT NULL
                 AND e.span_start_byte IS NOT NULL
                 AND e.span_end_byte IS NOT NULL
                 AND e.span_start_byte < s.best_chunk_end
                 AND e.span_end_byte > s.best_chunk_start
            THEN 'chunk_overlap'
            WHEN s.sem_norm IS NOT NULL THEN 'inherited'
            ELSE 'none'
        END AS sem_provenance,
        COALESCE(l.rrf_lex, 0) + COALESCE(s.rrf_sem, 0) AS rrf
    FROM enriched e
    LEFT JOIN lex l ON l.node_id = e.node_id
    LEFT JOIN sem s ON s.doc_id = e.doc_id
),

-- Combined score: one formula, one authority
final_scored AS (
    SELECT
        s.*,
        combine(
            s.bm25_score,
            s.fuzzy_score,
            s.sem_score,
            wb := cfg.bm25_w,
            wf := cfg.fuzzy_w,
            ws := cfg.effective_sem_weight
        ) AS score
    FROM scored s
    JOIN config cfg ON TRUE
),

-- ============================================================================
-- PHASE 4: UNIFIED RANKING — document promotion from best child
-- ============================================================================
-- A strong child symbol can promote its parent document:
--   document.score = max(own_score, best_child_score * 0.9)
-- This prevents good symbol matches from being orphaned when their
-- parent document scored low on its own.

doc_best_child AS (
    SELECT doc_id, MAX(score) AS best_child_score
    FROM final_scored
    WHERE node_scope = 'object'
    GROUP BY doc_id
),

promoted AS (
    SELECT
        fs.*,
        CASE
            WHEN fs.node_scope = 'document' AND dc.best_child_score IS NOT NULL
            THEN GREATEST(fs.score, dc.best_child_score * 0.9)
            ELSE fs.score
        END AS promoted_score
    FROM final_scored fs
    LEFT JOIN doc_best_child dc ON dc.doc_id = fs.doc_id
),

ranked AS (
    SELECT
        p.*,
        -- Floor-normalized confidence: subtract noise floor (0.33), rescale to [0,100].
        -- Matches search_pipeline macro. No min-max — absolute, not relative.
        CAST(LEAST(100, GREATEST(1,
            ROUND(100.0 * GREATEST(p.promoted_score - 0.33, 0) / (1.0 - 0.33))
        )) AS INTEGER) AS confidence,
        ROW_NUMBER() OVER (ORDER BY p.promoted_score DESC, LENGTH(p.uri)) AS rank_pos
    FROM promoted p
)

-- ============================================================================
-- OUTPUT — ranked candidates for explore
-- ============================================================================
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
    sem_score,
    promoted_score AS score,
    confidence,
    rrf,
    best_chunk_start,
    best_chunk_end,
    chunk_score,
    sem_provenance,
    rank_pos
FROM ranked
WHERE rank_pos <= (SELECT result_k FROM base_params)
ORDER BY promoted_score DESC, LENGTH(uri)
);
