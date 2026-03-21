---
description: Two-pass search architecture — document scoring then object scoring using both lexical and semantic signals mapped to objects via span byte/line ranges.
tags: [search, performance, design, lexical, semantic, objects]
audience: { human: 40, agent: 60 }
purpose: { design: 80, flow: 20 }
---

# Two-Pass Object Scoring

## North Star

Find the right objects in the right files. Phase 1 finds hot files cheaply. Phase 2 maps evidence onto objects within those files using both lexical and semantic signals. Every emitted object has evidence — no inherited-score filler.

## Context

`search()` takes ~5s. Semantic search was optimized from 4.4s to 0.83s (DuckDB TABLE macro fixes). Lexical search at 2.6s is now the bottleneck. The expensive part is object expansion: joining 5000 ranked documents through `span` → `node` to produce ~100K child objects, then scoring each with CASE + UDF calls.

**The problem:** 95%+ of expanded objects have no keyword signal. They inherit the document's score unchanged. Codex measured: for "authentication", only 491 of 104,364 objects had any signal (0.47%). For "ValidateToken", 24 of 124,012 (0.02%). We're spending 1.7s manufacturing duplicate-score rows that `search()` discards when it aggregates back to documents.

**The original intent was two-pass:** find relevant files, then find relevant chunks within those files. The implementation drifted — Phase 2 expanded ALL children of ALL ranked documents with shallow scoring.

**Compounding issue:** `_search_lexical` does object expansion with only lexical signals. It can't see semantic chunk scores because those live in `_search_semantic`, a separate macro. Objects can't be scored against both signals.

## Constraints

- Schema frozen: 5 tables, extend via views/macros/UDFs
- DuckDB TABLE macro traps: no CTE multi-reference, no raw params in QUALIFY, no casts at use site
- `search()` output contract must be preserved (callers depend on the column set)
- `_search_candidates` output contract: `(node_id, doc_id, uri, path, kind, symbol, ..., score, confidence)` — used by explore, read, and hybrid_search
- grep_matches is a UDF that returns line numbers + URIs (already available, line data currently discarded)
- document_embedding has per-chunk `start_byte`/`end_byte` and cosine scores per chunk

## Design

### Architecture Change

```
BEFORE:
  _search_lexical  → doc scores + ALL objects (lexical only)
  _search_semantic → doc scores + chunk data
  _search_candidates → UNION node_ids → enrich → combine weights

AFTER:
  _search_lexical  → doc scores + grep line data (NO objects)
  _search_semantic → doc scores + chunk byte ranges + scores (unchanged)
  _search_candidates → merge doc scores → _score_objects(top docs, grep lines, chunks)
                       → emit docs + evidence-bearing objects only
```

### Phase 1: Document Scoring (unchanged modules)

**`_search_lexical`** returns:
- `(doc_id, lex_score, lex_rank, rrf_lex)` — document-level lexical scores
- Removes: object expansion CTE, obj_scored, all_candidates UNION

**`_search_semantic`** returns:
- `(node_id, doc_id, sem_score, sem_rank, rrf_sem, chunk data)` — unchanged

Both run in parallel (DuckDB CTE evaluation). Both call `_scope_filter` independently.

**Grep line data:** `_search_lexical` changes `grep_hits` to retain line numbers:

```sql
grep_lines AS (
    SELECT n.id AS doc_id, g.line_number, g.uri
    FROM params p, grep_matches(p.keywords_lc, '**', 500) g
    JOIN node n ON n.uri = g.uri AND n.kind = 'document'
    WHERE p.keywords_empty = FALSE
)
```

This CTE is returned alongside doc scores (or exposed as a separate output — design decision below).

### Phase 2: Object Scoring (new)

Operates on the **top N merged documents** (N configurable, default ~200). Uses both lexical and semantic signals mapped to objects via span ranges.

#### Input signals

| Signal | Source | Available data |
|--------|--------|---------------|
| Grep line hits | `grep_lines` from lexical | `(doc_id, line_number)` |
| Semantic chunk scores | `_search_semantic` | `(doc_id, chunk_start_byte, chunk_end_byte, chunk_score)` |
| Symbol name | `node.uri` / `node.properties` | extracted at query time |
| Headline/structure | `node.headline`, `node.structure` | pre-computed at index time |

#### Span mapping

Each object has a span: `(document_id, start_line, end_line, start_byte, end_byte)`. Evidence maps to objects by range overlap:

```sql
-- Grep hits → objects (line range overlap)
grep_lines.line_number BETWEEN span.start_line AND span.end_line

-- Semantic chunks → objects (byte range overlap)
chunk.start_byte < span.end_byte AND chunk.end_byte > span.start_byte
```

#### Scoring model

| Signal | Score | Condition |
|--------|-------|-----------|
| Exact symbol match | 4.0 | `symbol_key = keywords` |
| Symbol contains keyword | 3.2 | `position(keywords IN symbol_key) > 0` |
| Multiple grep hits in span | 2.5 + 0.1 × count | `grep_hit_count >= 2` |
| Single grep hit in span | 2.0 | `grep_hit_count = 1` |
| Semantic chunk overlap | `max_chunk_score` | Continuous 0-1, from best overlapping chunk |
| Headline/structure match | 1.5 | `position(keywords IN headline \|\| structure) > 0` |
| No signal | not emitted | Document row provides file-level recall |

Combined object score:

```
object_score = GREATEST(
    symbol_score,
    grep_score,
    headline_score
) + 0.3 * COALESCE(max_chunk_sem, 0)
```

The semantic score is additive because it's an independent signal — an object can have both a symbol match (lexical) and a high chunk overlap (semantic). Neither subsumes the other.

#### Per-document cap

After scoring, cap objects per document at 20 (configurable). The top 100 documents by fanout produce 173K objects — capping prevents a few large files from flooding results.

```sql
QUALIFY ROW_NUMBER() OVER (
    PARTITION BY doc_id
    ORDER BY object_score DESC, span.start_line
) <= (SELECT per_doc_cap FROM params)
```

#### Emission rule

Only emit objects where `object_score > 0` (any signal present). The document row already provides file-level recall. Objects with no evidence are noise.

### Macro Structure

Two options for how to expose grep line data:

**Option A: Lexical returns doc scores only, grep lines separate**

```sql
_search_lexical → (doc_id, lex_score, ...) -- no grep data
_grep_lines     → (doc_id, line_number)    -- standalone macro
_search_candidates calls both + _search_semantic, then _score_objects
```

Pro: clean separation. Con: extra macro = extra re-evaluation risk.

**Option B: Lexical returns doc scores + grep lines as compound output**

```sql
_search_lexical → (doc_id, lex_score, ..., grep_line, grep_doc_id)
-- NULL grep columns for doc-scored rows, populated for grep rows
-- Callers filter by kind
```

Pro: one macro call. Con: mixed output shape.

**Recommended: Option A.** The grep_lines macro is cheap (190ms, no CTE chain) and separating concerns keeps each macro simple. The re-evaluation risk is low because grep_lines has no expensive dependencies.

### Semantic Chunk Data

`_search_semantic` already returns `best_chunk_index`, `best_chunk_start`, `best_chunk_end` per document. For object scoring, we need ALL chunks per document (not just the best), so we can map each object to its overlapping chunks.

Two approaches:

**A: Query document_embedding directly in _score_objects**

```sql
-- Within _score_objects, for top-N docs:
SELECT doc_id, start_byte, end_byte,
       safe_cosine(query_vec, embedding) AS chunk_score
FROM document_embedding de
WHERE de.doc_id IN (SELECT doc_id FROM top_docs)
  AND de.embedding_type = 'full'
  AND de.dim = query_dim
```

Pro: gets all chunks. Con: re-computes cosine for top-N docs' chunks (but N is small, ~200 docs × ~5 chunks = ~1000 cosine ops — negligible).

**B: Expand _search_semantic output to include all chunk scores**

Con: bloats semantic output for all callers. The current output is already efficient.

**Recommended: Option A.** The re-computation cost is negligible for 200 docs, and it keeps `_search_semantic` output clean.

### Query Vector Sharing

Both `_search_semantic` and `_score_objects` need the query embedding vector. Calling `embed_query()` twice wastes ~35ms + risks the CTE re-evaluation trap.

Solution: `_search_candidates` calls `embed_query()` once in its `base_params` CTE and passes the vector to both `_search_semantic` and `_score_objects`. This requires `_search_semantic` to accept a pre-computed vector parameter (currently it calls `embed_query` internally).

This is a moderate refactor but eliminates a redundant API call and the re-evaluation risk.

## Trade-offs

| Decision | Gave up | Got |
|----------|---------|-----|
| Objects without signal not emitted | Can't browse all objects in a matching file via search | 90%+ fewer object rows, ~1.5s faster |
| Per-doc cap of 20 | Miss the 21st-best object in a large file | Prevents 56 large files from producing 164K rows |
| Re-compute cosine for top-200 chunks | ~1000 extra cosine ops (~5ms) | Clean macro separation, no semantic output bloat |
| Separate grep_lines macro | One extra macro invocation | Clean concerns, simple CTEs, low re-evaluation risk |

## Alternatives Considered

**Pre-compute object scores at index time.** Store symbol-keyword associations or body-search indexes per object. Would eliminate query-time span joins entirely. Rejected for now: requires pipeline changes, increases index time, and the query-time approach is fast enough once row count drops 90%+. Good long-term optimization.

**Parallel grep in C#.** `Parallel.ForEach` over file reads. Tested: slower warm (thread coordination > I/O benefit when files in OS cache). Sequential grep with max_results=500 short-circuits early and is already 190ms warm.

**CROSS JOIN LATERAL for symbol pre-computation.** Pre-compute symbol key once per object to avoid duplicate UDF calls across CASE branches. Tested: 3.6s vs 2.7s — optimizer overhead in TABLE macro context outweighs the saving.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| grep_lines only finds literal keyword matches, misses semantic-only objects | Semantic chunk overlap catches objects that are semantically relevant but don't contain the exact keyword |
| Per-doc cap too aggressive for large files with many relevant objects | Cap is configurable via macro parameter; 20 is generous for most search use cases |
| Query vector sharing refactor touches _search_semantic interface | Can be done incrementally — start without vector sharing, add it as a follow-up |
| New _score_objects macro adds nesting depth | The macro operates on small data (top 200 docs × 20 objects max = 4000 rows) so re-evaluation cost is low |

## Expected Performance

| Component | Before | After |
|-----------|--------|-------|
| `_search_lexical` | 2.6s (11K docs + 100K objects) | ~0.8s (11K docs only) |
| `_score_objects` | N/A | ~0.2s (200 docs × evidence-bearing objects) |
| `_search_semantic` | 0.83s | 0.83s (unchanged) |
| Hybrid merge | ~1s | ~0.8s (fewer rows to enrich) |
| **Total search()** | **~5s** | **~2.5-3s** |
