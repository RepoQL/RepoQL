-- Internal search candidates function combining lexical, fuzzy, and semantic scorers.
-- Semantic is primary signal; lexical acts as modifier/boost.
-- NOTE: This is an internal helper - use search() (the renamed hybrid_search) for public API.
CREATE OR REPLACE MACRO _search_candidates(
    q,
    mode := 'auto',
    k := 50,
    uri_glob := NULL,
    mime_glob := NULL,
    max_cand := 5000,
    bm25_weight := 0.15,
    fuzzy_weight := 0.15,
    semantic_weight := 0.70
) AS TABLE (
WITH base_params AS (
    -- Normalize caller-provided parameters once so every downstream CTE
    -- references stable scalars instead of macro parameters (avoids binder issues).
    SELECT
        coalesce(trim(q), '')                                    AS raw_query,
        lower(coalesce(mode, 'auto'))                            AS requested_mode,
        CAST(coalesce(k, 50) AS BIGINT)                          AS result_k,
        CAST(coalesce(max_cand, 5000) AS BIGINT)                 AS max_candidates,
        coalesce(bm25_weight, 0.15)                              AS bm25_w,
        coalesce(fuzzy_weight, 0.15)                             AS fuzzy_w,
        coalesce(semantic_weight, 0.70)                          AS base_sem_w,
        NULLIF(trim(uri_glob), '')                               AS uri_glob_filter,
        NULLIF(trim(mime_glob), '')                              AS mime_glob_filter
),
-- Classify the query so the engine can route to the best mix of scorers.
classified AS (
    SELECT
        *,
        lower(raw_query)                                         AS keywords_lc,
        CASE WHEN raw_query = '' THEN TRUE ELSE FALSE END        AS keywords_empty,
        CASE
            WHEN requested_mode <> 'auto' THEN requested_mode
            WHEN raw_query = '' THEN 'auto'
            WHEN raw_query LIKE '%::%' OR raw_query LIKE '%.%' OR raw_query LIKE '%()%' THEN 'symbol'
            WHEN regexp_matches(lower(raw_query), '([a-z0-9_]+\.){2,}[a-z0-9_]+') THEN 'symbol'
            WHEN lower(raw_query) LIKE '% exception%' OR lower(raw_query) LIKE '% at %:%' THEN 'error'
            WHEN length(raw_query) > 160 THEN 'heavy'
            ELSE 'auto'
        END                                                      AS route_mode
    FROM base_params
),
-- Configure weighting + candidate limits based on the inferred route.
-- Semantic is always primary; only reduce for pure symbol lookups where exact match matters more.
config AS (
    SELECT
        *,
        CASE
            WHEN keywords_empty THEN 0.80
            WHEN route_mode = 'symbol' THEN base_sem_w * 0.7
            WHEN route_mode = 'heavy' THEN base_sem_w
            WHEN route_mode = 'error' THEN base_sem_w
            ELSE base_sem_w
        END                                                      AS effective_sem_weight,
        CASE
            WHEN route_mode = 'heavy' THEN max_candidates * 2
            WHEN route_mode = 'symbol' THEN LEAST(max_candidates, 4000)
            ELSE max_candidates
        END                                                      AS lex_limit,
        CASE
            WHEN route_mode = 'symbol' THEN LEAST(max_candidates, 2000)
            WHEN route_mode = 'heavy' THEN max_candidates * 2
            ELSE max_candidates
        END                                                      AS dense_limit
    FROM classified
),
-- Pre-filter repo_index with normalized URI/mime columns.
filtered_source AS (
    SELECT
        ri.*,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(lower(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
),
filtered AS (
    SELECT fs.*
    FROM filtered_source fs
         JOIN base_params bp_filter ON TRUE
    WHERE (
            bp_filter.uri_glob_filter IS NULL
            OR repoql_glob_match(fs.uri, bp_filter.uri_glob_filter, TRUE, 'file:///') IS TRUE
            OR repoql_glob_match(fs.uri_local, bp_filter.uri_glob_filter, TRUE, NULL) IS TRUE
        )
      AND (
            bp_filter.mime_glob_filter IS NULL
            OR repoql_glob_match(coalesce(fs.mime, ''), bp_filter.mime_glob_filter, TRUE, NULL) IS TRUE
        )
),
-- Lexical scorer (BM25-ish heuristics + fuzzy subsequence score).
score_source AS (
    SELECT
        ri.node_id,
        ri.doc_id,
        cls.keywords_empty AS lex_keywords_empty,
        concat_ws(' ',
            coalesce(ri.search_key, ''),
            coalesce(ri.basename, ''),
            coalesce(ri.body, ''),
            coalesce(ri.headline, ''),
            coalesce(ri.structure, ''),
            coalesce(ri.symbol, '')
        )                                                     AS text_target,
        CASE
            WHEN cfg.keywords_empty THEN 0.0
            WHEN coalesce(ri.symbol_key, '') = cls.keywords_lc THEN 4.0
            WHEN cls.keywords_lc <> '' AND position(cls.keywords_lc IN coalesce(ri.symbol_key, '')) > 0 THEN 3.2
            WHEN lower(coalesce(ri.basename, '')) = cls.keywords_lc
              OR lower(regexp_replace(coalesce(ri.basename, ''), '\.[^.]*$', '')) = cls.keywords_lc THEN 3.0
            WHEN position(cls.keywords_lc IN lower(coalesce(ri.basename, ''))) > 0 THEN 2.0
            WHEN position(cls.keywords_lc IN ri.search_key) > 0 THEN 1.0
            WHEN position(cls.keywords_lc IN lower(coalesce(ri.body, ''))) > 0 THEN 0.5
            ELSE NULL
        END                                                     AS bm25_heur,
        match_score(cls.keywords_lc, text_target)               AS bm25_fallback,
        -- Use text_target (path + name + content) for per-token scoring, not just search_key
        (SELECT MAX(match_score(trim(t.value), text_target))
            FROM UNNEST(str_split(cls.keywords_lc, ' ')) AS t(value)
            WHERE length(trim(t.value)) > 0)                    AS bm25_tokens,
        match_score(cls.keywords_lc, ri.search_key)             AS fuzz
    FROM filtered ri
         CROSS JOIN classified cls
        JOIN config cfg ON TRUE
    WHERE cfg.keywords_empty = FALSE
),
ranked_lex AS (
    SELECT
        node_id,
        doc_id,
        coalesce(
            bm25_heur,
            bm25_fallback,
            bm25_tokens,
            CASE WHEN lex_keywords_empty THEN 0 ELSE 0.05 END) AS bm25,
        fuzz,
        lex_row
    FROM (
        SELECT
            node_id,
            doc_id,
            lex_keywords_empty,
            bm25_heur,
            bm25_fallback,
            bm25_tokens,
            fuzz,
            ROW_NUMBER() OVER (ORDER BY coalesce(coalesce(bm25_heur, bm25_fallback, bm25_tokens, 0), 0) DESC, fuzz DESC, node_id) AS lex_row
        FROM score_source
    ) ranked
         JOIN config cfg ON TRUE
    WHERE lex_row <= cfg.lex_limit
),
lex_ranked AS (
    SELECT node_id, lex_row AS lex_rank
    FROM ranked_lex
),
normalized_lex AS (
    SELECT
        node_id,
        doc_id,
        zero_one(bm25) AS bm25n,
        zero_one(fuzz) AS fuzzn
    FROM ranked_lex
),
lex_rrf AS (
    SELECT node_id, 1.0 / (60 + lex_rank) AS rrf_lex
    FROM lex_ranked
),
-- Dense scorer (OpenAI-style embedding similarity).
-- Uses BOTH structure embeddings (fast, available immediately) AND full-text embeddings.
-- Applies a boost when both match strongly (high confidence).
semantic_seed AS (
    SELECT
        CASE
            WHEN cfg.keywords_empty THEN NULL
            ELSE cls.raw_query
        END AS query_text
    FROM classified cls
         JOIN config cfg ON TRUE
),
qv AS (
    SELECT embed_text(
                   'Represent this sentence for searching relevant passages: ' || query_text) AS qjson
    FROM semantic_seed
    WHERE query_text IS NOT NULL
),
-- Structure embeddings (fast, always chunk_index=0)
structure_sem AS (
    SELECT
        de.doc_id,
        de.node_id,
        list_cosine_similarity(qv.qjson::FLOAT[], de.embedding) AS struct_sem
    FROM qv
             JOIN document_embedding de ON de.embedding IS NOT NULL
             JOIN filtered ri ON ri.node_id = de.node_id
    WHERE de.scope = 'document'
      AND de.embedding_type = 'structure'
      AND de.dim = array_length(qv.qjson::FLOAT[])
      AND qv.qjson IS NOT NULL
),
-- Full-text embeddings: score all chunks to find best match within each document
full_text_chunks AS (
    SELECT
        de.doc_id,
        de.node_id,
        de.chunk_index,
        de.start_byte,
        de.end_byte,
        list_cosine_similarity(qv.qjson::FLOAT[], de.embedding) AS chunk_sem
    FROM qv
             JOIN document_embedding de ON de.embedding IS NOT NULL
             JOIN filtered ri ON ri.node_id = de.node_id
    WHERE de.scope = 'document'
      AND de.embedding_type = 'full'
      AND de.dim = array_length(qv.qjson::FLOAT[])
      AND qv.qjson IS NOT NULL
),
-- Aggregate full-text to best chunk per document
full_text_scored AS (
    SELECT
        node_id,
        doc_id,
        MAX(chunk_sem) AS full_sem,
        (ARRAY_AGG(chunk_index ORDER BY chunk_sem DESC))[1] AS best_chunk_index,
        (ARRAY_AGG(start_byte ORDER BY chunk_sem DESC))[1] AS best_chunk_start,
        (ARRAY_AGG(end_byte ORDER BY chunk_sem DESC))[1] AS best_chunk_end
    FROM full_text_chunks
    GROUP BY node_id, doc_id
),
-- Combine structure + full-text: take the maximum score with small agreement boost
-- Structure embeddings are fast (available immediately after hot path)
-- Full-text embeddings are more detailed (available after background processing)
sem_scored AS (
    SELECT
        COALESCE(ss.node_id, fs.node_id) AS node_id,
        COALESCE(ss.doc_id, fs.doc_id) AS doc_id,
        -- Use whichever embedding scored higher, plus 5% of combined when both exist
        GREATEST(COALESCE(ss.struct_sem, 0), COALESCE(fs.full_sem, 0))
            + CASE
                WHEN ss.struct_sem IS NOT NULL AND fs.full_sem IS NOT NULL
                THEN 0.05 * (ss.struct_sem + fs.full_sem)
                ELSE 0
            END AS sem,
        fs.best_chunk_index,
        fs.best_chunk_start,
        fs.best_chunk_end
    FROM structure_sem ss
    FULL OUTER JOIN full_text_scored fs ON ss.node_id = fs.node_id
),
sem_top AS (
    SELECT node_id, doc_id, sem, sem_rank
    FROM (
        SELECT
            node_id,
            doc_id,
            sem,
            ROW_NUMBER() OVER (ORDER BY sem DESC, node_id) AS sem_rank
        FROM sem_scored
    ) ranked
             JOIN config cfg ON TRUE
    WHERE sem_rank <= cfg.dense_limit
),
sem_norm AS (
    -- Cubed Boosted Raw: cube aggressively penalizes weak matches
    -- sem=0.7 → 0.39, sem=0.5 → 0.14, sem=0.3 → 0.03
    -- Relative ranking still provides ±15% adjustment
    SELECT
        node_id,
        doc_id,
        POWER(GREATEST(sem, 0), 3) * (0.85 + 0.3 * GREATEST(sem, 0) / NULLIF(MAX(sem) OVER (), 0)) AS semn
    FROM sem_top
),
sem_rrf AS (
    SELECT node_id, 1.0 / (60 + sem_rank) AS rrf_sem
    FROM sem_top
),
-- Union lexical + dense nodes; fall back to recency if both empty.
union_nodes AS (
    SELECT node_id FROM normalized_lex
    UNION
    SELECT node_id FROM sem_norm
),
fallback_nodes AS (
    SELECT node_id
    FROM (
        SELECT
            node_id,
            ROW_NUMBER() OVER (ORDER BY mtime DESC, node_id) AS fallback_row
        FROM filtered
    ) fallback
         JOIN base_params bp ON TRUE
    WHERE fallback_row <= bp.result_k
),
combined_nodes AS (
    SELECT node_id FROM union_nodes
    UNION ALL
    SELECT node_id FROM fallback_nodes
    WHERE NOT EXISTS (SELECT 1 FROM union_nodes)
),
final_nodes AS (
    SELECT DISTINCT node_id AS fn_node_id
    FROM combined_nodes
),
lex_stats AS (
    SELECT COUNT(*) AS cnt FROM normalized_lex
),
sem_stats AS (
    SELECT COUNT(*) AS cnt FROM sem_norm
),
scored AS (
    SELECT
        ri.doc_id,
        ri.node_id,
        ri.uri,
        ri.path,
        ri.scope,
        ri.kind,
        ri.symbol,
        ri.lang,
        ri.mime,
        ri.headline,
        ri.structure,
        -- Use semantic chunk location for snippet when available
        CASE
            WHEN ss.best_chunk_start IS NOT NULL
                 AND ss.best_chunk_end IS NOT NULL
                 AND art.text_content IS NOT NULL
                 AND length(art.text_content) > 0
            THEN array_to_string(
                list_slice(
                    string_split(art.text_content, chr(10)),
                    GREATEST(1, line_for_byte_offset(art.text_content, ss.best_chunk_start) - 2),
                    LEAST(
                        len(string_split(art.text_content, chr(10))),
                        line_for_byte_offset(art.text_content, ss.best_chunk_end) + 2
                    )
                ),
                chr(10)
            )
            ELSE substr(coalesce(ri.body, ''), 1, 640)
        END AS snippet,
        ri.line_start,
        ri.line_end,
        ri.digest,
        coalesce(lx.bm25n, 0)                 AS bm25_score,
        coalesce(lx.fuzzn, 0)                 AS fuzzy_score,
        coalesce(sn.semn, 0)                  AS dense_score,
        coalesce(rlex.rrf_lex, 0) + coalesce(rsem.rrf_sem, 0) AS rrf
        -- score computed in final_with_conf using doc_semn for objects
    FROM final_nodes fn
             JOIN filtered ri ON ri.node_id = fn.fn_node_id
             LEFT JOIN normalized_lex lx ON lx.node_id = fn.fn_node_id
             LEFT JOIN (
                 SELECT node_id, MAX(semn) AS semn
                 FROM sem_norm
                 GROUP BY node_id
             ) sn ON sn.node_id = fn.fn_node_id
             LEFT JOIN sem_scored ss ON ss.node_id = fn.fn_node_id
             LEFT JOIN node doc_node ON doc_node.id = ri.doc_id
             LEFT JOIN artifact art ON art.id = doc_node.artifact_id
             LEFT JOIN lex_rrf rlex ON rlex.node_id = fn.fn_node_id
             LEFT JOIN sem_rrf rsem ON rsem.node_id = fn.fn_node_id
             JOIN config cfg ON TRUE
             JOIN classified cls ON TRUE
), doc_sem AS (
    SELECT doc_id, MAX(dense_score) AS doc_semn
    FROM scored
    GROUP BY doc_id
), final_with_conf AS (
    -- Compute score using doc_semn so objects get semantic signal from their parent document
    SELECT
        fws.*,
        CASE
            WHEN fws.score >= 2 THEN 0.95
            WHEN fws.score >= 1.2 THEN 0.8
            WHEN fws.score >= 0.8 THEN 0.65
            ELSE 0.4
        END AS confidence
    FROM (
        SELECT
            s.*,
            coalesce(ds.doc_semn, s.dense_score) AS doc_semn,
            combine(
                s.bm25_score,
                s.fuzzy_score,
                coalesce(ds.doc_semn, s.dense_score),
                wb := cfg.bm25_w,
                wf := cfg.fuzzy_w,
                ws := cfg.effective_sem_weight
            ) AS score,
            cls.route_mode,
            cls.uri_glob_filter,
            cls.mime_glob_filter,
            cls.keywords_empty,
            cls.keywords_lc,
            cls.requested_mode,
            json_object(
                'route', cls.route_mode,
                'uri_glob_applied', cls.uri_glob_filter IS NOT NULL,
                'mime_glob_applied', cls.mime_glob_filter IS NOT NULL,
                'keywords_empty', cls.keywords_empty
            ) AS boosts_json,
            json_object(
                'route', cls.route_mode,
                'lex_candidates', (SELECT cnt FROM lex_stats),
                'dense_candidates', (SELECT cnt FROM sem_stats),
                'requested_mode', cls.requested_mode
            ) AS explain_json
        FROM scored s
        LEFT JOIN doc_sem ds ON ds.doc_id = s.doc_id
        JOIN classified cls ON TRUE
        JOIN config cfg ON TRUE
    ) fws
)
SELECT *
FROM final_with_conf
ORDER BY
    CASE
        WHEN uri_glob_filter IS NOT NULL AND scope = 'document' THEN 0
        WHEN uri_glob_filter IS NOT NULL THEN 1
        WHEN uri_glob_filter IS NULL
             AND coalesce(symbol, '') = coalesce(keywords_lc, '')
             AND coalesce(keywords_lc, '') <> '' THEN -1
        ELSE 0
    END,
    score DESC,
    length(uri)
LIMIT (SELECT result_k FROM base_params)
);

-- Lightweight "find related documents" helper that uses the same filtering approach.
CREATE OR REPLACE MACRO related(
    seed_uri,
    k := 20,
    mode := 'mixed',
    uri_glob := NULL,
    mime_glob := NULL
) AS TABLE (
WITH base_params AS (
    SELECT
        coalesce(trim(seed_uri), '')                              AS seed,
        CAST(coalesce(k, 20) AS BIGINT)                           AS result_k,
        lower(coalesce(mode, 'mixed'))                            AS requested_mode,
        NULLIF(trim(uri_glob), '')                                AS uri_glob_filter,
        NULLIF(trim(mime_glob), '')                               AS mime_glob_filter
),
seed AS (
    SELECT *
    FROM repo_index
         JOIN base_params bp_seed ON TRUE
    WHERE uri = bp_seed.seed
    LIMIT 1
),
related_source AS (
    SELECT
        ri.*,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(lower(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
         JOIN base_params bp_rs ON TRUE
    WHERE ri.uri <> bp_rs.seed
),
filtered AS (
    SELECT rs.*
    FROM related_source rs
         JOIN base_params bp_filter ON TRUE
    WHERE (
            bp_filter.uri_glob_filter IS NULL
            OR repoql_glob_match(rs.uri, bp_filter.uri_glob_filter, TRUE, 'file:///') IS TRUE
            OR repoql_glob_match(rs.uri_local, bp_filter.uri_glob_filter, TRUE, NULL) IS TRUE
        )
      AND (
            bp_filter.mime_glob_filter IS NULL
            OR repoql_glob_match(coalesce(rs.mime, ''), bp_filter.mime_glob_filter, TRUE, NULL) IS TRUE
        )
),
-- Score candidate documents relative to the provided seed URI.
scored AS (
    SELECT
        f.*,
        CASE
            WHEN seed.embedding IS NOT NULL AND f.embedding IS NOT NULL
                THEN list_cosine_similarity(seed.embedding, f.embedding)
            ELSE NULL
        END AS sim_score,
        match_score(lower(coalesce(seed.symbol_key, seed.search_key, '')), f.search_key) AS bm25_score,
        0.0 AS xref_score
    FROM filtered f
             JOIN seed ON TRUE
),
-- Rank by blended semantic + lexical score for output + RRF.
final AS (
    SELECT
        *,
        coalesce(sim_score, 0) * 0.7 + coalesce(bm25_score, 0) * 0.3 AS score,
        ROW_NUMBER() OVER (
            ORDER BY
                coalesce(sim_score, 0) * 0.7 + coalesce(bm25_score, 0) * 0.3 DESC,
                length(uri),
                uri
        ) AS rel_row,
        1.0 / (10 + ROW_NUMBER() OVER (ORDER BY coalesce(sim_score, 0) DESC, coalesce(bm25_score, 0) DESC, uri)) AS rrf
    FROM scored
),
-- Apply the caller's limit without using LIMIT expressions on macro params.
limited AS (
    SELECT f.*
    FROM final f
             JOIN base_params bp_limit ON TRUE
    WHERE f.rel_row <= bp_limit.result_k
)
SELECT
    doc_id,
    node_id,
    uri,
    path,
    scope,
    kind,
    symbol,
    lang,
    mime,
    headline,
    structure,
    -- TODO: Add chunk-based snippet extraction (requires adding per-chunk similarity scoring)
    substr(coalesce(body, ''), 1, 640) AS snippet,
    line_start,
    line_end,
    digest,
    bm25_score,
    sim_score AS dense_score,
    xref_score,
    score,
    rrf,
    CASE
        WHEN score >= 1.8 THEN 0.9
        WHEN score >= 1.0 THEN 0.75
        WHEN score >= 0.6 THEN 0.55
        ELSE 0.35
    END AS confidence,
    json_object('mode', bp_out.requested_mode, 'seed_uri', bp_out.seed) AS explain_json
FROM limited
         JOIN base_params bp_out ON TRUE
ORDER BY score DESC, length(uri)
);


