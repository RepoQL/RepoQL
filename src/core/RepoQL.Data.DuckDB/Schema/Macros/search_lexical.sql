-- Lexical search module: document-first scoring.
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
        LOWER(COALESCE(repository_uri_file_name(n.uri), '')) AS basename_lc,
        LOWER(COALESCE(
            COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, '')),
            ''
        )) AS headline_lc,
        LOWER(COALESCE(
            COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, '')),
            ''
        )) AS structure_lc
    FROM _lex_doc_scope sf
    JOIN node n ON n.id = sf.node_id
    LEFT JOIN artifact a ON a.id = n.artifact_id
),

-- Tokenize query into lowercase terms. Drop empty strings and 1-char noise tokens.
-- Defined before grep_lines so per-term grep can reference it.
query_terms AS (
    SELECT TRIM(qt.term_raw) AS term
    FROM params p
    CROSS JOIN UNNEST(string_split(p.keywords_lc, ' ')) AS qt(term_raw)
    WHERE p.keywords_empty = FALSE
      AND LENGTH(TRIM(qt.term_raw)) > 1
),

term_count AS (
    SELECT COUNT(*) AS n
    FROM query_terms
),

-- Single-pass multi-term grep: reads each file once, checks all terms per line.
grep_lines AS (
    SELECT DISTINCT n.id AS doc_id, g.line_number, g.term
    FROM params p, grep_terms(p.keywords_lc, '**', 1000) g
    JOIN node n ON n.uri = g.uri AND n.kind = 'document'
    WHERE p.keywords_empty = FALSE
),

-- Per-document, per-term coverage on in-memory fields.
doc_term_hits AS (
    SELECT
        d.node_id,
        d.doc_id,
        t.term,
        CASE WHEN position(t.term IN d.basename_lc) > 0 THEN 1 ELSE 0 END AS bn_hit,
        CASE WHEN d.basename_lc = t.term
              OR regexp_replace(d.basename_lc, '\.[^.]*$', '') = t.term THEN 1 ELSE 0 END AS bn_exact,
        CASE WHEN position(t.term IN d.headline_lc) > 0 THEN 1 ELSE 0 END AS hl_hit,
        CASE WHEN position(t.term IN d.structure_lc) > 0 THEN 1 ELSE 0 END AS st_hit,
        CASE WHEN position(t.term IN d.search_key) > 0 THEN 1 ELSE 0 END AS pk_hit,
        TRY_CAST(match_score(t.term, d.basename_lc) AS DOUBLE) AS bn_fuzz
    FROM doc_filtered d
    CROSS JOIN query_terms t
),

doc_coverage AS (
    SELECT
        h.node_id,
        h.doc_id,
        SUM(h.bn_hit)::DOUBLE / NULLIF(tc.n, 0) AS basename_coverage,
        MAX(h.bn_exact) AS has_basename_exact,
        SUM(h.hl_hit)::DOUBLE / NULLIF(tc.n, 0) AS headline_coverage,
        SUM(h.st_hit)::DOUBLE / NULLIF(tc.n, 0) AS structure_coverage,
        SUM(h.pk_hit)::DOUBLE / NULLIF(tc.n, 0) AS path_coverage,
        SUM(CASE WHEN h.bn_hit + h.hl_hit + h.st_hit + h.pk_hit > 0 THEN 1 ELSE 0 END)::DOUBLE
            / NULLIF(tc.n, 0) AS any_field_coverage,
        MAX(h.bn_fuzz) AS fuzz
    FROM doc_term_hits h
    CROSS JOIN term_count tc
    GROUP BY h.node_id, h.doc_id, tc.n
),

grep_doc_counts AS (
    SELECT
        doc_id,
        COUNT(*) AS hit_count,
        COUNT(DISTINCT term) AS terms_found
    FROM grep_lines
    GROUP BY doc_id
),

-- Cumulative scoring: independent lexical signals add evidence instead of collapsing into score buckets.
-- Sources from doc_coverage (not doc_filtered) to avoid MultiRefCTE double-evaluation trap.
-- When query_terms is empty (all terms filtered by LENGTH>1), doc_coverage is empty → no results.
doc_scored AS (
    SELECT
        c.node_id,
        c.doc_id,
        (
            CASE
                WHEN c.has_basename_exact = 1 THEN 3.0
                WHEN c.basename_coverage > 0 THEN 1.5 * c.basename_coverage
                ELSE 0
            END
            + CASE
                WHEN COALESCE(g.hit_count, 0) > 0
                    THEN 2.0 * (g.terms_found::DOUBLE / NULLIF((SELECT n FROM term_count), 0))
                       + 0.3 * LN(1 + g.hit_count)
                ELSE 0
            END
            + 1.5 * c.headline_coverage
            + 1.0 * c.structure_coverage
            + 0.5 * c.path_coverage
        ) AS bm25_score,
        c.fuzz AS fuzz
    FROM doc_coverage c
    LEFT JOIN grep_doc_counts g ON g.doc_id = c.doc_id
),

-- Rank, cap, normalize. Single ROW_NUMBER pass (previously duplicated).
ranked AS (
    SELECT
        node_id,
        doc_id,
        bm25_score,
        fuzz,
        ROW_NUMBER() OVER (
            ORDER BY bm25_score DESC, COALESCE(fuzz, 0) DESC, node_id
        ) AS lex_rank
    FROM doc_scored
    QUALIFY lex_rank <= (SELECT limit_cand FROM params)
),

normalized AS (
    SELECT
        node_id,
        doc_id,
        bm25_score,
        fuzz,
        lex_rank,
        zero_one(bm25_score) AS bm25_norm,
        zero_one(fuzz) AS fuzz_norm,
        rrf_score(lex_rank) AS rrf_lex
    FROM ranked
)

SELECT
    node_id,
    doc_id,
    bm25_score,
    fuzz AS fuzzy_score,
    bm25_norm,
    fuzz_norm,
    lex_rank,
    rrf_lex
FROM normalized
);
