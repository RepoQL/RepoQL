---
description: SQL macro and UDF reference for query tool users - signatures, parameters, and usage examples
tags: [search, snippet, xray, ask, annotations, macros, UDFs]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# SQL Reference

Macros and UDFs for the query tool. Use `repoql-docs:///quickstart.md` for SQL patterns; this covers function signatures.

---

## Search

### search()

Semantic + lexical search across documents. Primary discovery tool.

```sql
search(
    keywords,                    -- Search terms (required)
    scope := NULL,               -- URI LIKE pattern: 'file:///src/%'
    boost_pattern := NULL,       -- Regex to boost: 'auth|jwt|token'
    negative_pattern := NULL,    -- Regex to de-rank: '(?i)test|mock'
    k := 200,                    -- Max results
    sem_threshold := 0.35,       -- Min semantic score for tier 1
    bm25_threshold := 0.10,      -- Min BM25 score for tier 2
    derank_factor := 0.5,        -- Penalty multiplier for negative matches
    enable_body_rescue := FALSE  -- Scan full text (expensive)
)
```

**Returns**: `uri, headline, structure, source, sem_score, bm25_score, struct_mentions, body_mentions, deranked, score`

**Examples**:
```sql
-- Basic search
SELECT uri, score FROM search('authentication') LIMIT 10;

-- Scoped to directory
SELECT uri, score FROM search('config', scope := 'file:///src/api/%');

-- Boost specific patterns, exclude tests
SELECT uri, score FROM search('parser',
    boost_pattern := 'markdown|yaml',
    negative_pattern := '(?i)test|spec'
);
```

**Depth**
- `source`: tier origin - 'semantic', 'bm25', 'outline', 'body'
- `boost_pattern`: derived from keywords if not provided (space → OR)
- Keywords ending with `?` skip boost derivation (questions make bad regex)

---

### _search_candidates()

Internal low-level search. Returns richer metadata including objects (functions, classes).

```sql
_search_candidates(
    q,                    -- Query text
    mode := 'auto',       -- 'auto', 'symbol', 'error', 'heavy'
    k := 50,              -- Max results
    uri_glob := NULL,     -- Glob filter
    mime_glob := NULL     -- MIME type filter
)
```

**Returns**: `uri, scope, kind, symbol, headline, structure, snippet, bm25_score, dense_score, score, confidence`

**Example**:
```sql
-- Find specific symbol
SELECT uri, symbol, kind, line_start
FROM _search_candidates('ProcessRequest', k := 10)
WHERE scope = 'object';
```

**Depth**
- `scope`: 'document' or 'object'
- `mode=symbol`: optimized for exact symbol lookup
- Objects inherit semantic score from parent document

---

### related()

Find documents similar to a seed URI.

```sql
related(
    seed_uri,             -- Starting document URI
    k := 20,              -- Max results
    mode := 'mixed',      -- Scoring mode
    uri_glob := NULL,     -- URI filter
    mime_glob := NULL     -- MIME filter
)
```

**Example**:
```sql
SELECT uri, score FROM related('file:///src/auth/login.cs', k := 10);
```

---

## Content Retrieval

### snippet()

Extract lines from a document with context around a focal point.

```sql
snippet(uri, context_lines)
```

**Parameters**:
- `uri`: Document URI with optional fragment (`#line=42`, `#symbol=Foo`)
- `context_lines`: Lines before/after focal point

**Returns**: `line_number, text, is_focus, focus_start_column, focus_end_column, language, document_uri, resolved_uri`

**Examples**:
```sql
-- Lines around line 42
SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);

-- Symbol location
SELECT line_number, text FROM snippet('file:///src/lib.cs#symbol=ProcessRequest', 5);

-- Entire file (no fragment)
SELECT line_number, text FROM snippet('file:///README.md', 0);

-- Compose with search
SELECT s.uri, sn.line_number, sn.text
FROM search('error', k := 5) s,
     LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

**Depth**
- Fragment types: `#line=N`, `#line=N,M`, `#symbol=Name`, `#char=N,M`
- `is_focus`: TRUE for lines within the focal range
- No fragment → entire file

---

## X-Ray

### xray()

Token-budgeted codebase exploration. Returns pre-rendered text fitting the token budget.

```sql
xray(
    keywords,                -- Search terms
    intent := 'Explore',     -- 'Find', 'Explore', 'Understand'
    tokens := 1000,          -- Token budget for output
    scope := NULL,           -- URI glob filter
    boost := NULL,           -- Regex to boost
    penalize := NULL         -- Regex to de-rank
)
```

**Returns**: Text summary with ranked files, symbols, and structure details.

**Example**:
```sql
SELECT xray('authentication', intent := 'Find', tokens := 500);
```

**Depth**
- `Find`: Locate specific code/symbols
- `Explore`: Understand structure and relationships
- `Understand`: Deep analysis of how something works
- Output scales to token budget - more tokens = more detail

---

## Annotations

### annotations_for()

Diagnostics for a specific document.

```sql
annotations_for(uri, kinds, min_severity)
```

**Parameters**:
- `uri`: Document URI
- `kinds`: Comma-separated filter: `'lint,diagnostic'` or NULL for all
- `min_severity`: Minimum level: `'error'`, `'warning'`, `'info'`, `'hint'`

**Example**:
```sql
-- All errors in a file
SELECT rule_id, message, resolved_target_uri
FROM annotations_for('file:///src/api.cs', NULL, 'error');

-- Lint warnings only
SELECT * FROM annotations_for('file:///src/lib.cs', 'lint', 'warning');
```

---

### annotations (view)

Pre-joined annotation data across all documents.

```sql
SELECT * FROM annotations WHERE severity = 'error';
```

**Columns**: `id, kind, severity, source, message, rule_id, data, scope_document_id, target_node_id, resolved_target_uri, severity_rank, created_at`

---

## LLM Functions

### ask()

Ask a question about query results using LLM.

```sql
ask(json_data, question, max_tokens := 500)
```

**Parameters**:
- `json_data`: JSON array of result rows
- `question`: What you want to understand
- `max_tokens`: Approximate response length

**Example**:
```sql
WITH results AS (
    SELECT uri, headline, structure
    FROM search('authentication', k := 10)
)
SELECT ask(
    (SELECT json_group_array(json_object('uri', uri, 'headline', headline)) FROM results),
    'How is authentication implemented?',
    300
);
```

**Depth**
- Requires `OPENROUTER_API_KEY` environment variable
- Returns helpful message if LLM not configured

---

### llm_extract()

LLM-powered code extraction with snippet access.

```sql
llm_extract(json_data, intent)
```

**Example**:
```sql
WITH results AS (
    SELECT uri, headline FROM search('error handling', k := 5)
)
SELECT llm_extract(
    (SELECT json_group_array(json_object('uri', uri, 'headline', headline)) FROM results),
    'Show me the main error handling patterns'
);
```

---

## Diagnostics

### embed_status()

Embedding provider status.

```sql
SELECT embed_status();
```

**Returns**: Provider type, enabled status, model name, dimension.

---

### indexing_diagnostics()

Current indexer status as text.

```sql
SELECT indexing_diagnostics();
```

---

### indexing_queue()

Pending indexing items as JSON array.

```sql
SELECT indexing_queue();
```

---

## Utility Functions

### glob_match()

Test if a path matches a glob pattern.

```sql
glob_match(path, pattern)
```

**Example**:
```sql
SELECT uri FROM node
WHERE kind = 'document' AND glob_match(uri, '**/*.md');
```

---

### embed_text()

Embed text and return JSON array of floats.

```sql
SELECT embed_text('authentication flow')::FLOAT[] AS embedding;
```

---

## Patterns

### Search + Snippet Composition
```sql
SELECT s.uri, sn.line_number, sn.text
FROM search('config', k := 5) s,
     LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus
ORDER BY s.score DESC, sn.line_number;
```

### Error Summary
```sql
SELECT n.uri, count(*) AS errors
FROM node n
JOIN annotation a ON a.scope_document_id = n.id
WHERE a.severity = 'error'
GROUP BY n.uri
ORDER BY errors DESC;
```

### Symbol Lookup
```sql
SELECT uri, symbol, line_start
FROM _search_candidates('ClassName', mode := 'symbol', k := 10)
WHERE scope = 'object' AND kind LIKE '%class%';
```

---

## See Also

- `repoql-docs:///quickstart.md` - SQL patterns and capsules
- `repoql-docs:///advanced-search.md` - Search scoring details
- `repoql-docs:///formats/csharp.md` - C# specific views and queries
- `repoql-docs:///formats/markdown.md` - Markdown specific views
