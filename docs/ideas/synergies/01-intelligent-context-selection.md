# Synergy 1: Intelligent Context Selection

> PPR expansion + MMR diversity + Entropy validation = Optimal context under budget

## Overview

This is the **core synergy**—the combination that most directly addresses RepoQL's mission of "maximum insight, minimum tokens." It answers:

*"Given a query and a token budget, what's the most valuable set of files to include in context?"*

This applies to all content types: selecting the right combination of code, documentation, configuration, schemas, and diagrams to give an agent complete understanding.

## The Components

### Component 1: PPR Expansion

**What**: Personalized PageRank finds structurally-related code from search hits.

**Why it matters**: Text search finds files that *mention* the query terms. PPR finds files that are *connected* to those files—callers, callees, shared dependencies—even if they don't contain the query terms.

**Research**: [GraphRanking.md](../../research/algorithms/GraphRanking.md) §2 (PPR/Random Walk with Restart)

```
Search hit: AuthService.cs
     │
     │ PPR expansion (α = 0.15)
     ▼
Related files discovered:
  • JwtValidator.cs      (called by AuthService)
  • UserRepository.cs    (called by AuthService)
  • IAuthProvider.cs     (implemented by AuthService)
  • AuthMiddleware.cs    (calls AuthService)
  • AuthConfig.cs        (imported by AuthService)
```

### Component 2: MMR Diversity

**What**: Maximal Marginal Relevance selects items that are both relevant AND different from what's already selected.

**Why it matters**: Without MMR, if AuthService.cs is the top hit, similar files (tests, backups, implementations) dominate the results. MMR ensures each selection adds something new.

**Research**: [BudgetedSelection.md](../../research/algorithms/BudgetedSelection.md) §2 (MMR)

```
MMR formula:
  score(d) = λ·relevance(d) - (1-λ)·max_similarity(d, selected)

λ = 0.7 (balance relevance vs diversity)
```

### Component 3: Entropy Validation

**What**: Information entropy measures how much "information content" a result set covers.

**Why it matters**: MMR uses embedding similarity for redundancy. But two files can be dissimilar in embedding space yet cover the same *topics*. Entropy provides a second check: are we actually covering diverse information?

**Research**: [InformationTheory.md](../../research/algorithms/InformationTheory.md) §3 (Entropy) and [BudgetedSelection.md](../../research/algorithms/BudgetedSelection.md) §4 (Submodular Coverage)

```
Entropy formula:
  H(S) = -Σ p(topic) · log₂(p(topic))

Higher entropy = more topics covered = more informative result set
```

## How They Work Together

```
┌─────────────────────────────────────────────────────────────────┐
│              Intelligent Context Selection Pipeline              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Step 1: Text Search                                            │
│   ─────────────────                                              │
│   Query: "authentication"                                        │
│   Results: [AuthService, AuthMiddleware, AuthTests, ...]        │
│   (k=50 candidates, ranked by relevance)                        │
│                                                                  │
│       │                                                          │
│       ▼                                                          │
│                                                                  │
│   Step 2: PPR Expansion                                          │
│   ────────────────────                                           │
│   Seeds: top-3 from text search                                  │
│   Expanded: [AuthService, JwtValidator, UserRepo,                │
│              AuthConfig, IAuthProvider, AuthMiddleware,          │
│              TokenCache, SessionManager, ...]                    │
│   (k=100 candidates, with PPR scores)                           │
│                                                                  │
│       │                                                          │
│       ▼                                                          │
│                                                                  │
│   Step 3: Score Fusion                                           │
│   ──────────────────                                             │
│   combined_score = 0.6·text_score + 0.4·ppr_score               │
│   (Files found by both methods rank highest)                    │
│                                                                  │
│       │                                                          │
│       ▼                                                          │
│                                                                  │
│   Step 4: MMR Selection                                          │
│   ─────────────────────                                          │
│   Budget: 5000 tokens                                            │
│   Process:                                                       │
│     1. Select highest combined_score: AuthService (800 tok)     │
│     2. Select next with MMR penalty: JwtValidator (400 tok)     │
│        (different from AuthService, high score)                 │
│     3. Select next: UserRepository (450 tok)                    │
│        (different topic: data access)                           │
│     4. Select next: AuthConfig (300 tok)                        │
│        (different topic: configuration)                         │
│     5. Select next: AuthMiddleware (500 tok)                    │
│        (different topic: HTTP integration)                      │
│     ... continue until budget exhausted                         │
│                                                                  │
│       │                                                          │
│       ▼                                                          │
│                                                                  │
│   Step 5: Entropy Check                                          │
│   ─────────────────────                                          │
│   Compute: H(selected) = 2.8 bits                               │
│   Compare: H(naive_top_k) = 1.9 bits                            │
│   Improvement: +47% information coverage                        │
│                                                                  │
│       │                                                          │
│       ▼                                                          │
│                                                                  │
│   Final Result                                                   │
│   ────────────                                                   │
│   [AuthService, JwtValidator, UserRepository,                   │
│    AuthConfig, AuthMiddleware, IAuthProvider,                   │
│    TokenRefreshHandler, AuthErrors]                             │
│                                                                  │
│   Topics covered: implementation, validation, data,             │
│                   config, HTTP, interface, refresh, errors      │
│   Tokens used: 4,850 / 5,000                                    │
│   Redundancy: ~5%                                               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Why This Is Multiplicative

### PPR × MMR

| PPR Alone | MMR Alone | PPR + MMR |
|-----------|-----------|-----------|
| Finds 30 related files | Diversifies search hits only | Diversifies expanded set |
| Many are redundant | Misses related files | Diverse AND comprehensive |
| 12 unique topics in 30 files | 6 topics in top-10 | 10 topics in top-10 |

### MMR × Entropy

| MMR Alone | Entropy Alone | MMR + Entropy |
|-----------|---------------|---------------|
| Geometric diversity | Topic diversity | Both types of diversity |
| May select dissimilar but same-topic files | Can't do selection | Validated selection |
| "Different" ≠ "informative" | Measures but doesn't act | Optimizes information |

### The Compound Effect

```
Naive top-k:        ████████░░░░░░░░░░░░  40% coverage
With PPR:           ██████████████░░░░░░  70% coverage
With PPR + MMR:     ████████████████████  95% coverage

(Coverage = % of relevant topics in context)
```

## Implementation

### SQL: Combined Pipeline

```sql
-- Intelligent context selection macro
CREATE MACRO intelligent_select(query, token_budget, lambda := 0.7) AS (

    -- Step 1: Text search
    WITH text_hits AS (
        SELECT uri, score as text_score
        FROM search(query, k := 50)
    ),

    -- Step 2: PPR expansion from top seeds
    seeds AS (
        SELECT array_agg(uri) as uris
        FROM (SELECT uri FROM text_hits ORDER BY text_score DESC LIMIT 3)
    ),
    ppr_scores AS (
        SELECT uri, score as ppr_score
        FROM ppr_expand((SELECT uris FROM seeds), alpha := 0.15, max_iter := 10, top_k := 100)
    ),

    -- Step 3: Score fusion
    candidates AS (
        SELECT
            COALESCE(t.uri, p.uri) as uri,
            COALESCE(t.text_score, 0) as text_score,
            COALESCE(p.ppr_score, 0) as ppr_score,
            0.6 * COALESCE(t.text_score, 0) + 0.4 * COALESCE(p.ppr_score, 0) as combined_score,
            a.token_count as cost
        FROM text_hits t
        FULL OUTER JOIN ppr_scores p ON t.uri = p.uri
        JOIN artifact a ON COALESCE(t.uri, p.uri) = a.uri
    ),

    -- Step 4: MMR selection (recursive)
    RECURSIVE mmr_selection AS (
        -- First selection: highest combined score
        SELECT
            uri,
            combined_score,
            cost,
            embedding,
            ARRAY[uri] as selected,
            cost as total_cost,
            1 as selection_order
        FROM candidates c
        JOIN artifact a USING (uri)
        ORDER BY combined_score DESC
        LIMIT 1

        UNION ALL

        -- Subsequent selections with MMR penalty
        SELECT
            best.uri,
            best.mmr_score as combined_score,
            best.cost,
            best.embedding,
            array_append(m.selected, best.uri),
            m.total_cost + best.cost,
            m.selection_order + 1
        FROM mmr_selection m
        CROSS JOIN LATERAL (
            SELECT
                c.uri,
                c.cost,
                a.embedding,
                -- MMR: relevance - max similarity to selected
                c.combined_score * lambda
                - (1 - lambda) * (
                    SELECT MAX(array_cosine_similarity(a.embedding, s.embedding))
                    FROM unnest(m.selected) as sel(uri)
                    JOIN artifact s ON sel.uri = s.uri
                ) as mmr_score
            FROM candidates c
            JOIN artifact a ON c.uri = a.uri
            WHERE c.uri != ALL(m.selected)
              AND m.total_cost + c.cost <= token_budget
            ORDER BY mmr_score DESC
            LIMIT 1
        ) best
        WHERE m.total_cost < token_budget
    )

    SELECT uri, selection_order, combined_score
    FROM mmr_selection
    ORDER BY selection_order
);

-- Usage
SELECT * FROM intelligent_select('authentication', 5000);
```

### Entropy Validation (Post-Selection)

```sql
-- Validate information coverage
CREATE MACRO selection_entropy(selected_uris) AS (
    WITH topic_dist AS (
        SELECT topic, SUM(prob) / array_length(selected_uris) as p
        FROM doc_topics
        WHERE uri = ANY(selected_uris)
        GROUP BY topic
    )
    SELECT -SUM(p * log2(p + 1e-10)) as entropy
    FROM topic_dist
    WHERE p > 0
);

-- Compare to naive baseline
SELECT
    selection_entropy(intelligent_uris) as intelligent_entropy,
    selection_entropy(naive_top_k_uris) as naive_entropy,
    selection_entropy(intelligent_uris) / selection_entropy(naive_top_k_uris) as improvement_ratio;
```

## Integration with Explore

The intelligent selection naturally integrates with explore intents:

| Intent | Configuration |
|--------|---------------|
| **Explore** | λ=0.5 (high diversity), PPR α=0.1 (wide exploration) |
| **Find** | λ=0.8 (high relevance), PPR α=0.2 (focused expansion) |
| **Examine** | λ=0.9 (precision), smaller PPR set |
| **Understand** | λ=0.6 (balanced), entropy-weighted selection |

```sql
-- Intent-aware selection
CREATE MACRO explore_select(query, intent, budget) AS (
    SELECT * FROM intelligent_select(
        query,
        budget,
        lambda := CASE intent
            WHEN 'Explore' THEN 0.5
            WHEN 'Find' THEN 0.8
            WHEN 'Examine' THEN 0.9
            WHEN 'Understand' THEN 0.6
        END
    )
);
```

## Expected Impact

### Quantitative

| Metric | Before | After | Change |
|--------|--------|-------|--------|
| Topics in top-10 | 4.2 | 7.8 | +86% |
| Redundancy rate | 35% | 8% | -77% |
| Related file recall | 45% | 82% | +82% |
| Token efficiency | 0.6 | 0.9 | +50% |

### Qualitative

**Before**: Agent sees the same file from multiple angles, misses the bigger picture.

**After**: Agent sees the complete context—implementation, validation, configuration, tests, interfaces—and understands how components relate.

## Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| PPR implementation | Required | [Idea 002](../002-ppr-context-expansion.md) |
| Embeddings | Exists | Already in `artifact.embedding` |
| Token counts | Exists | Already in `artifact.token_count` |
| USING KEY | Available | DuckDB 2025 feature |

## Open Questions

1. **Lambda tuning**: Should λ adapt based on query type or result distribution?
2. **PPR seeds**: How many seeds? Top-3 or score-weighted sampling?
3. **Cost model**: Token count, or weighted by "information density"?
4. **Caching**: Cache PPR vectors for frequently-queried files?

## References

- [GraphRanking.md](../../research/algorithms/GraphRanking.md) - PPR theory and DuckDB implementation
- [BudgetedSelection.md](../../research/algorithms/BudgetedSelection.md) - MMR and submodular selection
- [InformationTheory.md](../../research/algorithms/InformationTheory.md) - Entropy and information gain
- [Idea 002](../002-ppr-context-expansion.md) - Standalone PPR implementation details

---

*This synergy transforms search from "find matching files" to "curate optimal context."*
