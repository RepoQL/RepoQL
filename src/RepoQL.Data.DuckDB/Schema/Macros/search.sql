-- Internal search candidates function combining lexical and semantic scorers.
-- This is a slim orchestrator that calls modular components:
--   _search_lexical()  - BM25/fuzzy scoring
--   _search_semantic() - HNSW/linear semantic scoring
-- NOTE: This is an internal helper - use search() (in hybrid_search.sql) for public API.

CREATE OR REPLACE MACRO _search_candidates(
    q,
    mode := 'auto',
    k := 50,
    uri_glob := NULL,
    max_cand := 5000,
    bm25_weight := 0.15,
    fuzzy_weight := 0.15,
    semantic_weight := 0.70,
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
        CAST(COALESCE(k, 50) AS BIGINT) AS result_k,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS max_candidates,
        COALESCE(bm25_weight, 0.15) AS bm25_w,
        COALESCE(fuzzy_weight, 0.15) AS fuzzy_w,
        COALESCE(semantic_weight, 0.70) AS base_sem_w,
        NULLIF(TRIM(uri_glob), '') AS uri_glob_filter,
        NULLIF(TRIM(uri_like), '') AS uri_like_filter
),

-- Query classification for routing and weight adjustment
classified AS (
    SELECT
        *,
        LOWER(raw_query) AS keywords_lc,
        CASE WHEN raw_query = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        _search_classify_query(raw_query) AS route_mode
    FROM base_params
),

-- Weight/limit configuration based on query type
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
-- CALL MODULAR SEARCH COMPONENTS
-- ============================================================================

-- Lexical scoring (BM25 + fuzzy)
lex AS (
    SELECT * FROM _search_lexical(
        q := (SELECT raw_query FROM base_params),
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        max_cand := (SELECT max_candidates FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

-- Semantic scoring (HNSW when available, linear fallback)
sem AS (
    SELECT * FROM _search_semantic(
        q := (SELECT raw_query FROM base_params),
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        max_cand := (SELECT max_candidates FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

-- ============================================================================
-- COMBINE SCORES + INLINE OBJECT SCORING
-- ============================================================================

-- Top documents from both scorers (for object expansion)
top_docs AS (
    SELECT DISTINCT doc_id
    FROM (
        SELECT doc_id FROM lex WHERE lex_rank <= 200
        UNION
        SELECT doc_id FROM sem WHERE sem_rank <= 200
    ) ranked_docs
),

-- Grep line hits scoped to top documents (for object span mapping)
grep_for_objects AS (
    SELECT DISTINCT n.id AS doc_id, TRY_CAST(g.line_number AS INTEGER) AS line_number
    FROM base_params bp,
         grep_matches(bp.raw_query, '**', 500) g
    JOIN node n ON n.uri = g.uri AND n.kind = 'document'
    JOIN top_docs td ON td.doc_id = n.id
    WHERE bp.raw_query <> ''
),

-- Expand top documents to child objects via span
obj_children AS (
    SELECT
        child.id AS node_id,
        s.document_id AS doc_id,
        s.start_line,
        s.end_line,
        s.start_byte,
        s.end_byte,
        LOWER(COALESCE(
            repository_uri_symbol(child.uri),
            json_extract_string(child.properties, '$.name'),
            ''
        )) AS symbol_key,
        LOWER(COALESCE(child.headline, '') || ' ' || COALESCE(child.structure, '')) AS headline_text
    FROM top_docs td
    JOIN span s ON s.document_id = td.doc_id
    JOIN node child ON child.span_id = s.id
    WHERE child.kind <> 'document'
),

-- Score objects: grep overlap + symbol match + headline match in one pass
scored_objs AS (
    SELECT
        c.node_id,
        c.doc_id,
        -- Symbol scoring
        CASE
            WHEN bp.keywords_lc <> '' AND c.symbol_key = bp.keywords_lc THEN 4.0
            WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.symbol_key) > 0 THEN 3.2
            ELSE 0.0
        END AS symbol_score,
        -- Grep overlap
        COUNT(DISTINCT g.line_number) AS grep_hits,
        CASE
            WHEN COUNT(DISTINCT g.line_number) >= 2 THEN 2.5 + 0.1 * COUNT(DISTINCT g.line_number)
            WHEN COUNT(DISTINCT g.line_number) = 1 THEN 2.0
            ELSE 0.0
        END AS grep_score,
        -- Headline/structure match
        CASE
            WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.headline_text) > 0 THEN 1.5
            ELSE 0.0
        END AS headline_score,
        CASE
            WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.headline_text) > 0 THEN TRUE
            ELSE FALSE
        END AS headline_hit,
        -- Semantic chunk overlap deferred to avoid re-evaluation of sem CTE.
        -- TODO: add when embed_query result can be shared across CTEs.
        CAST(NULL AS DOUBLE) AS chunk_sem
    FROM obj_children c
    CROSS JOIN (SELECT LOWER(COALESCE(TRIM(raw_query), '')) AS keywords_lc FROM base_params) bp
    LEFT JOIN grep_for_objects g
        ON g.doc_id = c.doc_id
       AND c.start_line IS NOT NULL AND c.end_line IS NOT NULL
       AND g.line_number BETWEEN c.start_line AND c.end_line
    GROUP BY c.node_id, c.doc_id, c.symbol_key, c.headline_text, c.start_line, bp.keywords_lc
    -- Combined score: best signal + semantic boost
    HAVING GREATEST(
        CASE WHEN bp.keywords_lc <> '' AND c.symbol_key = bp.keywords_lc THEN 4.0
             WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.symbol_key) > 0 THEN 3.2
             ELSE 0.0 END,
        CASE WHEN COUNT(DISTINCT g.line_number) >= 2 THEN 2.5 WHEN COUNT(DISTINCT g.line_number) = 1 THEN 2.0 ELSE 0.0 END,
        CASE WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.headline_text) > 0 THEN 1.5 ELSE 0.0 END
    ) > 0
    -- Per-doc cap
    QUALIFY ROW_NUMBER() OVER (
        PARTITION BY c.doc_id
        ORDER BY GREATEST(
            CASE WHEN bp.keywords_lc <> '' AND c.symbol_key = bp.keywords_lc THEN 4.0
                 WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.symbol_key) > 0 THEN 3.2
                 ELSE 0.0 END,
            CASE WHEN COUNT(DISTINCT g.line_number) >= 2 THEN 2.5 WHEN COUNT(DISTINCT g.line_number) = 1 THEN 2.0 ELSE 0.0 END,
            CASE WHEN bp.keywords_lc <> '' AND position(bp.keywords_lc IN c.headline_text) > 0 THEN 1.5 ELSE 0.0 END
        ) DESC, c.start_line NULLS LAST, c.node_id
    ) <= 20
),

-- Add object_score column for downstream scoring
scored_objs_final AS (
    SELECT
        node_id,
        doc_id,
        GREATEST(symbol_score, grep_score, headline_score) + 0.3 * COALESCE(chunk_sem, 0) AS object_score,
        symbol_score,
        grep_hits,
        chunk_sem,
        headline_hit
    FROM scored_objs
),

-- Union all candidate nodes from both scorers plus scored objects
union_nodes AS (
    SELECT node_id FROM lex
    UNION
    SELECT node_id FROM sem
    UNION
    SELECT node_id FROM scored_objs_final
),

-- Scope filter for fallback (recency-based when both scorers return nothing)
_sc_scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        uri_like := (SELECT uri_like_filter FROM base_params)
    )
),

fallback_nodes AS (
    SELECT sf.node_id
    FROM _sc_scope sf
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

-- Stats for explain_json
lex_stats AS (SELECT COUNT(*) AS cnt FROM lex),
sem_stats AS (SELECT COUNT(*) AS cnt FROM sem),

-- ============================================================================
-- ENRICH: compute metadata only for final scored/fallback nodes
-- ============================================================================

enriched AS (
    SELECT
        sf.doc_id,
        fn.fn_node_id AS node_id,
        sf.node_scope,
        n.kind,
        -- URI: documents use node URI, objects reconstruct if needed
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
        a.digest,
        a.text_content
    FROM final_nodes fn
    JOIN _sc_scope sf ON sf.node_id = fn.fn_node_id
    JOIN node n ON n.id = fn.fn_node_id
    LEFT JOIN span sp ON sp.id = n.span_id
    LEFT JOIN node doc ON doc.id = sf.doc_id
    LEFT JOIN artifact a ON a.id = COALESCE(
        CASE WHEN n.kind = 'document' THEN n.artifact_id END,
        doc.artifact_id
    )
),

-- ============================================================================
-- SCORE
-- ============================================================================

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
        -- Use semantic chunk location for snippet when available
        CASE
            WHEN s.best_chunk_start IS NOT NULL
                 AND s.best_chunk_end IS NOT NULL
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
            -- Fallback: for documents use text_content, for objects use metadata
            WHEN e.node_scope = 'document'
            THEN substr(COALESCE(e.text_content, ''), 1, 640)
            ELSE substr(COALESCE(e.headline || E'\n\n' || e.structure, e.headline, e.structure, ''), 1, 640)
        END AS snippet,
        e.line_start,
        e.line_end,
        e.digest,
        CASE
            WHEN e.node_scope = 'object' AND o.node_id IS NOT NULL
            THEN LEAST(COALESCE(o.object_score, 0) / 4.5, 1.0)
            ELSE COALESCE(l.bm25_norm, 0)
        END AS bm25_score,
        CASE
            WHEN e.node_scope = 'object' AND o.node_id IS NOT NULL
            THEN LEAST(COALESCE(o.symbol_score, 0) / 4.0, 1.0)
            ELSE COALESCE(l.fuzz_norm, 0)
        END AS fuzzy_score,
        CASE
            WHEN e.node_scope = 'object' AND o.node_id IS NOT NULL
            THEN GREATEST(
                COALESCE(s.sem_norm, 0),
                LEAST(GREATEST(COALESCE(o.chunk_sem, 0), 0), 1.0)
            )
            ELSE COALESCE(s.sem_norm, 0)
        END AS dense_score,
        COALESCE(o.object_score, 0) AS object_score,
        COALESCE(l.rrf_lex, 0) + COALESCE(s.rrf_sem, 0) AS rrf
    FROM enriched e
    LEFT JOIN lex l ON l.node_id = e.node_id
    LEFT JOIN sem s ON s.doc_id = e.doc_id
    LEFT JOIN scored_objs_final o ON o.node_id = e.node_id
),

-- Propagate semantic score from document to its objects
doc_sem AS (
    SELECT doc_id, MAX(sem_norm) AS doc_semn
    FROM sem
    GROUP BY doc_id
),

-- Final scoring with confidence
final_with_conf AS (
    SELECT
        fws.doc_id,
        fws.node_id,
        fws.uri,
        fws.path,
        fws.node_scope,
        fws.kind,
        fws.symbol,
        fws.lang,
        fws.mime,
        fws.headline,
        fws.structure,
        fws.snippet,
        fws.line_start,
        fws.line_end,
        fws.digest,
        fws.bm25_score,
        fws.fuzzy_score,
        fws.dense_score,
        fws.rrf,
        fws.doc_semn,
        fws.score,
        fws.route_mode,
        fws.uri_glob_filter,
        fws.keywords_empty,
        fws.keywords_lc,
        fws.requested_mode,
        fws.boosts_json,
        fws.explain_json,
        score_confidence(fws.score) AS confidence
    FROM (
        SELECT
            s.*,
            COALESCE(ds.doc_semn, s.dense_score) AS doc_semn,
            combine(
                s.bm25_score,
                s.fuzzy_score,
                GREATEST(COALESCE(ds.doc_semn, s.dense_score), s.dense_score),
                wb := cfg.bm25_w,
                wf := cfg.fuzzy_w,
                ws := cfg.effective_sem_weight
            ) + CASE
                WHEN s.node_scope = 'object'
                THEN 0.25 * LEAST(s.object_score / 4.5, 1.0)
                ELSE 0
            END AS score,
            cls.route_mode,
            cls.uri_glob_filter,
            cls.keywords_empty,
            cls.keywords_lc,
            cls.requested_mode,
            json_object(
                'route', cls.route_mode,
                'uri_glob_applied', cls.uri_glob_filter IS NOT NULL,
                'keywords_empty', cls.keywords_empty
            ) AS boosts_json,
            json_object(
                'route', cls.route_mode,
                'lex_candidates', (SELECT cnt FROM lex_stats),
                'dense_candidates', (SELECT cnt FROM sem_stats),
                'object_candidates', (SELECT COUNT(*) FROM scored_objs_final),
                'requested_mode', cls.requested_mode
            ) AS explain_json
        FROM scored s
        LEFT JOIN doc_sem ds ON ds.doc_id = s.doc_id
        JOIN classified cls ON TRUE
        JOIN config cfg ON TRUE
    ) fws
)

-- ============================================================================
-- OUTPUT
-- ============================================================================
SELECT *
FROM final_with_conf
ORDER BY
    CASE
        WHEN uri_glob_filter IS NOT NULL AND node_scope = 'document' THEN 0
        WHEN uri_glob_filter IS NOT NULL THEN 1
        WHEN uri_glob_filter IS NULL
             AND COALESCE(symbol, '') = COALESCE(keywords_lc, '')
             AND COALESCE(keywords_lc, '') <> '' THEN -1
        ELSE 0
    END,
    score DESC,
    LENGTH(uri)
LIMIT (SELECT result_k FROM base_params)
);

-- ============================================================================
-- RELATED DOCUMENTS HELPER
-- ============================================================================
-- Lightweight "find related documents" helper.
-- Scores by cosine similarity (embedding) + BM25 (search_key).
-- Enriches only top-K results with full metadata.
CREATE OR REPLACE MACRO related(
    seed_uri,
    k := 20,
    mode := 'mixed',
    uri_glob := NULL
) AS TABLE (
WITH base_params AS (
    SELECT
        COALESCE(TRIM(seed_uri), '') AS seed,
        CAST(COALESCE(k, 20) AS BIGINT) AS result_k,
        LOWER(COALESCE(mode, 'mixed')) AS requested_mode,
        NULLIF(TRIM(uri_glob), '') AS uri_glob_filter
),

-- Seed: direct node + embedding lookup (no repo_index)
seed AS (
    SELECT
        n.id AS node_id,
        n.uri,
        de.embedding,
        LOWER(REPLACE(repository_uri_container(n.uri), '\\', '/')) AS search_key,
        LOWER(COALESCE(
            repository_uri_symbol(n.uri),
            json_extract_string(n.properties, '$.symbol'),
            json_extract_string(n.properties, '$.name'),
            ''
        )) AS symbol_key
    FROM node n
    LEFT JOIN document_embedding de
        ON de.node_id = n.id AND de.embedding_type = 'full' AND de.chunk_index = 0
    WHERE n.uri = (SELECT seed FROM base_params)
    LIMIT 1
),

-- Scope-filtered candidates (excluding seed document)
_rel_scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := (SELECT uri_glob_filter FROM base_params),
        exclude_uri := (SELECT seed FROM base_params)
    )
),

-- Slim candidates for scoring: embedding + search_key only
scoring_candidates AS (
    SELECT
        sf.node_id,
        sf.doc_id,
        sf.node_scope,
        de.embedding,
        LOWER(REPLACE(repository_uri_container(COALESCE(n.uri, doc.uri, 'unknown')), '\\', '/')) AS search_key
    FROM _rel_scope sf
    JOIN node n ON n.id = sf.node_id
    LEFT JOIN node doc ON doc.id = sf.doc_id
    LEFT JOIN document_embedding de ON de.node_id = sf.node_id
        AND de.embedding_type = 'full' AND de.chunk_index = 0
),

-- Score all candidates
scored AS (
    SELECT
        f.*,
        safe_cosine(seed.embedding, f.embedding) AS sim_score,
        TRY_CAST(match_score(LOWER(COALESCE(seed.symbol_key, seed.search_key, '')), f.search_key) AS DOUBLE) AS bm25_score,
        0.0 AS xref_score
    FROM scoring_candidates f
    JOIN seed ON TRUE
),

-- Top-K via QUALIFY
final AS (
    SELECT
        *,
        COALESCE(sim_score, 0) * 0.7 + COALESCE(bm25_score, 0) * 0.3 AS score,
        ROW_NUMBER() OVER (
            ORDER BY
                COALESCE(sim_score, 0) * 0.7 + COALESCE(bm25_score, 0) * 0.3 DESC,
                LENGTH(COALESCE(
                    (SELECT uri FROM node WHERE id = node_id),
                    ''
                )),
                node_id
        ) AS rel_row,
        rrf_score(ROW_NUMBER() OVER (ORDER BY COALESCE(sim_score, 0) DESC, COALESCE(bm25_score, 0) DESC, node_id), 10) AS rrf
    FROM scored
    QUALIFY rel_row <= (SELECT result_k FROM base_params)
),

-- Enrich only top-K with full metadata
enriched_final AS (
    SELECT
        f.doc_id,
        f.node_id,
        COALESCE(
            n.uri,
            repository_uri_join(
                COALESCE(doc.uri, 'repoql://document/' || CAST(f.doc_id AS VARCHAR)),
                COALESCE(
                    fragment_from_line_range(CAST(sp.start_line AS VARCHAR), CAST(sp.end_line AS VARCHAR)),
                    concat('node/', n.kind, '/', REPLACE(CAST(n.id AS VARCHAR), '-', ''))
                )
            )
        ) AS uri,
        REPLACE(repository_uri_container(COALESCE(doc.uri, n.uri, 'repoql://unknown')), '\\', '/') AS path,
        f.node_scope,
        n.kind,
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
        substr(COALESCE(
            CASE WHEN n.kind = 'document'
                THEN COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, ''))
                ELSE COALESCE(NULLIF(n.headline, ''), json_extract_string(n.properties, '$.name'), repository_uri_file_name(doc.uri))
            END
            || E'\n\n' ||
            CASE WHEN n.kind = 'document'
                THEN COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, ''))
                ELSE NULLIF(n.structure, '')
            END,
            CASE WHEN n.kind = 'document'
                THEN COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, ''))
                ELSE COALESCE(NULLIF(n.headline, ''), json_extract_string(n.properties, '$.name'), repository_uri_file_name(doc.uri))
            END,
            CASE WHEN n.kind = 'document'
                THEN COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, ''))
                ELSE NULLIF(n.structure, '')
            END,
            ''
        ), 1, 640) AS snippet,
        COALESCE(sp.start_line, TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER)) AS line_start,
        COALESCE(sp.end_line, TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER)) AS line_end,
        a.digest,
        f.bm25_score,
        f.sim_score AS dense_score,
        f.xref_score,
        f.score,
        f.rrf,
        score_confidence(f.score) AS confidence,
        json_object('mode', bp_out.requested_mode, 'seed_uri', bp_out.seed) AS explain_json
    FROM final f
    JOIN node n ON n.id = f.node_id
    LEFT JOIN span sp ON sp.id = n.span_id
    LEFT JOIN node doc ON doc.id = f.doc_id
    LEFT JOIN artifact a ON a.id = COALESCE(
        CASE WHEN n.kind = 'document' THEN n.artifact_id END,
        doc.artifact_id
    )
    JOIN base_params bp_out ON TRUE
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
    dense_score,
    xref_score,
    score,
    rrf,
    confidence,
    explain_json
FROM enriched_final
ORDER BY score DESC, LENGTH(uri)
);
