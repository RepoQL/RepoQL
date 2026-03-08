# Search Accuracy Findings

## Summary

RepoQL's current search stack has good building blocks, but the architecture is fighting itself in a few high-leverage places:

1. There is no single ranking authority.
   `_search_candidates` computes a hybrid score, then `search()` re-buckets and re-scores documents with different rules, and then explore mutates scores again without always re-sorting.
2. Explore is still document-first.
   Strong symbol hits cannot reliably pull their file upward before truncation, and object recall is bounded by document recall.
3. Scope, chunk evidence, and object candidates are all lossy at retrieval time.
   Once relevant evidence is dropped, later boosts and JIT scoring can only decorate the survivors.

For an LLM querying a codebase, great search should have these properties:

- Scope is enforced before ranking, not after.
- The best matching unit can be either a document or a symbol.
- Ranking uses local evidence, not document-wide smearing.
- Context expansion is separate from match ranking.
- Query-matching spans survive through to rendering.

This review is based on static code inspection. The repoql MCP transport was unavailable during the review, so I used direct file inspection and parallel code review agents.

## Ranked Recommendations

### 1. Make Explore use one ranking engine end to end

**Impact:** Very high  
**Risk:** Medium  
**Why it matters:** Today, `explore` does not preserve the rank signal from the core hybrid search.

Current behavior:

- `_search_candidates` already computes lexical, fuzzy, semantic, RRF, and combined score in `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql:219`.
- The public `search()` wrapper then throws away most of that evidence, collapses to document-level `MAX(...)`, assigns coarse source buckets, and applies a second scoring model in `src/RepoQL.Data.DuckDB/Schema/Macros/hybrid_search.sql:73`, `src/RepoQL.Data.DuckDB/Schema/Macros/hybrid_search.sql:98`, and `src/RepoQL.Data.DuckDB/Schema/Macros/hybrid_search.sql:189`.
- `DocumentSearchService` and JIT document selection both use `search()`, not `_search_candidates`, in `src/RepoQL.ConsoleApp/Search/DocumentSearchService.cs:66`, `src/RepoQL.ConsoleApp/Search/DocumentSearchService.cs:94`, `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:278`, and `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:304`.

Why this is wrong:

- The system computes one hybrid rank, then replaces it with a second rank that is less expressive.
- Object-level evidence is lost before explore ever sees it.
- Later explore-side boosts often change scores without changing order, so the final output can disagree with the best available evidence.

Recommendation:

- Introduce an explore-specific retrieval macro or API that exposes the raw `_search_candidates` evidence directly.
- Keep `search()` as the human-facing convenience wrapper if needed, but stop using it as explore's primary retrieval source.
- Do rescue and boost logic as additive features on top of the primary hybrid rank, not as a separate bucketed ranker.

### 2. Make scope filtering first-class in retrieval

**Impact:** Very high  
**Risk:** Low  
**Why it matters:** Narrow scoped queries can silently lose relevant results before ranking.

Current behavior:

- When a scope contains `;`, `!`, or `#`, both document search services degrade the scope passed to `search()` to `%` and only intersect with `glob_files` after the global search in `src/RepoQL.ConsoleApp/Search/DocumentSearchService.cs:41`, `src/RepoQL.ConsoleApp/Search/DocumentSearchService.cs:323`, `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:262`, and `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:1190`.

Why this is wrong:

- Global top-`k` runs before scope intersection.
- Relevant in-scope documents can be dropped because unrelated global results consumed the candidate budget.
- This is especially damaging for LLM workflows that deliberately narrow search to a subsystem.

Recommendation:

- Push exact scoped document IDs into candidate generation before scoring.
- If `search()` cannot accept full glob/fragment/exclusion semantics, add an explore retrieval path that can.
- Treat scoped retrieval as a different corpus, not a post-filtered view of the global corpus.

### 3. Promote symbol search to a first-class retrieval path

**Impact:** Very high  
**Risk:** Medium  
**Why it matters:** RepoQL is a code search system, but explore still treats symbols as second-pass children of documents.

Current behavior:

- Standard explore search is `document search -> optional object search -> grouping` in `src/RepoQL.Explore/Search/IExploreSearchEngine.cs:117`.
- Query planning only decides whether to fetch objects from top documents; it never lets object retrieval drive the top-level rank in `src/RepoQL.Explore/Search/QueryStrategy.cs:34`.
- `search_symbol()` exists, but the explore pipeline does not route symbol-like queries through it.

Why this is wrong:

- A strong symbol hit in a lower-ranked document cannot surface unless its parent document already survives.
- Identifier-like queries should be able to retrieve symbols directly, not as decoration on documents.
- The final render shows documents as parents even when the child is the real answer.

Recommendation:

- For symbol-like queries, blend `search_symbol()` with hybrid retrieval instead of always going document-first.
- Rerank on a unified candidate pool containing both documents and symbols.
- Let the best child score promote its parent before limiting and rendering, or render the symbol as the top-level result when it is clearly the answer.

### 4. Remove position-based object truncation and filler

**Impact:** Very high  
**Risk:** Low  
**Why it matters:** Object candidate generation currently loses both recall and precision in deterministic ways.

Current behavior:

- `hybrid_object_candidates` computes cheap relevance features, but caps per-document candidates by line order in `src/RepoQL.Data.DuckDB/Schema/Macros/hybrid_search.sql:340`.
- Standard object search fills missing matches with the first objects by position and assigns them a neutral score in `src/RepoQL.ConsoleApp/Search/ObjectSearchService.cs:56` and `src/RepoQL.ConsoleApp/Search/ObjectSearchService.cs:183`.
- JIT object search expands candidates, but it still depends on `hybrid_object_candidates`, so late-file symbols may never enter the pool in `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:610`.

Why this is wrong:

- Late-file methods, handlers, and tests are invisible if they miss the positional cutoff.
- Filler objects are presented as if they were query matches.
- This causes LLMs to read plausible-looking but irrelevant symbols.

Recommendation:

- Rank object candidates by cheap relevance before applying `max_per_doc`.
- In question mode, never backfill ranked results with positional filler.
- If context expansion is desired, mark it explicitly as unranked context rather than as another match.

### 5. Replace the standard chunk booster with query-aware chunk evidence

**Impact:** High  
**Risk:** Low  
**Why it matters:** The standard explore path pretends to have query-aware chunk evidence, but it does not.

Current behavior:

- `DocumentSearchService.GetChunkScores()` assigns `1.0 as chunk_score` to every returned chunk in `src/RepoQL.ConsoleApp/Search/DocumentSearchService.cs:260`.
- `ChunkProximityBooster` then boosts object scores based on overlap with those chunks in `src/RepoQL.Explore/Search/ChunkProximityBooster.cs:28`.
- JIT search has a materially better query-aware chunk scoring path in `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:388`.

Why this is wrong:

- The standard path rewards overlap with arbitrary chunk boundaries, not with query-relevant spans.
- Provenance and confidence become misleading because "content" looks smarter than it is.

Recommendation:

- Either reuse the JIT chunk-scoring path in standard search or disable the standard booster entirely.
- Prefer carrying forward actual best-chunk spans and chunk scores from `_search_semantic` instead of reconstructing generic chunk regions later.

### 6. Stop smearing document evidence onto all child objects

**Impact:** High  
**Risk:** Medium  
**Why it matters:** File-level semantic and lexical hits currently contaminate object-level ranking.

Current behavior:

- `_search_semantic` reduces chunk evidence to the best chunk per document in `src/RepoQL.Data.DuckDB/Schema/Macros/search_semantic.sql:130` and `src/RepoQL.Data.DuckDB/Schema/Macros/search_semantic.sql:168`.
- `_search_candidates` joins semantic by `doc_id`, not `node_id`, and propagates `MAX(dense_score)` across the document in `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql:224` and `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql:230`.
- Lexical body hits are also evaluated against the document artifact, so one file-level body hit benefits every child node in `src/RepoQL.Data.DuckDB/Schema/Macros/search_lexical.sql:107`.

Why this is wrong:

- Unrelated members in a relevant file inherit relevance they did not earn.
- Object ranking becomes "which file was good" instead of "which symbol answered the query."

Recommendation:

- For object results, require object-local evidence or overlap with a query-relevant chunk/span before inheriting file-level score.
- Keep document score as a parent/context prior, not as the dominant object score.

### 7. Replace the lexical model with fielded token coverage

**Impact:** High  
**Risk:** Medium  
**Why it matters:** The lexical model under-expresses multi-term queries and is forcing the wrapper to compensate with regex rescue.

Current behavior:

- Lexical scoring relies heavily on `position(query IN field)` and `match_score()` over concatenated text in `src/RepoQL.Data.DuckDB/Schema/Macros/search_lexical.sql:83` through `src/RepoQL.Data.DuckDB/Schema/Macros/search_lexical.sql:111`.
- The public wrapper then derives OR-regex rescue from whitespace-split query text in `src/RepoQL.Data.DuckDB/Schema/Macros/hybrid_search.sql:40` and `src/RepoQL.Data.DuckDB/Schema/Macros/hybrid_search.sql:113`.

Why this is wrong:

- `"jwt refresh token"` behaves like a phrase until fuzzy fallback rescues it.
- OR-regex rescue improves recall by lowering precision, rather than by modeling term coverage correctly.
- Identifier-like queries and natural language queries are not using different lexical models.

Recommendation:

- Split queries into terms, score coverage across symbol, basename, path, structure, and body separately, and add explicit phrase bonuses.
- Reserve subsequence fuzzy matching for symbol-like queries rather than as a generic lexical fallback.
- Escape user text before any regex-derived rescue or boost logic.

### 8. Re-rank after any score-changing stage

**Impact:** High  
**Risk:** Low  
**Why it matters:** Several later stages mutate scores, but the display order is often still inherited from an earlier phase.

Current behavior:

- File groups are sorted before flattening in `src/RepoQL.Explore/Search/FileGrouper.cs:94`.
- Pattern boosts/penalties mutate `RawScore`, but the pipeline does not re-sort afterward in `src/RepoQL.Explore/Search/IExploreSearchEngine.cs:157` and `src/RepoQL.Explore/Search/PatternBooster.cs:24`.
- `ValueBasedAllocator` spends budget based on confidence but preserves input order in `src/RepoQL.Explore/ValueBasedAllocator.cs:35`.

Why this is wrong:

- A score that changes should be allowed to change rank.
- Today, some boosts are mostly cosmetic.

Recommendation:

- Add one reranking stage after object expansion and one after boost/penalty application.
- Compute parent file score from `max(documentScore, bestChildScore)` before final order, truncation, and confidence normalization.

### 9. Decouple retrieval breadth from render budget

**Impact:** Medium-high  
**Risk:** Low  
**Why it matters:** Search accuracy is currently constrained by presentation heuristics.

Current behavior:

- `QueryStrategy`, `FileGrouper`, and JIT config all derive retrieval limits directly from breadth and token budget in `src/RepoQL.Explore/Search/QueryStrategy.cs:40`, `src/RepoQL.Explore/Search/FileGrouper.cs:102`, and `src/RepoQL.Explore/Search/ObjectSearchTypes.cs:111`.

Why this is wrong:

- Retrieval depth should be chosen based on ambiguity and score dispersion, not only on how much output we plan to render.
- LLMs benefit from broader candidate recall even when final output is compact.

Recommendation:

- Pull a wider calibrated candidate set first.
- Let allocator and formatter control representation depth separately.
- Use score uncertainty or entropy to decide when to expand more documents or more objects.

### 10. Make JIT object reranking use real object text and user signals

**Impact:** Medium-high  
**Risk:** Medium  
**Why it matters:** JIT is the best architecture in the stack, but it still leaves accuracy on the table.

Current behavior:

- `NormalizeQueryAsync()` extracts boost-related signals, but object retrieval does not use them in cheap scoring in `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:137`.
- JIT embeddings are computed on `headline + structure`, not the real object span or fetched snippet in `src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs:906`.

Why this is wrong:

- The second-pass semantic model is not looking at the most discriminative text.
- User-supplied boost or penalize patterns do not meaningfully affect object retrieval.

Recommendation:

- Pass boost and penalize signals into cheap object scoring.
- For shortlisted objects, embed actual object text or fetched snippet rather than only x-ray text.
- This should become the default precision path for inspect/locate queries once the retrieval issues above are fixed.

## Recommended Order Of Attack

If the goal is dramatic accuracy improvement without destabilizing the product, I would do the work in this order:

1. Make scope filtering first-class in retrieval.
2. Stop using `search()` as explore's primary ranking source.
3. Remove position-based object truncation and positional filler.
4. Replace standard fake chunk boosting with real query-aware chunk evidence.
5. Add reranking after object expansion and after pattern boosts.
6. Replace lexical phrase containment with fielded token coverage.
7. Promote symbol retrieval to a first-class blended path.
8. Improve JIT object reranking with real object text.

## The Core Design Change

The central architectural change is simple:

- Retrieval should produce a wide, scoped, evidence-rich candidate pool.
- Reranking should be the single place where documents and symbols compete.
- Context expansion should happen after ranking, not during ranking.

RepoQL already has most of the pieces for this. The main problem is that the current pipeline mixes retrieval, rescue, ranking, and presentation too early, which makes later stages unable to recover lost evidence.
