-- Typed wrapper over the structured search pipeline UDF. The generated UDF macro
-- returns strings via json_each; this wrapper restores the historical UUID/numeric
-- contract expected by search(), explore, find, and object search.
CREATE OR REPLACE MACRO search_pipeline(
    query,
    scope := NULL,
    k := 50,
    top_doc_limit := 200,
    per_doc_cap := 20
) AS TABLE (
WITH raw AS (
    SELECT j.value
    FROM json_each(
        _search_pipeline_internal(
            COALESCE(query::VARCHAR, ''),
            COALESCE(scope::VARCHAR, ''),
            json_object(
                'k', k,
                'top_doc_limit', top_doc_limit,
                'per_doc_cap', per_doc_cap
            )
        )
    ) AS j
    WHERE j.type = 'OBJECT'
)
SELECT
    TRY_CAST(value->>'doc_id' AS UUID) AS doc_id,
    TRY_CAST(value->>'node_id' AS UUID) AS node_id,
    value->>'uri' AS uri,
    value->>'path' AS path,
    value->>'node_scope' AS node_scope,
    value->>'kind' AS kind,
    value->>'symbol' AS symbol,
    value->>'lang' AS lang,
    value->>'mime' AS mime,
    value->>'headline' AS headline,
    value->>'structure' AS structure,
    value->>'snippet' AS snippet,
    TRY_CAST(value->>'line_start' AS INTEGER) AS line_start,
    TRY_CAST(value->>'line_end' AS INTEGER) AS line_end,
    value->>'digest' AS digest,
    TRY_CAST(value->>'bm25_score' AS DOUBLE) AS bm25_score,
    TRY_CAST(value->>'fuzzy_score' AS DOUBLE) AS fuzzy_score,
    TRY_CAST(value->>'dense_score' AS DOUBLE) AS dense_score,
    TRY_CAST(value->>'rrf' AS DOUBLE) AS rrf,
    TRY_CAST(value->>'doc_semn' AS DOUBLE) AS doc_semn,
    -- Rescale score: subtract noise floor (0.33) so irrelevant results → 0.
    -- Floor is the Combine() output for zero lexical + weak semantic (~0.55 * 0.6).
    GREATEST(
        (TRY_CAST(value->>'score' AS DOUBLE) - 0.33) / (1.0 - 0.33),
        0
    ) AS score,
    GREATEST(
        (TRY_CAST(value->>'confidence' AS DOUBLE) - 0.33) / (1.0 - 0.33),
        0
    ) AS confidence,
    value->>'explain_json' AS explain_json,
    value->>'sem_provenance' AS sem_provenance
FROM raw
);

-- Internal search candidates function combining lexical and semantic scorers.
-- This now delegates orchestration to the C# search_pipeline UDF while preserving
-- the SQL surface and output contract used elsewhere in the system.
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
WITH candidates AS (
    SELECT *
    FROM search_pipeline(
        q,
        scope := uri_glob,
        k := k,
        top_doc_limit := LEAST(COALESCE(max_cand, 5000), 500),
        per_doc_cap := 20
    )
),
filtered AS (
    SELECT *
    FROM candidates
    WHERE uri_like IS NULL
       OR uri LIKE uri_like
       OR path LIKE uri_like
)
SELECT *
FROM filtered
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
        calibrated_cosine(seed.embedding, f.embedding) AS sim_score,
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
