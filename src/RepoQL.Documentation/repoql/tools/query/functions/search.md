---
description: "search() for documents, search_symbol() for objects within files. Hybrid semantic+lexical search."
tags: ["search", "search_symbol", "semantic", "lexical", "bm25", "embeddings", "hybrid", "symbol"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Search Functions

`search()` - document-level hybrid search combining semantic embeddings and lexical matching.
`search_symbol()` - object-level search for functions, classes, methods, and other code entities.

---

## Capsule: SearchBasic

**Invariant**
`search(keywords, k)` returns documents ranked by combined semantic and lexical score.

**Example**
```sql
SELECT uri, score FROM search('authentication', k := 20);
SELECT uri, headline, score FROM search('error handling', k := 10);
```
//BOUNDARY: Returns documents (files), not individual symbols. Use `snippet()` to get code context.

**Depth**
- `keywords`: Search terms (required)
- `k`: Max results (default 200)
- Results sorted by `score` descending
- Each result includes `uri`, `headline`, `structure`, and score components

---

## Capsule: SearchScoring

**Invariant**
Final score combines semantic similarity (`sem_score`) and lexical matching (`bm25_score`).

**Example**
```sql
SELECT uri, sem_score, bm25_score, score
FROM search('JWT token refresh', k := 10);
```
//BOUNDARY: `sem_score` NULL means embeddings not yet loaded. Search still works via lexical matching.

**Depth**
- **sem_score**: Embedding cosine similarity (0-1)
  - Query embedded at runtime
  - Compared against pre-computed document embeddings
- **bm25_score**: Lexical matching score
  - Matches in headline and structure weighted higher
- **source**: Origin tier - 'semantic', 'bm25', 'outline', 'body'
- **score**: Combined final ranking score

---

## Capsule: SearchFiltering

**Invariant**
`scope` filters by URI pattern; `boost_pattern`/`negative_pattern` adjust ranking.

**Example**
```sql
-- Filter to directory
SELECT uri, score FROM search('config', scope := 'file:///src/api/%', k := 20);

-- Boost pattern
SELECT uri, score FROM search('auth', boost_pattern := '(?i)service|handler', k := 20);

-- Negative pattern (demote tests)
SELECT uri, score FROM search('parser', negative_pattern := '(?i)test|mock', k := 20);

-- Combined
SELECT uri, score FROM search('validation',
    scope := 'file:///src/%',
    boost_pattern := 'input|form',
    negative_pattern := '(?i)test'
, k := 15);
```
//BOUNDARY: `scope` uses SQL LIKE pattern (% wildcard), not glob syntax.

**Depth**
- `scope`: URI LIKE pattern (e.g., `'file:///src/%'`)
- `boost_pattern`: Regex to boost matching results
- `negative_pattern`: Regex to demote matching results
- `derank_factor`: Penalty multiplier for negative matches (default 0.5)

---

## Capsule: SearchThresholds

**Invariant**
Threshold parameters control minimum scores for inclusion in results.

**Example**
```sql
-- Stricter semantic threshold
SELECT uri, score FROM search('architecture', sem_threshold := 0.5, k := 10);

-- Lower BM25 threshold for broader results
SELECT uri, score FROM search('config', bm25_threshold := 0.05, k := 30);
```
//BOUNDARY: Higher thresholds = fewer but more relevant results.

**Depth**
- `sem_threshold`: Min semantic score for tier 1 (default 0.35)
- `bm25_threshold`: Min BM25 score for tier 2 (default 0.10)
- `enable_body_rescue`: Scan full text when other methods fail (default FALSE, expensive)

---

## Capsule: ResultColumns

**Invariant**
Search returns document identity, pre-computed summaries, and score breakdown.

**Example**
```sql
SELECT
    uri,              -- Document URI (file:///path/to/file.cs)
    headline,         -- One-line summary
    structure,        -- Detailed outline
    source,           -- Match tier: 'semantic', 'bm25', 'outline', 'body'
    sem_score,        -- Semantic similarity (0-1, may be NULL)
    bm25_score,       -- Lexical match score
    struct_mentions,  -- Matches in structure
    body_mentions,    -- Matches in body
    deranked,         -- TRUE if negative_pattern matched
    score             -- Combined final score
FROM search('query', k := 20);
```
//BOUNDARY: Use `headline` and `structure` to understand results without reading files.

---

## Capsule: SearchComposition

**Invariant**
Compose `search()` with `snippet()` using LATERAL to get code context.

**Example**
```sql
-- Search + code preview
SELECT s.uri, sn.line_number, sn.text
FROM search('error handling', k := 5) s,
     LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus
ORDER BY s.score DESC, sn.line_number;

-- Search + annotations
SELECT s.uri, s.score, a.message
FROM search('validation', k := 10) s
LEFT JOIN annotation a ON a.resolved_target_uri = s.uri
WHERE a.severity = 'error';
```
//BOUNDARY: LATERAL = for each search result, invoke snippet with that result's URI.

**Depth**
- `snippet(uri, context_lines)` returns lines around matches
- Compose with annotations, edges, or format-specific views
- Avoids multiple round-trips by joining in one query

---

## Capsule: Related

**Invariant**
`related(seed_uri, k)` finds documents similar to the seed.

**Example**
```sql
SELECT uri, score FROM related('file:///src/Auth.cs', k := 10);
SELECT uri, score FROM related('file:///docs/API.md', k := 5);
```
//BOUNDARY: "More like this" query. Uses seed's embedding for similarity.

**Depth**
- Seed must be a valid indexed URI
- Combines embedding similarity with lexical fallback
- Excludes the seed from results
- Supports `uri_glob` and `mime_glob` filters

---

## Capsule: SearchSymbol

**Invariant**
Symbol search returns code objects (classes, methods, functions) ranked by name match with location.

**Example**
```sql
SELECT symbol, uri FROM search_symbol('ValidateToken');
SELECT symbol FROM search_symbol('Service', kind_filter := 'type', scope := 'src/**/*.cs');
```
//BOUNDARY: Returns objects within files, not files themselves.

**Depth**
- Distinction: `search()` finds documents; `search_symbol()` finds entities within documents.
- `scope` uses glob syntax (`**/*.cs`), not LIKE patterns.
- `kind_filter` matches substring: `'type'` matches `csharp.type`, `ts.interface`.
- Returns: `uri`, `symbol`, `kind`, `headline`, `line_start`, `line_end`, `score`, `confidence`.
- SeeAlso: `search`, `glob_files`.

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find files by keyword | `SELECT uri, score FROM search('auth', k := 20)` |
| Files in directory | `SELECT uri FROM search('handler', scope := 'file:///src/api/%', k := 15)` |
| Exclude tests | `SELECT uri FROM search('service', negative_pattern := '(?i)test', k := 20)` |
| Boost specific terms | `SELECT uri FROM search('config', boost_pattern := 'database\|redis', k := 15)` |
| Find similar files | `SELECT uri FROM related('file:///src/Auth.cs', k := 10)` |
| Search + code context | `SELECT s.uri, sn.text FROM search('error', k := 5) s, LATERAL snippet(s.uri, 2) sn WHERE sn.is_focus` |
| Markdown files only | `SELECT uri FROM search('setup', scope := '%.md', k := 10)` |
| High-confidence only | `SELECT uri FROM search('critical', sem_threshold := 0.5, k := 10)` |
| Find a symbol by name | `SELECT symbol, uri FROM search_symbol('ValidateToken')` |
| Find types only | `SELECT symbol, uri FROM search_symbol('Service', kind_filter := 'type')` |
| Symbols in directory | `SELECT symbol FROM search_symbol('Handler', scope := 'src/api/**')` |
| Exclude test symbols | `SELECT symbol FROM search_symbol('Test', scope := 'src/**;!**/tests/**')` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `search('auth') LIMIT 10` | Use `k := 10` parameter, not LIMIT |
| `scope := 'src/**/*.cs'` in search() | Use LIKE pattern: `scope := 'file:///src/%.cs'` |
| `sem_score IS NULL` | Normal during startup; embeddings load progressively |
| Using search() for symbols | Use `search_symbol()` for classes, methods, functions |
| `ORDER BY score` | Results already sorted; omit unless re-ordering |
| Very broad query | Add `scope` filter or increase specificity |
| Using LIKE in search_symbol() scope | Use glob: `scope := 'src/**/*.cs'` (not `'file:///src/%.cs'`) |
