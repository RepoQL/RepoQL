# BM25 Parameter Tuning for Code Search

> Optimize k1 and b parameters based on probabilistic retrieval theory

## Problem

RepoQL uses DuckDB's FTS extension which implements BM25, but with **default parameters** that were tuned for general text (web pages, news articles). Code has different characteristics:

- **Shorter documents** (functions vs articles)
- **Repetitive structure** (boilerplate, patterns)
- **Identifier-heavy** (camelCase tokens are terms)
- **High term frequency variance** (some tokens appear many times)

Default BM25 parameters may not be optimal.

## BM25 Formula Review

```
BM25(d, q) = Σ IDF(t) * (tf(t,d) * (k1 + 1)) / (tf(t,d) + k1 * (1 - b + b * |d|/avgdl))
           t∈q

Where:
  tf(t,d)  = term frequency of t in document d
  |d|      = document length
  avgdl    = average document length
  k1       = term frequency saturation parameter
  b        = length normalization parameter
```

## Parameters Explained

### k1: Term Frequency Saturation

Controls how quickly additional occurrences of a term stop mattering.

```
tf contribution = tf * (k1 + 1) / (tf + k1)

As tf → ∞, contribution → (k1 + 1)
```

| k1 Value | Behavior | Good For |
|----------|----------|----------|
| 0 | Binary (term present or not) | Very short docs |
| 0.5-0.8 | Rapid saturation | Code (identifiers matter, repetition doesn't) |
| 1.2 (default) | Standard saturation | General text |
| 2.0+ | Slow saturation | Long documents where frequency matters |

**For code**: Lower k1 (0.5-0.8) because:
- Seeing `userId` 5 times vs 1 time isn't 5x more relevant
- Repetitive patterns inflate tf artificially

### b: Length Normalization

Controls how much document length affects scoring.

```
Length factor = (1 - b + b * |d|/avgdl)

b=0: No length normalization
b=1: Full normalization (divide by length ratio)
```

| b Value | Behavior | Good For |
|---------|----------|----------|
| 0 | Ignore length | When length is meaningful signal |
| 0.3-0.5 | Mild normalization | Code (longer files often more comprehensive) |
| 0.75 (default) | Standard normalization | General text |
| 1.0 | Strong normalization | When length is noise |

**For code**: Lower b (0.3-0.5) because:
- A 500-line file covering auth thoroughly IS more relevant than a 50-line helper
- Length correlates with comprehensiveness

## Recommended Parameters for Code

| Parameter | Default | Code Search | Rationale |
|-----------|---------|-------------|-----------|
| k1 | 1.2 | **0.75** | Faster saturation for repetitive patterns |
| b | 0.75 | **0.4** | Less length penalty for comprehensive files |

## Implementation

### DuckDB FTS Configuration

```sql
-- Create FTS index with custom parameters
PRAGMA create_fts_index(
    'artifact',           -- table
    'artifact_id',        -- id column
    'content',            -- text column
    stemmer := 'none',    -- no stemming for code
    stopwords := '',      -- keep all tokens
    ignore := '',
    strip_accents := false,
    lower := false,       -- case-sensitive for identifiers
    overwrite := true
);

-- BM25 parameters in query (if supported)
-- Note: DuckDB FTS may require extension modification for custom k1/b
```

### Custom BM25 Macro (If Extension Doesn't Support)

```sql
-- Custom BM25 implementation with tunable parameters
CREATE MACRO bm25_score(
    tf,           -- term frequency in doc
    df,           -- document frequency
    doc_len,      -- document length
    avg_doc_len,  -- average doc length
    total_docs,   -- total document count
    k1 := 0.75,   -- saturation (lower for code)
    b := 0.4      -- length norm (lower for code)
) AS (
    -- IDF component
    ln((total_docs - df + 0.5) / (df + 0.5) + 1)
    *
    -- TF component with saturation and length normalization
    (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * doc_len / avg_doc_len))
);

-- Usage in search
WITH term_stats AS (
    SELECT
        term,
        COUNT(DISTINCT artifact_id) as df
    FROM inverted_index
    WHERE term IN ('auth', 'validate')  -- query terms
    GROUP BY term
),
doc_stats AS (
    SELECT
        AVG(length) as avg_len,
        COUNT(*) as total_docs
    FROM artifact
)
SELECT
    a.uri,
    SUM(bm25_score(
        ii.tf,
        ts.df,
        a.length,
        ds.avg_len,
        ds.total_docs,
        k1 := 0.75,
        b := 0.4
    )) as score
FROM artifact a
JOIN inverted_index ii ON a.artifact_id = ii.artifact_id
JOIN term_stats ts ON ii.term = ts.term
CROSS JOIN doc_stats ds
GROUP BY a.uri
ORDER BY score DESC;
```

## Field-Weighted BM25 (BM25F)

For code, different fields have different importance:

| Field | Weight | Rationale |
|-------|--------|-----------|
| `symbol_name` | 3.0 | Exact name matches are gold |
| `docstring` | 2.0 | Documentation describes purpose |
| `body` | 1.0 | Implementation details |
| `comments` | 0.5 | Often noise |
| `path` | 1.5 | Directory structure is meaningful |

```sql
-- BM25F: weighted combination of field scores
CREATE MACRO bm25f_score(
    symbol_score,
    docstring_score,
    body_score,
    comment_score,
    path_score
) AS (
    3.0 * symbol_score
    + 2.0 * docstring_score
    + 1.0 * body_score
    + 0.5 * comment_score
    + 1.5 * path_score
);
```

## Validation Strategy

### A/B Testing Approach

```sql
-- Compare ranking quality with different parameters
WITH rankings_default AS (
    SELECT uri, row_number() OVER (ORDER BY bm25_score(..., k1:=1.2, b:=0.75)) as rank
    FROM search_results
),
rankings_tuned AS (
    SELECT uri, row_number() OVER (ORDER BY bm25_score(..., k1:=0.75, b:=0.4)) as rank
    FROM search_results
)
SELECT
    d.uri,
    d.rank as default_rank,
    t.rank as tuned_rank,
    t.rank - d.rank as rank_change
FROM rankings_default d
JOIN rankings_tuned t USING (uri)
ORDER BY ABS(rank_change) DESC;
```

### Metrics to Track

| Metric | Definition | Target |
|--------|------------|--------|
| MRR | 1/rank of first relevant result | Higher is better |
| P@5 | Precision in top 5 | Higher is better |
| Recall@10 | Relevant docs in top 10 | Higher is better |

## Expected Impact

| Scenario | Default | Tuned | Why |
|----------|---------|-------|-----|
| Query: "userId" in large service file | Rank 5 | Rank 2 | Less length penalty |
| Query: "validate" in repetitive test file | Rank 3 | Rank 7 | Faster saturation hurts inflated tf |
| Query: "AuthService" exact match | Rank 1 | Rank 1 | Both handle well |

Net effect: Better ranking for comprehensive implementation files, reduced noise from repetitive boilerplate.

## Open Questions

1. Per-language parameter tuning? (Java verbosity vs Go conciseness)
2. Dynamic k1 based on query type (identifier vs concept)?
3. Integrate with DuckDB FTS extension or maintain separate scoring?

## References

- [ProbabilisticRetrieval.md](../research/algorithms/ProbabilisticRetrieval.md) - Full BM25 derivation
- Robertson & Zaragoza (2009) - The Probabilistic Relevance Framework: BM25 and Beyond
- Trotman et al. (2014) - Improvements to BM25 and Language Models
