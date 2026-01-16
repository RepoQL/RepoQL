-- Primary search function: semantic/BM25 retrieval + regex-based boosting/deranking.
-- Rescue is cheap by default (headline+structure). Enable body rescue for maximum recall.
--
-- Parameters:
--   keywords          - literal keywords for searching (required)
--   scope             - SQL LIKE pattern for doc URIs; NULL/'' => all docs
--   boost_pattern     - regex used for boosting + rescue (optional, derived from keywords if not provided)
--   negative_pattern  - regex used for de-ranking (optional)
--   k                 - max candidates
--   sem_threshold     - min semantic score for tier 1
--   bm25_threshold    - min BM25 score for tier 2
--   derank_factor     - multiplier when negative_pattern matches (0.5 = half score)
--   enable_body_rescue - TRUE => scan bodies to rescue docs missed (expensive)
--
-- Example usage:
--   SELECT * FROM search('database connection') LIMIT 10;
--   SELECT * FROM search('parser', boost_pattern := 'markdown|yaml', negative_pattern := '(?i)test');
--   SELECT * FROM search('config', scope := 'file:///src/%');

CREATE OR REPLACE MACRO search(
    keywords,
    scope := NULL,
    boost_pattern := NULL,
    negative_pattern := NULL,
    k := 200,
    sem_threshold := 0.35,
    bm25_threshold := 0.10,
    derank_factor := 0.5,
    enable_body_rescue := FALSE
) AS TABLE
WITH
params AS (
    SELECT
        trim(coalesce(keywords, '')) AS kw,
        COALESCE(NULLIF(scope, ''), '%') AS scope_like,
        NULLIF(trim(coalesce(boost_pattern, '')), '') AS boost_in,
        CAST(negative_pattern AS VARCHAR) AS neg_re
),

-- Derive boost regex from keywords (split on whitespace to create OR alternatives)
-- "jwt token" -> "jwt|token"
-- Skip derivation if keywords look like a question (ends with ?) - questions make bad regexes
cfg AS (
    SELECT
        kw,
        scope_like,
        neg_re,
        CAST(COALESCE(
            boost_in,
            CASE WHEN kw LIKE '%?' THEN NULL
                 ELSE regexp_replace(kw, '\s+', '|', 'g')
            END
        ) AS VARCHAR) AS boost_re
    FROM params
),

-- Outline-only corpus (cheap to scan); body is joined only for final candidates
docs_outline AS (
    SELECT
        n.uri AS doc_uri,
        n.artifact_id AS artifact_id,
        a.headline,
        a.structure,
        coalesce(a.headline,'') || ' ' || coalesce(a.structure,'') AS outline_text
    FROM node n
    JOIN artifact a ON n.artifact_id = a.id
    CROSS JOIN cfg c
    WHERE n.kind = 'document'
      AND n.uri LIKE c.scope_like
),

-- Search results aggregated to doc level (fixes "bm25 under-count in tier1" issue)
search_rows AS (
    SELECT
        split_part(uri, '#', 1) AS doc_uri,
        doc_semn,
        bm25_score
    FROM _search_candidates((SELECT kw FROM cfg), k := k)
),
search_docs AS (
    SELECT
        sr.doc_uri,
        MAX(sr.doc_semn)    AS sem_score,
        MAX(sr.bm25_score)  AS bm25_score
    FROM search_rows sr
    JOIN docs_outline d ON d.doc_uri = sr.doc_uri
    GROUP BY 1
),

-- Tiers: semantic, bm25, and a "search tail" tier so docs returned by search() aren't dropped
tiered AS (
    SELECT
        doc_uri,
        sem_score,
        bm25_score,
        CASE
            WHEN sem_score >= sem_threshold THEN 'semantic'
            WHEN bm25_score >= bm25_threshold THEN 'bm25'
            ELSE 'search'
        END AS src
    FROM search_docs
),

-- Cheap rescue: regex on headline+structure only (no full-body scan)
outline_rescue AS (
    SELECT
        d.doc_uri,
        CAST(NULL AS DOUBLE) AS sem_score,
        CAST(NULL AS DOUBLE) AS bm25_score,
        'outline' AS src
    FROM docs_outline d
    CROSS JOIN cfg c
    WHERE length(c.boost_re) > 0
      AND regexp_matches(d.outline_text, '(?i)' || c.boost_re)
      AND NOT EXISTS (SELECT 1 FROM search_docs sd WHERE sd.doc_uri = d.doc_uri)
),

-- Optional expensive rescue: scan bodies for docs missed by search() + outline rescue
body_rescue AS (
    SELECT
        d.doc_uri,
        CAST(NULL AS DOUBLE) AS sem_score,
        CAST(NULL AS DOUBLE) AS bm25_score,
        'body' AS src
    FROM docs_outline d
    JOIN artifact a ON a.id = d.artifact_id
    CROSS JOIN cfg c
    WHERE enable_body_rescue
      AND length(c.boost_re) > 0
      AND regexp_matches(coalesce(a.text_content,''), '(?i)' || c.boost_re)
      AND NOT EXISTS (SELECT 1 FROM search_docs sd WHERE sd.doc_uri = d.doc_uri)
      AND NOT EXISTS (SELECT 1 FROM outline_rescue orc WHERE orc.doc_uri = d.doc_uri)
),

combined AS (
    SELECT doc_uri, sem_score, bm25_score, src FROM tiered
    UNION ALL
    SELECT doc_uri, sem_score, bm25_score, src FROM outline_rescue
    UNION ALL
    SELECT doc_uri, sem_score, bm25_score, src FROM body_rescue
),

-- Compute features once (avoid recomputing regex work in the score expression)
features AS (
    SELECT
        c.doc_uri AS uri,
        d.headline,
        d.structure,
        c.src AS source,
        COALESCE(c.sem_score, 0.0)  AS sem_score,
        COALESCE(c.bm25_score, 0.0) AS bm25_score,
        CASE WHEN length(cfg.boost_re) > 0
             THEN COALESCE(array_length(regexp_extract_all(coalesce(d.outline_text,''), '(?i)' || cfg.boost_re)), 0)
             ELSE 0 END AS outline_mentions,
        CASE WHEN length(cfg.boost_re) > 0
             THEN COALESCE(array_length(regexp_extract_all(coalesce(a.text_content,''), '(?i)' || cfg.boost_re)), 0)
             ELSE 0 END AS body_mentions,
        CASE
            WHEN cfg.neg_re IS NOT NULL AND length(cfg.neg_re) > 0
                 AND regexp_matches(coalesce(d.outline_text,'') || ' ' || coalesce(a.text_content,''), cfg.neg_re)
            THEN true ELSE false
        END AS deranked
    FROM combined c
    JOIN docs_outline d ON d.doc_uri = c.doc_uri
    JOIN artifact a ON a.id = d.artifact_id
    CROSS JOIN cfg
)

SELECT
    uri,
    headline,
    structure,
    source,
    ROUND(sem_score, 3)  AS sem_score,
    ROUND(bm25_score, 3) AS bm25_score,
    outline_mentions AS struct_mentions,
    body_mentions,
    deranked,
    ROUND(
        -- Base score by tier
        (CASE
            WHEN source = 'semantic' THEN sem_score
            WHEN source = 'bm25'     THEN 0.30
            WHEN source = 'outline'  THEN 0.25
            WHEN source = 'body'     THEN 0.20
            ELSE 0.18
         END)
        * (
            1.0
            -- BM25 boost (smooth, capped at +30%)
            + LEAST(0.30, bm25_score * 0.60)
            -- Outline boost (log2 scaling, cap +40%)
            + LEAST(0.40, 0.20 * LN(outline_mentions + 1) / LN(2))
            -- Body boost (log2 scaling, cap +20%)
            + LEAST(0.20, 0.05 * LN(body_mentions + 1) / LN(2))
        )
        -- De-rank penalty
        * (CASE WHEN deranked THEN derank_factor ELSE 1.0 END)
    , 3) AS score
FROM features
ORDER BY score DESC;

-- Fetch raw object candidates from selected documents for second-pass object search.
-- Does NOT compute final scores - just retrieves object metadata with cheap features.
-- The C# JitObjectSearchService handles scoring, JIT embedding planning, and final ranking.
--
-- Parameters:
--   doc_uris          - Array of document URIs to fetch objects from
--   keywords          - Keywords for name matching and boost regex derivation
--   boost_pattern     - Optional regex for boosting (derived from keywords if not provided)
--   max_per_doc       - Maximum objects per document
--
-- Returns object candidates with:
--   - Basic metadata (uri, kind, symbol, headline, structure, line range)
--   - name_hit_score: How well the object name matches query keywords
--   - regex_mentions: Count of boost pattern matches in headline+structure

CREATE OR REPLACE MACRO hybrid_object_candidates(
    doc_uris,
    keywords := '',
    boost_pattern := NULL,
    max_per_doc := 50
) AS TABLE
WITH
params AS (
    SELECT
        trim(coalesce(keywords, '')) AS kw,
        NULLIF(trim(coalesce(boost_pattern, '')), '') AS boost_in
),

cfg AS (
    SELECT
        kw,
        CAST(COALESCE(
            boost_in,
            CASE WHEN kw LIKE '%?' THEN NULL
                 ELSE regexp_replace(kw, '\s+', '|', 'g')
            END
        ) AS VARCHAR) AS boost_re,
        lower(kw) AS kw_lower
    FROM params
),

-- Get objects from documents and apply per-doc limit via QUALIFY
candidates AS (
    SELECT
        ri.doc_id,
        ri.node_id,
        ri.uri,
        split_part(ri.uri, '#', 1) AS document_uri,
        ri.kind,
        ri.symbol,
        ri.headline,
        ri.structure,
        ri.line_start,
        ri.line_end,
        ri.lang,
        ri.mime AS semantic_type,
        -- Name hit score: exact match = 1.0, substring = 0.5-0.8
        CASE
            WHEN cfg.kw_lower <> '' AND ri.symbol_key = cfg.kw_lower THEN 1.0
            WHEN cfg.kw_lower <> '' AND position(cfg.kw_lower IN ri.symbol_key) > 0 THEN 0.8
            WHEN cfg.kw_lower <> '' AND position(cfg.kw_lower IN lower(coalesce(ri.headline, ''))) > 0 THEN 0.5
            ELSE 0.0
        END AS name_hit_score,
        -- Regex mentions in outline (headline + structure)
        CASE
            WHEN length(cfg.boost_re) > 0
            THEN COALESCE(array_length(regexp_extract_all(
                coalesce(ri.headline, '') || ' ' || coalesce(ri.structure, ''),
                '(?i)' || cfg.boost_re
            )), 0)
            ELSE 0
        END AS regex_mentions
    FROM repo_index ri
    CROSS JOIN cfg
    WHERE ri.scope = 'object'
      AND split_part(ri.uri, '#', 1) = ANY(doc_uris)
    QUALIFY ROW_NUMBER() OVER (PARTITION BY ri.doc_id ORDER BY ri.line_start NULLS LAST, ri.node_id) <= max_per_doc
)

SELECT
    node_id,
    uri,
    document_uri,
    kind,
    symbol,
    headline,
    structure,
    line_start,
    line_end,
    lang,
    semantic_type,
    name_hit_score,
    regex_mentions
FROM candidates
ORDER BY document_uri, line_start NULLS LAST, node_id;
