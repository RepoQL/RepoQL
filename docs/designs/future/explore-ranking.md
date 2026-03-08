---
description: Unified ranking architecture for explore — single authority, scope-first retrieval, symbol-level competition.
tags: [design, explore, search, ranking, architecture]
audience: { human: 40, agent: 60 }
purpose: { design: 85, flow: 15 }
---

# Explore Ranking — Design

## North Star

An agent asks a question. The tool finds every relevant answer — whether it's a file, a function, a type, or a section — and returns them ranked by actual relevance, with precisely the right amount of context. Nothing relevant is missing. Nothing irrelevant is present. The ranking matches what a domain expert would produce.

## Context

Explore is RepoQL's most important tool. It's the difference between "I know where to look" and "I'm guessing." When explore returns wrong results or buries good ones, agents read irrelevant code, miss relevant code, and waste tokens on verification searches.

The current pipeline has good building blocks but the architecture is fighting itself. Three independent ranking stages re-score results with different rules. Symbols can only surface through their parent document. Complex scopes silently degrade. Chunk evidence is faked.

**Informed by:** `docs/findings/search-accuracy.md` — systematic analysis of accuracy gaps.

**Builds on:** `docs/designs/current/search-sql-simplification.md` — `_scope_filter()` macro, which this design depends on.

## Constraints

- DuckDB single-writer: all SQL changes via schema macros, no new tables
- Must be backward-compatible: `search()` SQL macro continues to work for direct callers
- Must work with partial embeddings (0-100% coverage, any model)
- Must not regress performance: current explore is 4-9 seconds, can't go slower
- Budget contract: explore's token budget promise doesn't change
- JIT and standard paths must share the same ranking logic

---

## Problems

### P1: Three ranking authorities

`_search_candidates` computes a rich hybrid score (BM25 + fuzzy + semantic, RRF fusion, combined with configurable weights). Then `search()` throws away everything except `doc_id`, `doc_semn`, and `bm25_score`, collapses to document level, assigns coarse tier labels (semantic/bm25/outline/body/search), and re-scores with a completely different formula involving regex mention counting. Then explore applies pattern boosts that change scores without re-sorting. Three independent scoring models, each overriding the last.

### P2: Document-first architecture

Objects can only surface if their parent document survives the document search. A perfect symbol match in a low-ranked file is invisible. The pipeline discovers documents first, then asks "what objects exist in these documents?" — but the real question is "what answers exist for this query?"

### P3: Scope degradation

When scope contains `;`, `!`, or `#` (the most expressive patterns), both `DocumentSearchService.ConvertScopeToSearchLike` and `JitObjectSearchService.ConvertGlobToLike` degrade to `"%"`. The search runs globally, then post-filters against `glob_files()`. This means the candidate budget (top-k) is consumed by globally-relevant but out-of-scope results, and in-scope results get dropped.

### P4: Fake chunk evidence

`DocumentSearchService.GetChunkScores()` assigns `1.0 as chunk_score` to every embedding chunk. `ChunkProximityBooster` then boosts objects based on overlap with *any* chunk, not query-relevant chunks. JIT has real chunk scoring (cosine similarity against query embedding, threshold > 0.3). The standard path pretends.

### P5: Positional filler

`ObjectSearchService.GetObjectsByPosition` fills gaps with objects ordered by line position at score `0.5`. These are presented identically to search-matched objects. Agents read them thinking they're relevant.

### P6: Score evidence smearing

`_search_candidates` propagates semantic scores from document to all children via `MAX(dense_score) GROUP BY doc_id`. Every object in a semantically-relevant file inherits that relevance, whether the object itself is relevant or not.

---

## Design

### Architecture: Retrieve → Score → Rank → Enrich → Allocate

```
┌──────────────────────────────────────────────────────────┐
│                    Explore Request                         │
│  keywords, scope, breadth, budget, boost, penalize        │
└──────────────────────────────────────────────────────────┘
                            │
                            ▼
┌──────────────────────────────────────────────────────────┐
│              Phase 1: Scoped Retrieval                    │
│                                                           │
│  _scope_filter(uri_glob) ──► candidate universe           │
│  _search_lexical() ────────► lexical candidates + scores  │
│  _search_semantic() ───────► semantic candidates + scores │
│  search_symbol() ──────────► symbol candidates (optional) │
│                                                           │
│  All three respect scope BEFORE scoring.                  │
│  Union produces wide candidate pool.                      │
└──────────────────────────────────────────────────────────┘
                            │
                            ▼
┌──────────────────────────────────────────────────────────┐
│              Phase 2: Unified Scoring                     │
│                                                           │
│  Each candidate gets ONE combined score:                  │
│    combine(bm25_norm, fuzz_norm, sem_norm, weights)       │
│                                                           │
│  Intent-aware weights (symbol/semantic/auto).             │
│  Document semantic prior dampened for child objects.       │
│  Chunk-level evidence used when available.                │
└──────────────────────────────────────────────────────────┘
                            │
                            ▼
┌──────────────────────────────────────────────────────────┐
│              Phase 3: Unified Ranking                     │
│                                                           │
│  Documents and symbols compete in ONE pool.               │
│  Top-k applied ONCE after scoring.                        │
│  Symbol can promote its parent document.                  │
│  Document score = max(own_score, best_child_score * 0.9). │
│                                                           │
│  Pattern boost/penalty applied. RE-SORT after.            │
└──────────────────────────────────────────────────────────┘
                            │
                            ▼
┌──────────────────────────────────────────────────────────┐
│              Phase 4: Evidence Enrichment                  │
│                                                           │
│  After ranking — no score changes:                        │
│  - Fetch snippets for top results                         │
│  - Add parent context for orphan symbols                  │
│  - Populate chunk locations from semantic search           │
│  - Compute provenance labels                              │
└──────────────────────────────────────────────────────────┘
                            │
                            ▼
┌──────────────────────────────────────────────────────────┐
│              Phase 5: Budget Allocation                    │
│                                                           │
│  ValueBasedAllocator: rank + confidence → representation  │
│  No score mutations here — just presentation decisions.   │
└──────────────────────────────────────────────────────────┘
```

### Component Changes

#### SQL Layer: New `_explore_candidates()` macro

A new macro in `search.sql` that replaces `search()` as explore's retrieval source. Unlike `search()`, it:

- Accepts full `uri_glob` (not LIKE pattern) — scope is first-class
- Returns both documents and objects in one result set with per-node scores
- Preserves chunk evidence (best_chunk_start, best_chunk_end, chunk_score)
- Does NOT re-bucket or re-score — the `combine()` output IS the final score
- Does NOT do rescue (rescue is a search() concern, not explore's)

```sql
CREATE OR REPLACE MACRO _explore_candidates(
    q,
    uri_glob := NULL,
    k := 100,
    mode := 'auto'
) AS TABLE (
    -- Phase 1: scoped retrieval
    -- Phase 2: unified scoring (reuses _search_candidates internals)
    -- Phase 3: unified ranking (documents + objects, one pool, one sort)
    -- Returns: node_id, doc_id, uri, kind, symbol, headline, structure,
    --          score, sem_score, bm25_score, fuzz_score, confidence,
    --          best_chunk_start, best_chunk_end, chunk_score,
    --          line_start, line_end, provenance
);
```

This macro is built from `_search_candidates` internals but differs in three ways:

1. **No document-level collapse.** Results stay at the granularity `_search_candidates` produces — both documents and objects.
2. **Dampened semantic inheritance.** Objects inherit parent document's semantic score at 0.5x instead of 1.0x. Objects with their own semantic evidence (from chunk overlap) keep the better of own vs inherited.
3. **Chunk evidence preserved.** `best_chunk_start`, `best_chunk_end`, and actual `chunk_score` from `_search_semantic` flow through to output.

`search()` continues to exist unchanged for direct SQL callers and CLI use.

#### SQL Layer: Scope-first retrieval

`_scope_filter()` already exists and works correctly. The change is in the C# layer: `DocumentSearchService` and `JitObjectSearchService` stop degrading scopes to `%`. Instead, they pass the full glob to `_explore_candidates(uri_glob := ...)`, which delegates to `_scope_filter()` before scoring.

For scopes with `#` fragments (symbol targeting), the service extracts the fragment and passes it as both `uri_glob` (for scope filtering) and as an additional symbol filter in the query.

#### C# Layer: ExploreSearchService (new)

Replaces the current `IExploreSearchEngine` implementation. Single code path for both standard and JIT modes.

```
ExploreSearchService.SearchAsync(params)
    │
    ├── Call _explore_candidates(q, uri_glob, k)
    │   Returns: ranked candidates (docs + objects) with scores
    │
    ├── Apply pattern boosts/penalties
    │   RE-SORT after application
    │
    ├── Group: parent documents with child objects
    │   Document score = max(own, best_child * 0.9)
    │   Objects sorted by score within parent
    │
    ├── JIT enrichment (if available, breadth <= 7)
    │   Compute embeddings for top uncertain candidates
    │   Re-score with JIT evidence, re-sort if changed
    │
    ├── Fetch snippets for top results
    │   Real code snippets, not structure concatenation
    │
    └── Return SearchResult hierarchy
```

#### C# Layer: No positional filler

Objects come from `_explore_candidates` — scored candidates only. If a document has no matching objects, it appears as a document-only result. No backfilling with line-ordered objects at fake scores.

If the user needs object inventory (breadth >= 8, no question), the service queries objects by structure summary, not by position. These are explicitly marked as "inventory" rather than "search matches."

#### C# Layer: Real chunk scoring in standard path

The standard path uses chunk scores from `_search_semantic` (which already computes `best_chunk_start`, `best_chunk_end`, and real cosine scores). These flow through `_explore_candidates` to the C# layer. No need to re-query `document_embedding` with fake scores.

ChunkProximityBooster receives real scores and applies proportional boosting: a chunk scoring 0.8 gives 4x the boost of a chunk scoring 0.2.

#### C# Layer: Confidence normalization

Both JIT and standard paths use the same absolute confidence normalization (the current sigmoid + linear hybrid). No more divergent scales between paths.

### What Stays the Same

- `search()` SQL macro — unchanged, backward compatible
- `_search_lexical()` — unchanged
- `_search_semantic()` — unchanged (already has calibration + contrast gate)
- `_scope_filter()` — unchanged
- `ValueBasedAllocator` — unchanged (receives better-ranked input)
- `OutputComposer` — unchanged
- `ResultClusterer` — unchanged
- JIT embedding infrastructure — reused, just triggered differently

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| New `_explore_candidates` macro | Modifying `search()` | Backward compatibility; `search()` serves direct SQL callers well |
| Dampened inheritance (0.5x) | No inheritance / full inheritance | Objects in relevant files deserve a prior; full inheritance smears evidence |
| No positional filler | Backfilling gaps | Confidence in results; agents trust what we show |
| Single code path | Separate standard/JIT paths | Consistency; JIT becomes enrichment, not a parallel universe |
| Re-sort after boosts | Cosmetic-only boosts | If a boost changes the answer, the output should reflect it |

## Alternatives Considered

**LLM reranking.** Use an LLM to re-rank top-50 candidates. Adds latency and cost. Better approach: get the initial ranking right so LLM reranking isn't needed. Could be added later as an optional precision layer.

**Full symbol-first architecture.** Always search symbols, derive documents from symbol matches. Loses document-level queries ("find the config file") and broad inventory. The hybrid pool is better.

**Drop `search()` entirely.** Force all callers through `_explore_candidates`. Breaking change for existing SQL queries and CLI. Not worth the compatibility cost.

**Real BM25 with inverted index.** Replace the heuristic BM25 with DuckDB's full-text search. Adds index maintenance complexity. The heuristic works well for identifier-heavy code queries. Consider if natural-language queries become more common.

## Risks

| Risk | Mitigation |
|------|------------|
| Performance regression from wider candidate pool | `_scope_filter` limits universe before scoring; same `max_cand` budget |
| JIT enrichment adds latency | JIT is optional enhancement, not critical path; timeout after 3s |
| Dampened inheritance misses relevant objects | 0.5x is dampening, not elimination; object-local evidence always preferred |
| Breaking change in ExploreSearchService | Feature-flagged; old path available until validated |

## Implementation Sequence

### Phase 1: `_explore_candidates` SQL macro
New macro alongside `_search_candidates`. Shares internals, differs in: no document collapse, dampened inheritance, chunk evidence passthrough. Build + test.

### Phase 2: Scope-first in C# layer
`ExploreSearchService` passes full glob to `_explore_candidates`. Remove `ConvertScopeToSearchLike` degradation. Build + test with scoped queries.

### Phase 3: Remove positional filler
`ObjectSearchService.GetObjectsByPosition` stops being called from explore. Object results are search-matched only. Build + test.

### Phase 4: Real chunk scoring
Standard path uses chunk scores from `_explore_candidates` output. `ChunkProximityBooster` receives real scores. Remove `GetChunkScores()` fake path. Build + test.

### Phase 5: Re-sort after boosts
`PatternBooster` returns a flag indicating scores changed. If changed, re-sort before confidence normalization. Build + test.

### Phase 6: Unify JIT path
JIT becomes an enrichment step within `ExploreSearchService`, not a parallel path. Triggered after initial ranking for top uncertain candidates. Build + test.

### Phase 7: Validate and switch
Feature flag to A/B compare old vs new. Run standard test suite of explore queries. Measure: result relevance, timing, token usage. Switch default when satisfied.
