# Intelligent Context Selection

> Maximum insight, minimum tokens—for code, docs, configs, and everything else.

## The Opportunity

RepoQL already has solid hybrid search combining lexical (BM25, 15%), semantic (HNSW embeddings, 70%), and fuzzy (15%) scoring. But search results today are a **flat list ranked by score**. This misses opportunities:

| What we have | What we're missing |
|--------------|-------------------|
| Good relevance ranking | No diversity—similar files dominate |
| Text matching | No graph expansion—misses related content |
| File-level results | No structure—agent sees list, not architecture |
| Full content or nothing | No focus—wastes tokens on irrelevant parts |

RepoQL's unique asset is the **graph**—relationships between files across all content types. These synergies exploit that advantage.

## The Vision

Transform search from "here are matching files" to "here's the context you need, organized for understanding":

```
Query: "How does authentication work?"
Budget: 3000 tokens

┌─ Documentation ─────────────────────────────────────────────────┐
│ ● docs/auth-flow.md                                             │
│   ## Overview                                                   │
│   Authentication flows through AuthService → JwtValidator...    │
│   ## Token Refresh                                              │
│   When tokens expire, the refresh flow...                       │
│                                                                 │
│   [Sections matching query, 400 tokens]                         │
└─────────────────────────────────────────────────────────────────┘

┌─ src/Auth/ (4 files) ───────────────────────────────────────────┐
│ ● AuthService.cs → Authenticate()                               │
│   public async Task<AuthResult> Authenticate(Credentials c)     │
│   {                                                             │
│       var token = await _validator.ValidateAsync(c);            │
│       return new AuthResult(token, _config.Expiry);             │
│   }                                                             │
│                                                                 │
│   [Focused on core method, 200 tokens]                          │
│                                                                 │
│ Also in this directory:                                         │
│ ├─ AuthServiceV2.cs              (refactored, 85% similar)     │
│ ├─ IAuthService.cs               (interface)                   │
│ └─ vendor/AuthService.cs         (vendored copy, identical)    │
└─────────────────────────────────────────────────────────────────┘

┌─ Called by AuthService ─────────────────────────────────────────┐
│ ● JwtValidator.cs → ValidateAsync()                             │
│   public async Task<Token> ValidateAsync(Credentials c) {...}   │
│                                                                 │
│   [Edge target method only, 150 tokens]                         │
│                                                                 │
│ └─ UserRepository.cs             (data access, headline only)  │
└─────────────────────────────────────────────────────────────────┘

┌─ Configuration ─────────────────────────────────────────────────┐
│ ● config/auth-settings.yaml                                     │
│   jwt_secret: ${JWT_SECRET}                                     │
│   token_expiry: 3600                                            │
│   refresh_enabled: true                                         │
│                                                                 │
│   [Full config, 100 tokens]                                     │
└─────────────────────────────────────────────────────────────────┘

Budget: 3000 tokens
Spent:  ~2200 tokens across 4 clusters
Coverage: 9 files known (4 full/focused, 5 headlines)
Topics: implementation, validation, config, docs, interface
```

**What the agent learns:**
- How authentication works (docs + code)
- The key method to modify (focused snippet)
- Related files exist (V2, vendor copy, interface)
- Runtime configuration
- What calls what (graph structure)

**What we didn't waste tokens on:**
- 500 tokens of AuthServiceV2 that's 85% identical
- The other 15 methods in AuthService.cs
- Full UserRepository when only the relationship matters

---

## Core Principles

### 1. Awareness ≠ Full Content

Agents need to know what exists, but that doesn't mean reading everything. The hierarchy:

| Representation | Tokens | Use when |
|----------------|--------|----------|
| **Full content** | 100-500 | Canonical, highest relevance |
| **Focused snippet** | 50-200 | Relevant method/section only |
| **Structure** | 30-100 | Shape without implementation |
| **Headline** | 10-30 | Existence and relationship |

A headline like `AuthServiceV2.cs (refactored, 85% similar)` tells the agent everything needed to decide whether to read more.

### 2. Clustered Results Show Structure

Flat lists hide relationships. Clusters reveal them:

```
Flat (hides structure):
1. AuthService.cs
2. AuthServiceV2.cs
3. JwtValidator.cs
4. vendor/AuthService.cs
5. auth-flow.md

Clustered (reveals structure):
┌─ src/Auth/ ──────────────────────────┐
│ AuthService.cs, AuthServiceV2.cs,    │
│ vendor/AuthService.cs (copies)       │
└──────────────────────────────────────┘
┌─ Called by AuthService ──────────────┐
│ JwtValidator.cs                      │
└──────────────────────────────────────┘
┌─ Documentation ──────────────────────┐
│ auth-flow.md                         │
└──────────────────────────────────────┘
```

### 3. Labels Use What We Know

Cluster labels come from facts, not inference:

| Source | Confidence | Example |
|--------|------------|---------|
| Path prefix | High | `src/Auth/` |
| Edge type | High | "Called by AuthService" |
| SimHash | High | "85% similar to X" |
| Content type | High | "Documentation" |
| Fallback | Always works | "Related to results" |

### 4. Focused Snippets Multiply Token Value

Every file in results has a *reason* for being there. That reason points to the relevant part:

| Reason for inclusion | Focus signal | Show |
|---------------------|--------------|------|
| Semantic match | `best_chunk_start/end` | The matching chunk |
| Lexical match | Term position | Lines around the match |
| PPR "calls" edge | Called method | Just that method |
| PPR "implements" edge | Interface method | The implementation |
| PPR "links to" edge | Target section | That heading |
| SimHash similar | Delta from canonical | What's different |

**Token impact:**
- Before: 500 tokens for whole file
- After: 150 tokens for relevant method
- Result: 3x more coverage in same budget

### 5. The Graph Connects Everything

PPR expansion walks all relationship types, not just code imports:

| Edge Type | Connects | Example |
|-----------|----------|---------|
| `imports` | Code → Code | Find dependencies |
| `calls` | Code → Code | Find implementations |
| `implements` | Code → Code | Interface to concrete |
| `links` | Markdown → Markdown | Documentation graph |
| `references` | Config → Code | Trace configuration |
| `embeds` | Markdown → Mermaid | Find visualizations |
| `defines` | GraphQL → TypeScript | Schema to implementation |

Query about authentication? PPR finds the code, the docs that link to it, the config that references it, and the diagrams embedded in the docs.

---

## The Four Synergies

These improvements multiply when combined:

```
                    ┌─────────────────────────────┐
                    │   Synergy 4: Better Seeds   │
                    │   (BM25 + Query Expansion)  │
                    └─────────────┬───────────────┘
                                  │
                    More + better initial matches
                                  │
                                  ▼
                    ┌─────────────────────────────┐
                    │   Synergy 3: Deduplication  │
                    │   (SimHash)                 │
                    └─────────────┬───────────────┘
                                  │
                    Clones identified, not removed
                                  │
                                  ▼
                    ┌─────────────────────────────┐
                    │   Synergy 2: Module Scope   │
                    │   (Spectral + PPR)          │
                    └─────────────┬───────────────┘
                                  │
                    Graph expansion respects boundaries
                                  │
                                  ▼
                    ┌─────────────────────────────┐
                    │   Synergy 1: Selection      │
                    │   (MMR + Budget Allocation) │
                    └─────────────┬───────────────┘
                                  │
                    Diverse, focused, structured output
                                  │
                                  ▼
                         INTELLIGENT CONTEXT
```

### Synergy 1: Intelligent Context Selection
**PPR expansion + MMR diversity + Hierarchical budget allocation**

- PPR finds structurally related content the query didn't mention
- MMR ensures each selection adds new information
- Budget flows: clusters → files → objects → snippets

[Details: 01-intelligent-context-selection.md](01-intelligent-context-selection.md)

### Synergy 2: Module-Aware Search
**Spectral clustering + Bounded PPR**

- Spectral clustering discovers organizational boundaries at index time
- PPR expansion respects those boundaries—explores within modules first
- Prevents "random walk into infrastructure" problem

[Details: 02-module-aware-search.md](02-module-aware-search.md)

### Synergy 3: Deduplicated Search
**SimHash fingerprinting + Awareness preservation**

- 64-bit SimHash computed at index time (8 bytes per file)
- Near-duplicates shown as headlines, not full content
- Agent knows copies exist without wasting tokens on redundant content

[Details: 03-deduplicated-search.md](03-deduplicated-search.md)

### Synergy 4: Compound Recall
**BM25 tuning + Query expansion**

- BM25 parameters tuned for repository content (not web text)
- Abbreviations expanded: "auth" → "authentication authorization oauth"
- Better initial retrieval feeds everything downstream

[Details: 04-compound-recall.md](04-compound-recall.md)

---

## Hierarchical Budget Allocation

Token budget flows through three levels:

```
Budget: 3000 tokens
         │
         ▼
┌─────────────────────────────────────────────────────────────────┐
│ CLUSTER ALLOCATION (MMR-weighted)                               │
│                                                                 │
│ Documentation ────────── 800 tok (27%)  ← highest relevance    │
│ src/Auth/ ───────────── 900 tok (30%)  ← core implementation   │
│ Called by AuthService ── 600 tok (20%)  ← graph expansion      │
│ Configuration ────────── 400 tok (13%)  ← related content      │
│ Tests ───────────────── 300 tok (10%)  ← awareness only        │
└─────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────┐
│ FILE ALLOCATION (within src/Auth/, 900 tok)                     │
│                                                                 │
│ AuthService.cs ────────── 600 tok (67%)  ← canonical           │
│ IAuthService.cs ─────────  50 tok  (6%)  ← headline            │
│ AuthServiceV2.cs ─────────  50 tok  (6%)  ← headline (similar) │
│ vendor/AuthService.cs ──── 50 tok  (6%)  ← headline (clone)    │
│ Cluster overhead ──────── 150 tok (17%)  ← labels, structure   │
└─────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────┐
│ OBJECT ALLOCATION (within AuthService.cs, 600 tok)              │
│                                                                 │
│ Class signature ─────────  50 tok  (8%)  ← always show         │
│ Authenticate() ────────── 200 tok (33%)  ← query relevant      │
│ Constructor ───────────── 100 tok (17%)  ← shows dependencies  │
│ RefreshToken() ────────── 150 tok (25%)  ← semantic chunk hit  │
│ 5 other methods ──────────100 tok (17%)  ← headlines only      │
└─────────────────────────────────────────────────────────────────┘
```

---

## Modular Architecture

Every component independently testable and tunable. No monolithic search function.

```
┌─────────────────────────────────────────────────────────────────┐
│                    COMPONENT ARCHITECTURE                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  SCORERS (return score per item)                                │
│  _search_lexical(q)        → node_id, bm25_score, fuzz_score   │
│  _search_semantic(q)       → node_id, sem_score, chunk_loc     │
│  _score_ppr(seeds)         → node_id, ppr_score, edge_path     │
│  _score_diversity(items)   → node_id, mmr_penalty              │
│                                                                  │
│  DETECTORS (return classifications)                             │
│  _detect_duplicates(items) → node_id, canonical_id, hamming    │
│  _detect_clusters(items)   → node_id, cluster_id, label        │
│                                                                  │
│  EXPANDERS (transform inputs)                                   │
│  _expand_query(q)          → expanded_query, expansions[]      │
│  _expand_via_ppr(seeds)    → node_id, ppr_score, edge_path     │
│                                                                  │
│  FOCUSERS (select within-file regions)                          │
│  _focus_by_chunk(node)     → start_line, end_line              │
│  _focus_by_edge(node,edge) → symbol, span                      │
│  _focus_by_section(node,q) → heading, span                     │
│                                                                  │
│  ALLOCATORS (distribute budget)                                 │
│  _allocate_clusters(items,budget)  → cluster_id, budget        │
│  _allocate_files(cluster,budget)   → node_id, budget, level    │
│  _allocate_objects(file,budget)    → symbol, budget, level     │
│                                                                  │
│  FUSERS (compose scores)                                        │
│  _fuse_rrf(scores[], k)            → combined_score            │
│  _fuse_weighted(scores[], w[])     → combined_score            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

**Composition with tunable weights:**

```sql
CREATE MACRO search_intelligent(
    q,
    budget := 3000,
    -- Tunable weights
    w_lexical := 0.15,
    w_semantic := 0.70,
    w_graph := 0.15,
    -- Tunable thresholds
    simhash_threshold := 3,
    mmr_lambda := 0.7,
    ppr_alpha := 0.15,
    ppr_max_iter := 10
) AS (
    WITH
    expanded AS (SELECT * FROM _expand_query(q)),
    lex AS (SELECT * FROM _search_lexical(expanded.query)),
    sem AS (SELECT * FROM _search_semantic(expanded.query)),
    fused AS (SELECT * FROM _fuse_weighted(lex, sem, w_lexical, w_semantic)),
    graph_exp AS (SELECT * FROM _expand_via_ppr(fused.seeds, ppr_alpha, ppr_max_iter)),
    with_dupes AS (SELECT * FROM _detect_duplicates(graph_exp, simhash_threshold)),
    diverse AS (SELECT * FROM _score_diversity(with_dupes, mmr_lambda)),
    clustered AS (SELECT * FROM _detect_clusters(diverse)),
    allocated AS (SELECT * FROM _allocate_clusters(clustered, budget)),
    focused AS (SELECT * FROM _focus_snippets(allocated, q)),
    SELECT * FROM _render_clustered(focused)
);
```

**Testing each component in isolation:**

```sql
-- Test query expansion
SELECT * FROM _expand_query('auth config');
-- Expected: auth, authentication, authorization, oauth, config, configuration...

-- Test PPR with known graph
SELECT * FROM _expand_via_ppr(ARRAY['file:///src/Auth.cs'], alpha := 0.15);
-- Expected: related files with ppr_scores and edge_paths

-- Test duplicate detection
SELECT * FROM _detect_duplicates(candidates, threshold := 3);
-- Expected: groups with canonical_id and hamming distances

-- Test budget allocation
SELECT * FROM _allocate_clusters(clusters, budget := 3000);
-- Expected: cluster_id, allocated_budget summing to 3000
```

---

## Performance

Most components are fast. Two need care:

| Component | Query-time | Notes |
|-----------|------------|-------|
| Query expansion | <1ms | Dictionary lookup |
| Lexical search | ~10ms | FTS index |
| Semantic search | ~15ms | HNSW index |
| Score fusion | <1ms | Arithmetic |
| **PPR expansion** | ~30ms | Bounded: max_iter=10, max_nodes=500 |
| SimHash dedup | <1ms | XOR + popcount on ~100 items |
| MMR diversity | ~5ms | Pairwise on ~100 items |
| **Spectral clustering** | 0ms | **Index-time only** |
| Budget allocation | <1ms | Arithmetic |
| Snippet focusing | ~5ms | Already have chunk offsets |
| **Total** | **~70ms** | Well under interactive latency |

**Spectral clustering** is O(n³) but computed at index time and stored. Query just reads `cluster_id`.

**PPR** is bounded by `max_iter` and `min_score` threshold. Converges fast—10 iterations on 100k nodes touches ~5-10k, and we only need top-100.

---

## Implementation Roadmap

### Phase 1: Focused Snippets
**Effort**: Low | **Impact**: High | **Dependencies**: None

Already have `best_chunk_start/end` from semantic search. Use it.

```sql
-- Instead of returning whole file
-- Return just the matching chunk ± context
```

Token savings: 50-70% on file content.

### Phase 2: Query Expansion
**Effort**: Low | **Impact**: Medium | **Dependencies**: None

```sql
CREATE TABLE abbreviations (abbrev, expansions[]);
CREATE MACRO _expand_query(q) AS (...);
```

Catches "auth" → "authentication", "k8s" → "kubernetes".

### Phase 3: SimHash Deduplication
**Effort**: Low | **Impact**: Medium | **Dependencies**: None

```sql
ALTER TABLE artifact ADD COLUMN simhash UBIGINT;
-- Compute during indexing
-- Use at query time for grouping
```

8 bytes per file. Detect clones, show as headlines.

### Phase 4: Clustered Output
**Effort**: Medium | **Impact**: High | **Dependencies**: Phase 3

Group results by path, edge type, similarity. Label honestly.

### Phase 5: Budget Allocation
**Effort**: Medium | **Impact**: High | **Dependencies**: Phase 4

Hierarchical allocation: cluster → file → object.

### Phase 6: PPR Expansion
**Effort**: Medium | **Impact**: High | **Dependencies**: Phases 1-5

```sql
CREATE MACRO _expand_via_ppr(seeds, alpha, max_iter) AS (...);
```

Graph expansion with bounded iterations.

### Phase 7: MMR Diversity
**Effort**: Low | **Impact**: Medium | **Dependencies**: Phase 6

```sql
CREATE MACRO _score_diversity(items, lambda) AS (...);
```

Penalize similarity to already-selected items.

### Phase 8: Spectral Modules
**Effort**: High | **Impact**: Medium | **Dependencies**: Phase 6

Index-time clustering. Query-time lookup.

---

## Expected Impact

| Metric | Today | With Synergies |
|--------|-------|----------------|
| Topics in top-10 results | ~4 | ~8 |
| Redundant content | ~35% | <10% |
| Graph-related discovery | None | Automatic |
| Token efficiency | ~0.6 | ~0.9 |
| Zero-result queries | ~8% | <2% |

**The compound effect:**

```
Query expansion:     Finds 40% more relevant files
SimHash dedup:       Removes 25% redundant content
PPR expansion:       Discovers 30% more related files
Focused snippets:    3x more files in same token budget
Clustered output:    Agent understands structure, not just list

Combined: Agents see complete, organized context instead of
          redundant fragments of the same few files.
```

---

## References

- [01-intelligent-context-selection.md](01-intelligent-context-selection.md) — PPR + MMR + Budget allocation
- [02-module-aware-search.md](02-module-aware-search.md) — Spectral clustering + bounded expansion
- [03-deduplicated-search.md](03-deduplicated-search.md) — SimHash + awareness preservation
- [04-compound-recall.md](04-compound-recall.md) — BM25 tuning + query expansion

**Research foundations:**
- [GraphRanking.md](../../research/algorithms/GraphRanking.md) — PPR theory
- [BudgetedSelection.md](../../research/algorithms/BudgetedSelection.md) — MMR and submodular optimization
- [SketchingAlgorithms.md](../../research/algorithms/SketchingAlgorithms.md) — SimHash
- [SpectralGraphTheory.md](../../research/algorithms/SpectralGraphTheory.md) — Clustering
- [QueryExpansion.md](../../research/algorithms/QueryExpansion.md) — Expansion techniques
- [InformationTheory.md](../../research/algorithms/InformationTheory.md) — Entropy and diversity

---

*The goal: agents that understand context, not agents that search for files.*
