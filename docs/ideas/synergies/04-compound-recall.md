# Synergy 4: Compound Recall

> BM25 tuning + Query expansion = Better initial retrieval that feeds everything downstream

## Overview

This synergy is the **foundation** for all others. It improves the initial search that provides seeds for PPR, candidates for MMR, and baseline results. Better initial retrieval compounds through the entire pipeline.

**Note**: While examples here often reference code, these techniques apply to *all* content RepoQL indexes—documentation, configs, schemas, diagrams, and more. Query expansion for "k8s" → "kubernetes" helps find deployment YAML just as "auth" → "authentication" helps find code.

The two components are independent but **multiply** when combined:
- BM25 tuning without expansion: Better ranking, same recall
- Expansion without BM25 tuning: More matches, poor ranking
- Both together: More matches AND better ranking

## The Components

### Component 1: BM25 Parameter Tuning

**What**: Adjust k1 and b parameters from defaults (web/news text) to code-optimized values.

**Research**: [ProbabilisticRetrieval.md](../../research/algorithms/ProbabilisticRetrieval.md) §4 (BM25 Derivation)

```
BM25(d, q) = Σ IDF(t) · (tf · (k1 + 1)) / (tf + k1 · (1 - b + b · |d|/avgdl))

Default (web text):  k1 = 1.2,  b = 0.75
Code-optimized:      k1 = 0.75, b = 0.4
```

**Why different for repository content**:

| Characteristic | Web Text | Repository Content | Parameter Impact |
|----------------|----------|-------------------|------------------|
| Repetition | Low | High (patterns, templates, boilerplate) | Lower k1 (faster saturation) |
| Length meaning | Often noise | Often signal (comprehensive docs/files) | Lower b (less length penalty) |
| Term importance | Varies | Identifiers/keywords dominate | Field-specific weights |

Note: Different content types may benefit from different tuning:
- **Code**: Lower k1 (patterns repeat), lower b (long files = comprehensive)
- **Markdown**: Standard k1, moderate b (length correlates with depth)
- **Config/YAML**: Very low k1 (keys repeat), very low b (structure matters, not length)

### Component 2: Query Expansion

**What**: Expand queries with abbreviations, synonyms, and domain-specific variants before search.

**Research**: [QueryExpansion.md](../../research/algorithms/QueryExpansion.md) §7 (Domain-Specific Expansion)

```
Query: "auth config"

Expansion (applies to all content types):
  auth → authentication, authorization, authenticate, OAuth, OIDC
  config → configuration, configure, conf, settings, options

Code variants:
  Auth, AUTH, auth, AuthConfig, auth_config, authConfig
  IAuth*, *Service, *Provider, *Handler

Documentation variants:
  "authentication guide", "auth setup", "login configuration"

DevOps/Config variants:
  k8s → kubernetes
  env → environment
  db → database
  tls → ssl, https, certificate
```

## How They Multiply

### Problem: Neither Alone Is Sufficient

**BM25 tuning without expansion**:
```
Query: "auth"
Finds: Files containing "auth"
Misses: Files containing only "authentication"
Result: High precision, low recall
```

**Expansion without BM25 tuning**:
```
Query: "auth" → expanded to "auth authentication authorization"
Finds: All files mentioning any term
Problem: Long files with "authentication" once rank too high
Result: High recall, low precision
```

### Solution: Both Together

```
Query: "auth"
  ↓
Expansion: "auth authentication authorization authenticate"
  ↓
BM25 with code-tuned parameters:
  - k1=0.75: "authentication" appearing 10x doesn't dominate
  - b=0.4: Comprehensive AuthService.cs not penalized for length
  ↓
Result: High recall AND high precision
```

## Implementation

### BM25 Configuration

```sql
-- If DuckDB FTS supports parameter configuration:
PRAGMA fts_config = '{
    "stemmer": "none",           -- No stemming for code
    "stopwords": "",             -- Keep all tokens
    "lowercase": false,          -- Case-sensitive identifiers
    "k1": 0.75,                  -- Faster saturation
    "b": 0.4                     -- Less length normalization
}';

-- If not, implement custom BM25 scoring:
CREATE MACRO bm25_code(tf, df, doc_len, avg_len, total_docs) AS (
    -- IDF
    ln((total_docs - df + 0.5) / (df + 0.5) + 1)
    *
    -- TF with code-optimized params
    (tf * 1.75) / (tf + 0.75 * (0.6 + 0.4 * doc_len / avg_len))
);
```

### Query Expansion

```sql
-- Abbreviation dictionary
CREATE TABLE abbreviations (
    abbrev VARCHAR PRIMARY KEY,
    expansions VARCHAR[]
);

INSERT INTO abbreviations VALUES
    -- Code abbreviations
    ('auth', ARRAY['authentication', 'authorization', 'authenticate', 'oauth', 'oidc']),
    ('config', ARRAY['configuration', 'configure', 'conf', 'settings', 'options']),
    ('repo', ARRAY['repository']),
    ('impl', ARRAY['implementation', 'implement']),
    ('svc', ARRAY['service']),
    ('ctx', ARRAY['context']),
    ('req', ARRAY['request']),
    ('res', ARRAY['response', 'result']),
    ('db', ARRAY['database']),
    ('util', ARRAY['utility', 'utilities']),
    ('param', ARRAY['parameter']),
    ('init', ARRAY['initialize', 'initialization']),
    ('val', ARRAY['value', 'validate', 'validation']),
    ('err', ARRAY['error']),
    ('msg', ARRAY['message']),
    -- DevOps/Infrastructure abbreviations
    ('k8s', ARRAY['kubernetes']),
    ('env', ARRAY['environment']),
    ('tls', ARRAY['ssl', 'https', 'certificate']),
    ('infra', ARRAY['infrastructure']),
    ('deps', ARRAY['dependencies']),
    ('ci', ARRAY['continuous integration']),
    ('cd', ARRAY['continuous deployment', 'continuous delivery']),
    -- Documentation abbreviations
    ('docs', ARRAY['documentation', 'document']),
    ('spec', ARRAY['specification']),
    ('arch', ARRAY['architecture']),
    ('api', ARRAY['application programming interface', 'endpoint']);

-- Expand query terms
CREATE MACRO expand_query(query) AS (
    WITH tokens AS (
        SELECT unnest(string_split(lower(query), ' ')) as token
    ),
    expanded AS (
        SELECT
            t.token,
            COALESCE(a.expansions, ARRAY[t.token]) as variants
        FROM tokens t
        LEFT JOIN abbreviations a ON t.token = a.abbrev
    ),
    all_terms AS (
        SELECT DISTINCT term
        FROM expanded, unnest(variants) as v(term)
    )
    SELECT string_agg(term, ' ') as expanded_query
    FROM all_terms
);

-- Example:
-- expand_query('auth config') → 'auth authentication authorization authenticate config configuration configure conf'
```

### Combined Search

```sql
-- Search with expansion and tuned BM25
CREATE MACRO search_enhanced(query, k) AS (
    WITH
    -- Expand query
    expanded AS (
        SELECT expand_query(query) as exp_query
    ),

    -- Search with original (high weight)
    original_results AS (
        SELECT uri, score * 1.0 as weighted_score, 'original' as source
        FROM search(query, k := k * 2)
    ),

    -- Search with expanded (lower weight)
    expanded_results AS (
        SELECT uri, score * 0.6 as weighted_score, 'expanded' as source
        FROM search((SELECT exp_query FROM expanded), k := k * 2)
    ),

    -- Combine with RRF
    combined AS (
        SELECT uri, source, row_number() OVER (PARTITION BY source ORDER BY weighted_score DESC) as rank
        FROM (
            SELECT * FROM original_results
            UNION ALL
            SELECT * FROM expanded_results
        )
    )

    SELECT
        uri,
        SUM(1.0 / (60 + rank)) as rrf_score
    FROM combined
    GROUP BY uri
    ORDER BY rrf_score DESC
    LIMIT k
);
```

## Synergy with Other Components

### Compound Recall × PPR

Better initial hits = better PPR seeds:

```
Without enhancement:
  Query: "auth"
  Top hits: [AuthService, AuthTests, AuthBackup]
  PPR seeds: Mostly redundant, biased expansion

With enhancement:
  Query: "auth"
  Top hits: [AuthService, AuthenticationMiddleware, AuthorizationPolicy]
  PPR seeds: Diverse, comprehensive expansion
```

### Compound Recall × MMR

More candidates = better diversity potential:

```
Without enhancement:
  Candidates: 20 files
  After MMR: 10 files from limited pool

With enhancement:
  Candidates: 45 files (expansion found more)
  After MMR: 10 files from rich pool (better diversity)
```

### Compound Recall × SimHash

More true matches = fewer relative duplicates:

```
Without enhancement:
  Results: 5 files, 3 are clones of 2 unique
  Effective: 2 unique files

With enhancement:
  Results: 12 files, 3 are clones
  After dedup: 9 unique files
```

## Field-Weighted BM25 (BM25F)

For even better results, weight different code fields:

```sql
-- BM25F: different weights for different fields
CREATE TABLE field_weights (
    field VARCHAR PRIMARY KEY,
    weight DOUBLE
);

INSERT INTO field_weights VALUES
    ('symbol_name', 3.0),    -- Function/class names are gold
    ('docstring', 2.0),      -- Documentation describes purpose
    ('path', 1.5),           -- File path is meaningful
    ('body', 1.0),           -- Code body is baseline
    ('comments', 0.5);       -- Comments are often noise

-- Multi-field search
CREATE MACRO search_bm25f(query, k) AS (
    WITH field_scores AS (
        SELECT
            uri,
            SUM(
                fw.weight * bm25_code(tf, df, doc_len, avg_len, total_docs)
            ) as score
        FROM term_occurrences t
        JOIN field_weights fw ON t.field = fw.field
        JOIN corpus_stats cs
        WHERE t.term IN (SELECT unnest(string_split(expand_query(query), ' ')))
        GROUP BY uri
    )
    SELECT uri, score
    FROM field_scores
    ORDER BY score DESC
    LIMIT k
);
```

## Expected Impact

### Quantitative

| Metric | Default BM25 | Tuned Only | Expansion Only | Both |
|--------|--------------|------------|----------------|------|
| Recall@20 | 0.55 | 0.60 | 0.75 | **0.85** |
| MRR | 0.45 | 0.52 | 0.42 | **0.58** |
| Zero-result rate | 8% | 7% | 3% | **<1%** |

**The multiplication**: Tuned BM25 gives +9% recall. Expansion gives +36% recall. Together: +55% recall (not additive—synergistic).

### Example Queries

| Query | Default | Tuned + Expanded |
|-------|---------|------------------|
| "auth" | 3 relevant in top-5 | 5 relevant in top-5 |
| "db conn" | 1 relevant (misses "database connection") | 4 relevant |
| "validate user input" | 2 relevant | 5 relevant |
| "svc init" | 0 results | 6 relevant |

## Implementation Priority

This synergy should be **implemented first** because:

1. **No dependencies**: Works with existing search
2. **Low complexity**: Dictionary + parameter change
3. **Immediate impact**: Every search gets better
4. **Compounds downstream**: Better seeds for PPR, MMR, everything

```
Effort:     ████░░░░░░  Low
Impact:     ████████░░  High
Risk:       ██░░░░░░░░  Low
Priority:   1 (First)
```

## Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| DuckDB FTS | Exists | May need custom scoring |
| Abbreviation dict | Simple | Add table + populate |
| RRF fusion | Simple | Standard algorithm |

## Open Questions

1. **Abbreviation coverage**: How to discover domain-specific abbreviations?
2. **Per-language tuning**: Different k1/b for Java (verbose) vs Go (concise)?
3. **Dynamic weights**: Learn field weights from user behavior?
4. **Query classification**: Detect when NOT to expand (exact identifier queries)?

## References

- [ProbabilisticRetrieval.md](../../research/algorithms/ProbabilisticRetrieval.md) - BM25 theory
- [QueryExpansion.md](../../research/algorithms/QueryExpansion.md) - Expansion techniques
- [HybridRetrieval.md](../../research/algorithms/HybridRetrieval.md) - RRF fusion
- [Idea 003](../003-code-query-expansion.md) - Query expansion details
- [Idea 007](../007-bm25-parameter-tuning.md) - BM25 tuning details

---

*This synergy is the foundation—get it right and everything downstream improves.*
