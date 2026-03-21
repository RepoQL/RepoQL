-- Search helper macros: small, composable utilities for scoring.
-- These are used by _search_lexical, _search_semantic, and _search_combine.

-- Dimension-safe cosine similarity. Returns NULL instead of throwing when
-- vector dimensions don't match. Required because DuckDB's vectorized engine
-- may evaluate list_cosine_similarity on rows before WHERE/JOIN filters apply,
-- causing crashes when the index contains mixed-dimension embeddings.
CREATE OR REPLACE MACRO safe_cosine(a, b) AS (
    IF(a IS NOT NULL AND b IS NOT NULL AND array_length(a) = array_length(b),
       list_cosine_similarity(a, b),
       NULL)
);

-- Normalize a value to [0,1] range relative to the max in the window.
-- Returns 0 if max is NULL or 0, otherwise x/max.
CREATE OR REPLACE MACRO zero_one(x) AS (
    CASE
        WHEN MAX(x) OVER () IS NULL OR MAX(x) OVER () = 0 THEN 0
        ELSE COALESCE(x, 0) / NULLIF(MAX(x) OVER (), 0)
    END
);

-- Combine normalized scores via weighted blend. All inputs [0,1], output [0,1].
-- Weights default to 0.30/0.15/0.55 matching the C# Combine() in SearchPipelineUdf.
CREATE OR REPLACE MACRO combine(bm25n, fuzzn, semn, wb := 0.30, wf := 0.15, ws := 0.55) AS (
    COALESCE(wb * bm25n, 0) + COALESCE(wf * fuzzn, 0) + COALESCE(ws * semn, 0)
);

-- Classify a query to determine the best search route.
-- Returns: 'symbol', 'error', 'heavy', 'empty', or 'auto'
CREATE OR REPLACE MACRO _search_classify_query(q) AS (
    CASE
        WHEN q IS NULL OR TRIM(q) = '' THEN 'empty'
        WHEN q LIKE '%::%' OR q LIKE '%.%' OR q LIKE '%()%' THEN 'symbol'
        WHEN regexp_matches(LOWER(q), '([a-z0-9_]+\.){2,}[a-z0-9_]+') THEN 'symbol'
        WHEN LOWER(q) LIKE '% exception%' OR LOWER(q) LIKE '% at %:%' THEN 'error'
        WHEN LENGTH(q) > 160 THEN 'heavy'
        ELSE 'auto'
    END
);

-- Calculate RRF (Reciprocal Rank Fusion) score from a rank.
-- k=60 is standard constant for RRF.
CREATE OR REPLACE MACRO rrf_score(rank, k := 60) AS (
    1.0 / (k + rank)
);

-- Confidence bucket based on combined score.
-- Confidence = score clamped to [0,1]. Display layer (ConfidenceNormalizer)
-- handles the sigmoid mapping to percentages.
CREATE OR REPLACE MACRO score_confidence(score) AS (
    LEAST(GREATEST(COALESCE(score, 0), 0), 1.0)
);

-- Per-model noise floor: cosine scores below this are random similarity.
-- 384 = ONNX E5-small (high raw scores), 1024 = Voyage contextual (low raw scores).
CREATE OR REPLACE MACRO _sem_floor(query_dim) AS (
    CASE
        WHEN query_dim = 384 THEN 0.30
        WHEN query_dim = 768 THEN 0.20
        WHEN query_dim = 1024 THEN 0.03
        ELSE 0.10
    END
);

-- Per-model score range (ceiling - floor).
CREATE OR REPLACE MACRO _sem_range(query_dim) AS (
    CASE
        WHEN query_dim = 384 THEN 0.60   -- [0.30, 0.90]
        WHEN query_dim = 768 THEN 0.60   -- [0.20, 0.80]
        WHEN query_dim = 1024 THEN 0.42  -- [0.03, 0.45]
        ELSE 0.50                         -- [0.10, 0.60]
    END
);

-- Calibrate raw cosine similarity to [0,1] based on model score distribution.
-- Replaces sem_cubed_boost which crushed scores from models with low raw ranges.
CREATE OR REPLACE MACRO sem_calibrate(sem, query_dim) AS (
    LEAST(GREATEST(
        (COALESCE(sem, 0) - _sem_floor(query_dim)) / NULLIF(_sem_range(query_dim), 0),
    0), 1.0)
);

-- Minimum score separation between the top semantic result and the p90 tail
-- before we trust semantic strongly for this query.
CREATE OR REPLACE MACRO _sem_contrast_gap(query_dim) AS (
    CASE
        WHEN query_dim = 384 THEN 0.12
        WHEN query_dim = 768 THEN 0.15
        WHEN query_dim = 1024 THEN 0.18
        ELSE 0.15
    END
);

-- Query-level semantic confidence derived from score contrast.
-- Flat result sets (noise queries) get suppressed; well-separated tops keep full weight.
-- For tiny result sets, skip the gate because percentile statistics are not stable.
CREATE OR REPLACE MACRO sem_query_confidence(top_sem, p90_sem, candidate_count, query_dim) AS (
    CASE
        WHEN COALESCE(candidate_count, 0) < 5 THEN 1.0
        ELSE LEAST(GREATEST(
            (COALESCE(top_sem, 0) - COALESCE(p90_sem, COALESCE(top_sem, 0))) / NULLIF(_sem_contrast_gap(query_dim), 0),
        0), 1.0)
    END
);
