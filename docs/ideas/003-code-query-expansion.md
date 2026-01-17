# Code-Specific Query Expansion

> Expand queries with code-aware transformations before search

## Problem

Developer queries often don't match code vocabulary:

| Query | Actual Identifier | Why It Fails |
|-------|-------------------|--------------|
| "auth" | `AuthenticationService` | Abbreviation |
| "get user" | `fetchUserById` | Synonym + naming convention |
| "config" | `IConfigurationProvider` | Interface prefix, full word |
| "db connection" | `SqlConnectionPool` | Domain term mismatch |

Current search relies on embedding similarity to bridge these gaps, but dense retrieval can miss exact identifier matches that BM25 would catch with the right terms.

## Proposed Solution

Lightweight, rule-based query expansion specifically for code:

```
┌─────────────────────────────────────────────────────────────────┐
│              Code Query Expansion Pipeline                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "auth config"                                           │
│       │                                                          │
│       ├──▶ Abbreviation expansion                                │
│       │    → authentication, authorization, configure            │
│       │                                                          │
│       ├──▶ Casing variants                                       │
│       │    → Auth, AUTH, auth, AuthConfig, auth_config           │
│       │                                                          │
│       ├──▶ Common patterns                                       │
│       │    → IAuth*, *Service, *Provider, *Handler               │
│       │                                                          │
│       └──▶ Compound terms                                        │
│            → AuthenticationConfiguration, AuthConfig             │
│                                                                  │
│   Expanded: original + expansions → Multi-query search           │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Implementation

### Abbreviation Dictionary

```sql
CREATE TABLE code_abbreviations (
    abbrev VARCHAR PRIMARY KEY,
    expansions VARCHAR[]  -- Multiple possible expansions
);

INSERT INTO code_abbreviations VALUES
    ('auth', ['authentication', 'authorization', 'authenticate']),
    ('config', ['configuration', 'configure', 'conf']),
    ('repo', ['repository']),
    ('impl', ['implementation', 'implement']),
    ('util', ['utility', 'utilities']),
    ('db', ['database']),
    ('msg', ['message']),
    ('ctx', ['context']),
    ('req', ['request']),
    ('res', ['response', 'result']),
    ('err', ['error']),
    ('cb', ['callback']),
    ('fn', ['function']),
    ('param', ['parameter']),
    ('args', ['arguments']),
    ('init', ['initialize', 'initialization']),
    ('info', ['information']),
    ('mgr', ['manager']),
    ('svc', ['service']),
    ('proc', ['process', 'processor']),
    ('val', ['value', 'validate', 'validation']),
    ('str', ['string']),
    ('num', ['number']),
    ('idx', ['index']),
    ('len', ['length']),
    ('max', ['maximum']),
    ('min', ['minimum']),
    ('tmp', ['temp', 'temporary']),
    ('src', ['source']),
    ('dst', ['destination']),
    ('usr', ['user']);
```

### Query Expansion Function

```sql
CREATE MACRO expand_code_query(query) AS (
    WITH tokens AS (
        SELECT unnest(string_split(lower(query), ' ')) as token
    ),
    expanded AS (
        SELECT
            t.token,
            COALESCE(a.expansions, [t.token]) as variants
        FROM tokens t
        LEFT JOIN code_abbreviations a ON t.token = a.abbrev
    )
    SELECT array_agg(DISTINCT term) as expanded_terms
    FROM expanded, unnest(variants) as v(term)
);

-- Example: expand_code_query('auth config')
-- Returns: ['auth', 'authentication', 'authorization', 'authenticate',
--           'config', 'configuration', 'configure', 'conf']
```

### Language-Specific Patterns

```sql
CREATE TABLE language_patterns (
    lang VARCHAR,
    pattern_type VARCHAR,
    prefix VARCHAR,
    suffix VARCHAR
);

INSERT INTO language_patterns VALUES
    ('csharp', 'interface', 'I', NULL),
    ('csharp', 'async', NULL, 'Async'),
    ('java', 'implementation', NULL, 'Impl'),
    ('python', 'private', '_', NULL),
    ('typescript', 'type', NULL, 'Type'),
    ('go', 'interface', NULL, 'er');
```

### Multi-Query Search with Expansion

```sql
CREATE MACRO search_expanded(query, k) AS (
    WITH expansions AS (
        SELECT unnest(expand_code_query(query)) as term
    ),
    all_results AS (
        -- Original query (highest weight)
        SELECT uri, score * 1.0 as weighted_score, 'original' as source
        FROM search(query, k := k * 2)

        UNION ALL

        -- Expanded terms
        SELECT uri, score * 0.7 as weighted_score, 'expanded' as source
        FROM expansions e,
             LATERAL search(e.term, k := k) s
    )
    -- RRF fusion
    SELECT uri, SUM(1.0 / (60 + row_number)) as rrf_score
    FROM (
        SELECT uri, row_number() OVER (PARTITION BY source ORDER BY weighted_score DESC) as row_number
        FROM all_results
    )
    GROUP BY uri
    ORDER BY rrf_score DESC
    LIMIT k
);
```

## Casing Transformations

```python
def generate_casing_variants(term: str) -> List[str]:
    """Generate common code casing variants."""
    variants = [
        term.lower(),                    # auth
        term.upper(),                    # AUTH
        term.capitalize(),               # Auth
        to_camel_case(term),            # auth (already)
        to_pascal_case(term),           # Auth
        to_snake_case(term),            # auth
    ]
    return list(set(variants))

def to_pascal_case(s: str) -> str:
    return ''.join(word.capitalize() for word in s.split('_'))
```

## Expected Benefits

| Metric | Without Expansion | With Expansion |
|--------|-------------------|----------------|
| Recall@10 | 0.65 | 0.78 (+20%) |
| Exact identifier matches | Low | High |
| Zero-result queries | Common | Rare |

## Performance Characteristics

- **Latency**: <5ms for expansion (dictionary lookup + string ops)
- **No LLM required**: Pure rule-based, deterministic
- **Parallelizable**: Each expanded query can search independently

## When NOT to Expand

| Query Type | Skip Expansion | Reason |
|------------|----------------|--------|
| Quoted exact match | Yes | User wants literal |
| Already long/specific | Yes | Risk of drift |
| Contains full identifier | Yes | Already specific |

```sql
-- Detect when to skip expansion
CASE
    WHEN query LIKE '"%"' THEN FALSE  -- Quoted
    WHEN length(query) > 50 THEN FALSE  -- Long
    WHEN query ~ '[A-Z][a-z]+[A-Z]' THEN FALSE  -- Already CamelCase
    ELSE TRUE
END as should_expand
```

## Open Questions

1. Should expansion be opt-in or default?
2. How to handle domain-specific abbreviations (user-configurable dictionary)?
3. Weight for expanded vs original terms in fusion?

## References

- [QueryExpansion.md](../research/algorithms/QueryExpansion.md) - Full expansion theory
- [QECK paper](https://arxiv.org/abs/1703.01443) - Crowd knowledge for code search
