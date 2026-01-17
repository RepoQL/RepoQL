# Query Expansion and Retrieval Robustness

> Reference documentation for query expansion techniques to improve code search retrieval

## Table of Contents

1. [Overview](#overview)
2. [HyDE: Hypothetical Document Embeddings](#hyde-hypothetical-document-embeddings)
3. [Multi-Query Generation and Fusion](#multi-query-generation-and-fusion)
4. [Pseudo-Relevance Feedback (PRF)](#pseudo-relevance-feedback-prf)
5. [Query Rewriting with LLMs](#query-rewriting-with-llms)
6. [GraphRAG: Global-to-Local Approaches](#graphrag-global-to-local-approaches)
7. [Code-Specific Query Expansion](#code-specific-query-expansion)
8. [Combining Expansion with RRF](#combining-expansion-with-rrf)
9. [Implementation Strategies](#implementation-strategies)
10. [Best Practices and Common Pitfalls](#best-practices-and-common-pitfalls)
11. [References](#references)

---

## Overview

**Query expansion** transforms underspecified user queries into richer representations that better match relevant documents. This is critical for code search where:

- Developer queries are often terse ("where is auth handled?")
- Local code terminology may not match query terms
- Identifiers use abbreviations, camelCase, or domain jargon
- Semantic intent differs from lexical surface form

```
┌─────────────────────────────────────────────────────────────────┐
│                 Query Expansion Pipeline                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   User Query                                                     │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐    ┌──────────────┐    ┌──────────────┐      │
│   │   HyDE       │    │  Multi-Query │    │    PRF       │      │
│   │  Generation  │    │  Rewriting   │    │  Expansion   │      │
│   └──────┬───────┘    └──────┬───────┘    └──────┬───────┘      │
│          │                   │                   │               │
│          └───────────────────┼───────────────────┘               │
│                              ▼                                   │
│                      ┌──────────────┐                            │
│                      │  Rank Fusion │                            │
│                      │    (RRF)     │                            │
│                      └──────┬───────┘                            │
│                             │                                    │
│                             ▼                                    │
│                      Expanded Results                            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Why Query Expansion Matters

| Problem | Manifestation | Solution |
|---------|---------------|----------|
| **Vocabulary mismatch** | Query "auth" doesn't match `AuthenticationService` | Synonym/identifier expansion |
| **Embedding misalignment** | Questions and documents occupy different embedding regions | HyDE alignment |
| **Incomplete intent** | "config" could mean many things | Multi-query disambiguation |
| **Zero results** | Exact keywords not present | Semantic expansion |

### Technique Comparison

| Technique | Latency | Quality Gain | Best For |
|-----------|---------|--------------|----------|
| HyDE | High (~500ms) | +15-25% on zero-shot | Unfamiliar domains |
| Multi-Query + RRF | Medium (~200ms) | +10-20% comprehensiveness | Ambiguous queries |
| PRF | Low (~50ms) | +5-15% recall | Known-good corpus |
| LLM Rewriting | High (~300ms) | Variable | Complex multi-hop |

---

## HyDE: Hypothetical Document Embeddings

HyDE addresses the fundamental **embedding misalignment** problem: user questions and documents exist in different regions of embedding space because they have different linguistic structures.

### The Problem

```
┌─────────────────────────────────────────────────────────────────┐
│              Embedding Space Misalignment                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│     Query: "How does JWT validation work?"                       │
│                    ●                                             │
│                         (interrogative, short)                   │
│                                                                  │
│                                        ○ Document A              │
│                                        ○ Document B              │
│                                        ○ Document C              │
│                              (declarative, detailed)             │
│                                                                  │
│     Gap between question embedding and document embeddings       │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### The Solution

HyDE uses an LLM to generate a **hypothetical document** that would answer the query, then embeds that document instead of the query:

```
┌─────────────────────────────────────────────────────────────────┐
│                    HyDE Pipeline                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "How does JWT validation work?"                         │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────────────────────────────┐                       │
│   │  LLM generates hypothetical document │                       │
│   │  "JWT validation typically involves  │                       │
│   │   decoding the token, verifying the  │                       │
│   │   signature using the secret key..." │                       │
│   └──────────────────┬───────────────────┘                       │
│                      │                                           │
│                      ▼                                           │
│   ┌──────────────────────────────────────┐                       │
│   │  Embed hypothetical document         │                       │
│   │  (now in document embedding space)   │  ●────→ ○ ○ ○        │
│   └──────────────────┬───────────────────┘     closer to docs    │
│                      │                                           │
│                      ▼                                           │
│              Dense retrieval                                     │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Implementation

```python
def hyde_expand(query: str, llm: LLM, embedder: Embedder) -> np.ndarray:
    """Generate HyDE embedding for a query."""

    # Prompt for hypothetical document generation
    prompt = f"""Write a detailed passage that would answer this question.
Do not explain that you are writing a passage - just write the content directly.

Question: {query}

Passage:"""

    # Generate hypothetical document
    hypothetical_doc = llm.generate(prompt, max_tokens=256)

    # Embed the hypothetical document (not the query!)
    embedding = embedder.encode(hypothetical_doc)

    return embedding

def hyde_search(query: str, k: int = 10) -> List[Document]:
    """Search using HyDE expansion."""

    # Generate multiple hypothetical documents for robustness
    hypothetical_docs = [
        generate_hypothetical(query)
        for _ in range(3)
    ]

    # Average embeddings
    embeddings = [embed(doc) for doc in hypothetical_docs]
    avg_embedding = np.mean(embeddings, axis=0)

    # Retrieve using averaged embedding
    return vector_search(avg_embedding, k=k)
```

### Performance Characteristics

| Benchmark | Baseline (Direct) | HyDE | Improvement |
|-----------|-------------------|------|-------------|
| TREC DL19 | 0.480 | 0.603 | +25.6% |
| TREC DL20 | 0.474 | 0.559 | +17.9% |
| BEIR (avg) | 0.412 | 0.478 | +16.0% |

**Key insight**: HyDE excels in **zero-shot** scenarios where you have no training data for the target domain.

### When to Use HyDE

| Scenario | Use HyDE? | Rationale |
|----------|-----------|-----------|
| New/unfamiliar codebase | Yes | No domain-specific training |
| Conceptual queries | Yes | "How does X work?" benefits from expansion |
| Exact identifier search | No | Direct matching is faster and precise |
| High-throughput indexing | No | Too slow for bulk operations |

---

## Multi-Query Generation and Fusion

Multi-query expansion generates **diverse query variants** to capture different facets of user intent, then fuses results using Reciprocal Rank Fusion (RRF).

### RAG-Fusion Approach

```
┌─────────────────────────────────────────────────────────────────┐
│                  RAG-Fusion Pipeline                             │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Original: "authentication flow"                                │
│       │                                                          │
│       ▼                                                          │
│   ┌─────────────────────────────────────────────┐                │
│   │  LLM generates diverse queries:             │                │
│   │  1. "user login authentication process"     │                │
│   │  2. "JWT token validation middleware"       │                │
│   │  3. "OAuth2 authorization code flow"        │                │
│   │  4. "session management authentication"     │                │
│   └─────────────────────────────────────────────┘                │
│       │         │         │         │                            │
│       ▼         ▼         ▼         ▼                            │
│   ┌──────┐  ┌──────┐  ┌──────┐  ┌──────┐                        │
│   │Search│  │Search│  │Search│  │Search│                        │
│   └──┬───┘  └──┬───┘  └──┬───┘  └──┬───┘                        │
│      │         │         │         │                             │
│      └─────────┴────┬────┴─────────┘                             │
│                     ▼                                            │
│              ┌──────────────┐                                    │
│              │  RRF Fusion  │                                    │
│              └──────┬───────┘                                    │
│                     ▼                                            │
│              Fused Rankings                                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### DMQR-RAG: Diverse Multi-Query Rewriting

Standard multi-query often produces **nearly identical** rewrites. DMQR-RAG enforces diversity:

```python
DIVERSE_QUERY_PROMPT = """Generate 4 diverse search queries for the following question.
Each query should approach the topic from a DIFFERENT angle:
1. Use different terminology/synonyms
2. Focus on different aspects of the question
3. Vary specificity (some broad, some narrow)
4. Include domain-specific jargon where appropriate

Original question: {query}

Generate 4 diverse queries (one per line):"""

def generate_diverse_queries(query: str, llm: LLM) -> List[str]:
    """Generate diverse query variants."""
    response = llm.generate(DIVERSE_QUERY_PROMPT.format(query=query))
    queries = [q.strip() for q in response.split('\n') if q.strip()]
    return [query] + queries[:4]  # Include original + 4 variants
```

### RRF Fusion

Reciprocal Rank Fusion combines multiple ranked lists without requiring score calibration:

```
RRF_score(d) = Σ 1/(k + rank_i(d))

where:
  - d is a document
  - rank_i(d) is the rank of d in result list i
  - k is a constant (typically 60)
```

```sql
-- DuckDB implementation of RRF
WITH query_results AS (
    -- Results from query variant 1
    SELECT doc_id, row_number() OVER (ORDER BY score DESC) as rank, 1 as query_id
    FROM search('authentication flow', k := 100)
    UNION ALL
    -- Results from query variant 2
    SELECT doc_id, row_number() OVER (ORDER BY score DESC) as rank, 2 as query_id
    FROM search('user login process', k := 100)
    UNION ALL
    -- Results from query variant 3
    SELECT doc_id, row_number() OVER (ORDER BY score DESC) as rank, 3 as query_id
    FROM search('JWT token validation', k := 100)
)
SELECT
    doc_id,
    SUM(1.0 / (60 + rank)) as rrf_score
FROM query_results
GROUP BY doc_id
ORDER BY rrf_score DESC
LIMIT 20;
```

### Performance Trade-offs

| Metric | Single Query | Multi-Query (4) | Change |
|--------|--------------|-----------------|--------|
| Recall@10 | 0.65 | 0.78 | +20% |
| Latency | 50ms | 88ms | +76% (parallel) |
| Comprehensiveness | Baseline | +35% | Significant |
| Risk of drift | Low | Medium | Monitor relevance |

**Warning**: Non-representative subqueries can cause **relevance drift** - always include the original query in fusion.

---

## Pseudo-Relevance Feedback (PRF)

PRF uses top-ranked documents from an initial retrieval to expand the query, assuming top results are relevant.

### Classic PRF (Rocchio)

```
q' = α·q + β·(1/|R|)·Σ(d∈R) d - γ·(1/|NR|)·Σ(d∈NR) d

where:
  - q' is expanded query
  - q is original query
  - R is pseudo-relevant set (top-k docs)
  - NR is non-relevant set
  - α, β, γ are weights (typically α=1, β=0.75, γ=0.15)
```

### ColBERT-PRF: Neural PRF

ColBERT-PRF extends PRF to dense retrieval using ColBERT's multi-vector representations:

```
┌─────────────────────────────────────────────────────────────────┐
│                  ColBERT-PRF Pipeline                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "JWT validation"                                        │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────────────────────────────┐                       │
│   │  First-pass ColBERT retrieval        │                       │
│   │  → Top-k pseudo-relevant docs        │                       │
│   └──────────────────┬───────────────────┘                       │
│                      │                                           │
│                      ▼                                           │
│   ┌──────────────────────────────────────┐                       │
│   │  Extract token embeddings from       │                       │
│   │  pseudo-relevant documents           │                       │
│   └──────────────────┬───────────────────┘                       │
│                      │                                           │
│                      ▼                                           │
│   ┌──────────────────────────────────────┐                       │
│   │  K-means clustering to find          │                       │
│   │  representative feedback embeddings  │                       │
│   └──────────────────┬───────────────────┘                       │
│                      │                                           │
│                      ▼                                           │
│   ┌──────────────────────────────────────┐                       │
│   │  Select discriminative embeddings    │                       │
│   │  (high IDF, expand query repr.)      │                       │
│   └──────────────────┬───────────────────┘                       │
│                      │                                           │
│                      ▼                                           │
│   Second-pass retrieval with expanded query                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Performance Results

| Dataset | ColBERT | ColBERT-PRF | Improvement |
|---------|---------|-------------|-------------|
| TREC DL19 (MAP) | 0.401 | 0.505 | +26% |
| TREC DL20 (MAP) | 0.392 | 0.431 | +10% |
| MS MARCO (MRR) | 0.360 | 0.378 | +5% |

### When PRF Works Best

| Condition | PRF Effectiveness |
|-----------|-------------------|
| High-quality corpus | Excellent |
| Homogeneous topic | Good |
| Noisy/diverse corpus | Poor (topic drift) |
| Very short queries | Good |
| Long, specific queries | Marginal |

---

## Query Rewriting with LLMs

LLMs can rewrite queries to be more retrieval-friendly through several strategies.

### Query2Doc

Generate a pseudo-document that expands the query with relevant context:

```python
QUERY2DOC_PROMPT = """Write a passage that provides background information
to help answer this question. Include relevant technical terms, concepts,
and context that would appear in documentation about this topic.

Question: {query}

Background passage:"""

def query2doc_expand(query: str, llm: LLM) -> str:
    """Expand query with generated background context."""
    background = llm.generate(QUERY2DOC_PROMPT.format(query=query))
    # Concatenate original query with background for retrieval
    return f"{query} {background}"
```

### Step-Back Prompting

For complex queries, first generate a higher-level "step-back" question:

```python
STEPBACK_PROMPT = """Given a specific question, generate a more general
"step-back" question that would help answer the original.

Original: "Why does the AuthMiddleware reject tokens after 1 hour?"
Step-back: "How does JWT token expiration work in authentication middleware?"

Original: "{query}"
Step-back:"""

def stepback_expand(query: str, llm: LLM) -> List[str]:
    """Generate step-back question for broader context."""
    stepback = llm.generate(STEPBACK_PROMPT.format(query=query))
    return [query, stepback]  # Search both
```

### Chain-of-Verification

For factual queries, verify and refine the expansion:

```
1. Generate initial expansion
2. Generate verification questions about the expansion
3. Answer verification questions independently
4. Refine expansion based on verification
```

---

## GraphRAG: Global-to-Local Approaches

GraphRAG addresses **global queries** like "What are the main themes?" that standard RAG cannot answer.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                  GraphRAG Architecture                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   INDEXING PHASE                                                 │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │  Documents → Entity Extraction → Knowledge Graph        │   │
│   │                                                         │   │
│   │  ┌─────┐    ┌─────┐    ┌─────┐                         │   │
│   │  │Doc A│───▶│ LLM │───▶│ KG  │                         │   │
│   │  │Doc B│    │Extract   │Nodes│                         │   │
│   │  │Doc C│    │Entities│ │Edges│                         │   │
│   │  └─────┘    └─────┘    └─────┘                         │   │
│   │                           │                             │   │
│   │                           ▼                             │   │
│   │                    ┌──────────────┐                     │   │
│   │                    │   Leiden     │                     │   │
│   │                    │  Community   │                     │   │
│   │                    │  Detection   │                     │   │
│   │                    └──────┬───────┘                     │   │
│   │                           │                             │   │
│   │                           ▼                             │   │
│   │                    Community Summaries                  │   │
│   │                    (hierarchical)                       │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│   QUERY PHASE                                                    │
│   ┌─────────────────────────────────────────────────────────┐   │
│   │  Global Query: "What are the main patterns?"            │   │
│   │       │                                                 │   │
│   │       ▼                                                 │   │
│   │  Community summaries → Partial answers → Final answer   │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Query Routing

| Query Type | Routing | Method |
|------------|---------|--------|
| **Global** ("What are the themes?") | Community summaries | Map-reduce over summaries |
| **Local** ("What does function X do?") | Entity retrieval | Standard RAG |
| **Mixed** ("How do auth patterns vary?") | Hybrid | Communities + local docs |

### Application to Code Search

GraphRAG's community detection maps naturally to code:

| Code Concept | Graph Representation |
|--------------|---------------------|
| Module/Package | Community |
| Class hierarchy | Subgraph |
| Import relationships | Edges |
| Shared dependencies | Community membership |

```sql
-- Query community summaries for global code questions
SELECT
    community_id,
    summary,
    member_count
FROM code_communities
WHERE summary ILIKE '%authentication%'
ORDER BY member_count DESC;
```

---

## Code-Specific Query Expansion

Code search has unique expansion opportunities based on programming language structure.

### Identifier Expansion

```
┌─────────────────────────────────────────────────────────────────┐
│              Code-Specific Expansion Pipeline                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "auth service"                                          │
│       │                                                          │
│       ├──▶ Camel/Pascal split: auth, service                     │
│       │                                                          │
│       ├──▶ Abbreviation expand: authentication, authorization    │
│       │                                                          │
│       ├──▶ Pattern match: *Auth*, *Service*, *Authenticate*      │
│       │                                                          │
│       └──▶ Type inference: IAuthService, AuthenticationHandler   │
│                                                                  │
│   Expanded queries:                                              │
│   - "AuthenticationService"                                      │
│   - "IAuthenticationService"                                     │
│   - "AuthService"                                                │
│   - "authenticate user"                                          │
│   - "authorization middleware"                                   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Common Abbreviation Dictionary

| Abbreviation | Expansions |
|--------------|------------|
| auth | authentication, authorization, authenticate |
| config | configuration, configure |
| repo | repository |
| impl | implementation, implement |
| util | utility, utilities |
| db | database |
| msg | message |
| ctx | context |
| req | request |
| res | response |
| err | error |
| cb | callback |
| fn | function |

### Language-Specific Patterns

| Language | Pattern | Example Expansion |
|----------|---------|-------------------|
| C# | I{Name} interface | `auth` → `IAuthenticationService` |
| Java | {Name}Impl | `repository` → `UserRepositoryImpl` |
| Python | _{name} private | `validate` → `_validate_token` |
| TypeScript | {name}.ts module | `auth` → `auth.service.ts` |
| Go | {Name}er interface | `read` → `Reader` |

### QECK: Crowd Knowledge Expansion

QECK (Query Expansion based on Crowd Knowledge) uses Stack Overflow to find software-specific expansions:

```python
def qeck_expand(query: str, so_index: Index) -> List[str]:
    """Expand query using Stack Overflow knowledge."""

    # Find related SO questions
    related_questions = so_index.search(query, k=10)

    # Extract API terms from accepted answers
    api_terms = []
    for q in related_questions:
        answer = q.accepted_answer
        api_terms.extend(extract_api_mentions(answer))

    # Rank by frequency and relevance
    expansion_terms = rank_terms(api_terms, query)

    return [query] + expansion_terms[:5]
```

---

## Combining Expansion with RRF

Multiple expansion techniques can be combined using RRF for robust retrieval.

### Multi-Expansion Fusion

```
┌─────────────────────────────────────────────────────────────────┐
│              Multi-Expansion RRF Pipeline                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "where is auth handled"                                 │
│       │                                                          │
│       ├──▶ Original query search                                 │
│       │                                                          │
│       ├──▶ HyDE expansion search                                 │
│       │                                                          │
│       ├──▶ Multi-query variants search                           │
│       │                                                          │
│       ├──▶ Identifier expansion search                           │
│       │                                                          │
│       └──▶ PRF expansion search                                  │
│                                                                  │
│                     │                                            │
│                     ▼                                            │
│              ┌──────────────┐                                    │
│              │  Weighted    │                                    │
│              │  RRF Fusion  │                                    │
│              │              │                                    │
│              │  w_orig=1.0  │                                    │
│              │  w_hyde=0.8  │                                    │
│              │  w_multi=0.6 │                                    │
│              │  w_ident=0.7 │                                    │
│              │  w_prf=0.5   │                                    │
│              └──────┬───────┘                                    │
│                     │                                            │
│                     ▼                                            │
│              Final Rankings                                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Weighted RRF Implementation

```sql
-- Weighted RRF with multiple expansion sources
WITH expansions AS (
    SELECT 'original' as source, 1.0 as weight
    UNION ALL SELECT 'hyde', 0.8
    UNION ALL SELECT 'multiquery', 0.6
    UNION ALL SELECT 'identifier', 0.7
),
all_results AS (
    SELECT doc_id, rank, source
    FROM (
        SELECT doc_id, row_number() OVER (ORDER BY score DESC) as rank, 'original' as source
        FROM search('where is auth handled', k := 50)
        UNION ALL
        SELECT doc_id, row_number() OVER (ORDER BY score DESC) as rank, 'hyde' as source
        FROM search_hyde('where is auth handled', k := 50)
        UNION ALL
        SELECT doc_id, row_number() OVER (ORDER BY score DESC) as rank, 'identifier' as source
        FROM search('AuthenticationService AuthHandler', k := 50)
    )
)
SELECT
    r.doc_id,
    SUM(e.weight / (60 + r.rank)) as weighted_rrf_score
FROM all_results r
JOIN expansions e ON r.source = e.source
GROUP BY r.doc_id
ORDER BY weighted_rrf_score DESC
LIMIT 20;
```

---

## Implementation Strategies

### Strategy Selection by Query Type

```
┌─────────────────────────────────────────────────────────────────┐
│           Query Expansion Decision Tree                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query received                                                 │
│       │                                                          │
│       ▼                                                          │
│   Is it an exact identifier? (e.g., "AuthService")               │
│       │                                                          │
│       ├─ Yes ──▶ Direct search only (no expansion)               │
│       │                                                          │
│       └─ No                                                      │
│           │                                                      │
│           ▼                                                      │
│       Is it a conceptual query? (e.g., "how does X work")        │
│           │                                                      │
│           ├─ Yes ──▶ HyDE + Multi-query                          │
│           │                                                      │
│           └─ No                                                  │
│               │                                                  │
│               ▼                                                  │
│           Contains abbreviations? (e.g., "auth config")          │
│               │                                                  │
│               ├─ Yes ──▶ Identifier expansion + Original         │
│               │                                                  │
│               └─ No ──▶ Multi-query + PRF                        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Latency Budgets

| Budget | Strategy | Expected Quality |
|--------|----------|------------------|
| <50ms | Original only | Baseline |
| 50-150ms | Original + identifier expansion | +10% |
| 150-300ms | Multi-query (parallel) + RRF | +20% |
| 300-500ms | HyDE + multi-query + RRF | +30% |
| >500ms | Full pipeline with PRF | +35% |

### Caching Strategies

```python
class ExpansionCache:
    """Cache expensive expansions for repeated queries."""

    def __init__(self, ttl_seconds: int = 3600):
        self.hyde_cache = TTLCache(maxsize=1000, ttl=ttl_seconds)
        self.multiquery_cache = TTLCache(maxsize=1000, ttl=ttl_seconds)

    def get_hyde(self, query: str) -> Optional[str]:
        return self.hyde_cache.get(query)

    def set_hyde(self, query: str, expansion: str):
        self.hyde_cache[query] = expansion
```

---

## Best Practices and Common Pitfalls

### Best Practices

| Practice | Rationale |
|----------|-----------|
| **Always include original query** | Prevents drift from user intent |
| **Limit expansion count** | >5 variants show diminishing returns |
| **Use async/parallel retrieval** | Multi-query shouldn't multiply latency |
| **Monitor relevance drift** | Track when expansions hurt precision |
| **Cache HyDE results** | LLM calls are expensive |
| **Tune RRF k parameter** | k=60 is default; tune for your domain |

### Common Pitfalls

| Pitfall | Symptom | Solution |
|---------|---------|----------|
| **Over-expansion** | Results too broad/noisy | Limit to 3-4 query variants |
| **HyDE hallucination** | Expanded query introduces wrong terms | Use multiple hypotheticals + averaging |
| **PRF topic drift** | Expansion pulls in unrelated topics | Use fewer PRF docs, higher quality threshold |
| **Latency explosion** | Sequential expansion calls | Parallelize independent expansions |
| **Ignoring original** | Losing user's exact intent | Weight original query highest in RRF |

### Quality Monitoring

```sql
-- Track expansion effectiveness
SELECT
    expansion_method,
    AVG(CASE WHEN clicked THEN 1 ELSE 0 END) as click_rate,
    AVG(position_of_first_click) as avg_first_click_pos,
    COUNT(*) as query_count
FROM search_logs
WHERE timestamp > now() - INTERVAL 7 DAY
GROUP BY expansion_method
ORDER BY click_rate DESC;
```

---

## References

### Core Papers

| Paper | Year | Focus |
|-------|------|-------|
| [HyDE: Precise Zero-Shot Dense Retrieval](https://arxiv.org/abs/2212.10496) | 2023 | Hypothetical document embeddings |
| [GraphRAG: Local to Global](https://arxiv.org/abs/2404.16130) | 2024 | Community-based global retrieval |
| [RAG-Fusion](https://arxiv.org/abs/2402.03367) | 2024 | Multi-query + RRF |
| [DMQR-RAG](https://arxiv.org/abs/2411.13154) | 2024 | Diverse multi-query rewriting |
| [ColBERT-PRF](https://dl.acm.org/doi/10.1145/3572405) | 2023 | Neural pseudo-relevance feedback |
| [Query2Doc](https://arxiv.org/abs/2303.07678) | 2023 | LLM query expansion |
| [Step-Back Prompting](https://arxiv.org/abs/2310.06117) | 2023 | Abstraction for complex queries |
| [QECK](https://arxiv.org/abs/1703.01443) | 2017 | Crowd knowledge for code search |

### Implementation Resources

- [Haystack HyDE Documentation](https://docs.haystack.deepset.ai/docs/hypothetical-document-embeddings-hyde)
- [Microsoft GraphRAG](https://github.com/microsoft/graphrag)
- [LangChain Query Transformation](https://python.langchain.com/docs/how_to/query_multi_step)

---

*Query expansion transforms "what the user said" into "what the user meant" — the gap between these determines retrieval success.*
