---
description: "search(q, mode='auto', k=200, uri_glob, mime_glob) → uri, symbol, scope, kind, score, dense_score. Hybrid lexical+semantic search for documents and objects."
tags: ["search", "semantic", "lexical", "bm25", "embeddings", "hybrid"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# search Function

Hybrid search combining lexical matching, fuzzy subsequence, and semantic embeddings.

---

## Capsule: SearchBasic

**Invariant**
`search(q, k)` returns documents and objects ranked by combined score.

**Example**
```sql
SELECT uri, score FROM search('authentication', k := 20);
SELECT uri, symbol, kind FROM search('ProcessRequest', k := 10) WHERE scope = 'object';
```
//BOUNDARY: Returns both files (scope='document') and symbols within files (scope='object') unless filtered.

**Depth**
- `q`: Query text (keywords, phrases, questions)
- `k`: Max results (default 200)
- Results sorted by `score` descending
- Each result includes identity (`uri`, `symbol`), type (`scope`, `kind`), and scores

---

## Capsule: SearchScope

**Invariant**
`scope='document'` returns files; `scope='object'` returns functions/classes/headings.

**Example**
```sql
-- Files only
SELECT uri, score FROM search('config', k := 20) WHERE scope = 'document';

-- Functions/classes only
SELECT uri, symbol, kind, score
FROM search('ValidateToken', k := 15)
WHERE scope = 'object';

-- Mixed (default)
SELECT uri, scope, symbol, score FROM search('auth', k := 30);
```
//BOUNDARY: Object URIs include fragment: `file:///src/Auth.cs#symbol=AuthService.Validate&line=42,60`

**Depth**
- Document scope: whole files (`file:///path/to/file.cs`)
- Object scope: entities within files (classes, functions, methods, headings)
- Object URIs encode both symbol name and line range
- `uri_glob` parameter forces document scope (avoids object spam)

---

## Capsule: SearchScoring

**Invariant**
Final score combines three signals: `bm25_score` (lexical) + `fuzzy_score` (subsequence) + `dense_score` (semantic).

**Example**
```sql
SELECT uri, bm25_score, fuzzy_score, dense_score, score
FROM search('JWT token refresh', k := 10);
```
//BOUNDARY: Weights vary by query type. Auto mode: 45% BM25, 35% fuzzy, 20% semantic.

**Depth**
- **bm25_score**: Position-based lexical matching
  - Exact symbol match: 4.0 points
  - Symbol contains query: 3.2
  - Basename equals: 3.0
  - Basename contains: 2.0
  - Path contains: 1.0
- **fuzzy_score**: Subsequence matching (0-5)
  - Rewards consecutive character matches
  - Rewards word boundary matches
- **dense_score**: Embedding cosine similarity (0-1)
  - Query embedded at runtime
  - Compared against pre-computed embeddings
  - NULL if embeddings not yet loaded

---

## Capsule: SearchModes

**Invariant**
`mode` parameter controls scoring weights. Auto-detected from query when `mode='auto'`.

**Example**
```sql
-- Explicit heavy mode (semantic-first)
SELECT uri, score FROM search('Why does auth fail?', mode := 'heavy', k := 10);

-- Symbol mode (lexical-first)
SELECT uri, symbol FROM search('AuthService::Validate', mode := 'symbol', k := 10);
```
//BOUNDARY: Auto-detection uses heuristics: `::`, `.()`, questions, length.

**Depth**
| Mode | Trigger | BM25 | Fuzzy | Semantic |
|------|---------|------|-------|----------|
| `auto` | Default | 45% | 35% | 20% |
| `heavy` | Questions, >160 chars, "exception" | 20% | 0% | 80% |
| `symbol` | Contains `::`, `.()`, nested dots | 45% | 35% | 14% |
| `error` | Stack traces | 45% | 35% | 20% |

---

## Capsule: SearchFiltering

**Invariant**
`uri_glob` filters by path; `mime_glob` filters by type; `boost_pattern`/`negative_pattern` adjust ranking.

**Example**
```sql
-- Path filter (forces document scope)
SELECT uri, score FROM search('config', uri_glob := 'src/**/*.cs', k := 20);

-- MIME filter
SELECT uri, score FROM search('schema', mime_glob := '*markdown*', k := 15);

-- Boost pattern
SELECT uri, score FROM search('auth', boost_pattern := '(?i)service', k := 20);

-- Negative pattern (demote tests)
SELECT uri, score FROM search('handler', negative_pattern := '(?i)test|mock', k := 20);
```
//BOUNDARY: `uri_glob` forces `scope='document'`. Cannot glob-filter objects.

**Depth**
- `uri_glob`: Glob pattern on URI path (`src/**/*.cs`)
- `mime_glob`: Glob pattern on MIME type (`*json*`)
- `boost_pattern`: Regex to boost matching results
- `negative_pattern`: Regex to demote matching results (0.5x multiplier)

---

## Capsule: FileSearch

**Invariant**
`file_search(keywords, question, k)` searches documents only, with question-aware mode switching.

**Example**
```sql
-- Keyword search
SELECT uri, score FROM file_search('authentication config', k := 10);

-- Question search (heavy mode)
SELECT uri, score
FROM file_search('', question := 'Where are JWT tokens validated?', k := 10);

-- Combined
SELECT uri, score
FROM file_search('mermaid', question := 'Show class diagram examples', k := 5);
```
//BOUNDARY: `question` parameter triggers heavy mode (80% semantic). Wrapper around `search()`.

**Depth**
- Documents only (no objects)
- `question` appended to keywords, triggers semantic-heavy mode
- Simpler interface when you only want files

---

## Capsule: Related

**Invariant**
`related(seed_uri, k)` finds documents/objects similar to the seed.

**Example**
```sql
-- Find files similar to Auth.cs
SELECT uri, score FROM related('file:///src/Auth.cs', k := 10);

-- Find functions similar to a specific method
SELECT uri, symbol, score
FROM related('file:///src/Auth.cs#symbol=ValidateToken', k := 10);
```
//BOUNDARY: "More like this" query. Uses seed's embedding for similarity.

**Depth**
- Seed must be a valid indexed URI
- Combines embedding similarity with lexical fallback
- Supports same glob filters as `search()`
- Excludes the seed from results

---

## Capsule: ResultColumns

**Invariant**
Search returns identity, type, scores, and metadata columns.

**Example**
```sql
SELECT
    uri,           -- Full URI (file:///path or file:///path#symbol=...)
    symbol,        -- Symbol name (NULL for documents)
    scope,         -- 'document' | 'object'
    kind,          -- Node type: 'document', 'class', 'function', etc.
    headline,      -- Short summary
    bm25_score,    -- Lexical score (0-1 normalized)
    fuzzy_score,   -- Subsequence score (0-1 normalized)
    dense_score,   -- Semantic score (0-1, NULL if not embedded)
    score,         -- Combined final score
    confidence     -- Score bucket: 0.95 (high), 0.80, 0.65, 0.40 (low)
FROM search('query', k := 20);
```
//BOUNDARY: `dense_score` NULL means embeddings not yet loaded for that result.

---

## Capsule: SearchDebug

**Invariant**
`boosts_json` and `explain_json` provide debugging info.

**Example**
```sql
SELECT uri, score, boosts_json, explain_json
FROM search('auth', k := 5);
```
//BOUNDARY: JSON columns show route taken and candidate counts.

**Depth**
- `boosts_json`: `{route, uri_glob_applied, mime_glob_applied, keywords_empty}`
- `explain_json`: `{route, lex_candidates, dense_candidates, requested_mode}`
- Use to debug unexpected rankings
- Check `lex_candidates=0` if lexical matching seems broken

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find files by keyword | `SELECT uri FROM search('auth', k := 20) WHERE scope = 'document'` |
| Find functions by name | `SELECT uri, symbol FROM search('ProcessRequest', k := 10) WHERE scope = 'object'` |
| Semantic question | `SELECT uri FROM file_search('', question := 'How does caching work?', k := 10)` |
| Files in directory | `SELECT uri FROM search('handler', uri_glob := 'src/api/**', k := 15)` |
| Exclude tests | `SELECT uri FROM search('service', negative_pattern := '(?i)test', k := 20)` |
| Find similar files | `SELECT uri FROM related('file:///src/Auth.cs', k := 10)` |
| Symbol exact match | `SELECT uri, symbol FROM search('AuthService.ValidateToken', k := 5) WHERE scope = 'object' ORDER BY bm25_score DESC` |
| Markdown files only | `SELECT uri FROM search('setup', mime_glob := '*markdown*', k := 10)` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `search('auth') LIMIT 10` | Use `k := 10` parameter, not LIMIT |
| `scope = 'file'` | Use `scope = 'document'` |
| `uri_glob` with objects | `uri_glob` forces document scope; filter objects via WHERE |
| `dense_score IS NULL` | Embeddings loading; wait or use lexical columns |
| `ORDER BY score` | Results already sorted by score; omit or use `ORDER BY bm25_score` for lexical-first |
| Very broad query | Add `uri_glob` or increase specificity; broad queries dilute relevance |
