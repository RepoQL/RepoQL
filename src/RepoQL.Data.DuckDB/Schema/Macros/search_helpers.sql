-- Search helper macros: small, composable utilities for scoring.
-- These are used by _search_lexical, _search_semantic, and _search_combine.

-- Normalize a value to [0,1] range relative to the max in the window.
-- Returns 0 if max is NULL or 0, otherwise x/max.
CREATE OR REPLACE MACRO zero_one(x) AS (
    CASE
        WHEN MAX(x) OVER () IS NULL OR MAX(x) OVER () = 0 THEN 0
        ELSE COALESCE(x, 0) / NULLIF(MAX(x) OVER (), 0)
    END
);

-- Combine normalized scores with configurable weights.
-- Default weights: semantic 0.70, BM25 0.15, fuzzy 0.15
CREATE OR REPLACE MACRO combine(bm25n, fuzzn, semn, wb := 0.15, wf := 0.15, ws := 0.70) AS (
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
CREATE OR REPLACE MACRO score_confidence(score) AS (
    CASE
        WHEN score >= 2.0 THEN 0.95
        WHEN score >= 1.2 THEN 0.80
        WHEN score >= 0.8 THEN 0.65
        ELSE 0.40
    END
);

-- Cubed semantic score with relative boost.
-- Aggressively penalizes weak matches: 0.7 -> 0.39, 0.5 -> 0.14, 0.3 -> 0.03
CREATE OR REPLACE MACRO sem_cubed_boost(sem) AS (
    POWER(GREATEST(sem, 0), 3) * (0.85 + 0.3 * GREATEST(sem, 0) / NULLIF(MAX(sem) OVER (), 0))
);
