# Advanced Search (Terse)

Search = lexical + semantic. Two macros. Documents OR objects.

- `file_search(keywords, question := NULL, k := 50)` → `uri, score, bm25n, fuzzn, semn` (documents only)
- `search(q, mode := 'auto', k := 50, uri_glob, mime_glob)` → `uri, symbol, scope, kind, score, bm25_score, fuzzy_score, dense_score` (documents + objects)

## Scope

- `scope = 'document'` → whole files (URIs: `file:///path/to/file.cs`)
- `scope = 'object'` → functions, classes, headings, etc. (URIs: `file:///lib.cs#symbol=Foo.Bar&line=12,20`)

Use `search()` + WHERE scope to control granularity. Use `file_search()` for file-only results.

## Quick Use

```sql
-- Find whole files by intent
SELECT uri, score, semn
FROM file_search('', question := 'Where are mermaid diagram classes defined?', k := 10);

-- Find functions/classes/symbols (objects only)
SELECT uri, symbol, kind, score
FROM search('authentication token', k := 20)
WHERE scope = 'object'
ORDER BY score DESC;

-- Find documents OR objects, mixed
SELECT uri, scope, symbol, kind, score
FROM search('embedding runtime', k := 30)
ORDER BY score DESC;

-- Symbol exact match boost
SELECT uri, symbol, line_start, line_end, bm25_score
FROM search('ProcessRequest', k := 10)
WHERE scope = 'object' AND symbol IS NOT NULL
ORDER BY bm25_score DESC;  -- Exact symbol match = 4.0 points

-- URI glob filter (documents only when glob provided)
SELECT uri, score
FROM search('embeddings', uri_glob := 'src/**/*.cs', k := 20);

-- Headings from top file hits
WITH s AS (
  SELECT doc_id, uri, ROW_NUMBER() OVER (ORDER BY score DESC) rn
  FROM file_search('mermaid graph', question := 'Show class diagrams', k := 5)
)
SELECT s.uri, h.text, h.level, h.start_line
FROM s JOIN markdown_headings h ON h.file_uri = s.uri
WHERE s.rn <= 3
ORDER BY s.rn, h.level, h.start_line;

-- Semantic-first ordering with null handling
SELECT uri, symbol, dense_score, score
FROM search('Why does the embedding runtime broadcast error?', k := 20)
WHERE scope = 'object'
ORDER BY dense_score DESC NULLS LAST;
```

## Scoring

Three signals combined:
- `bm25_score` → exact/substring match (symbol=query: 4.0, symbol contains: 3.2, basename: 3.0)
- `fuzzy_score` → subsequence match via `match_score()`
- `dense_score` → cosine similarity (embedding, per-object if scope='object')

Default weights: 45% BM25, 35% fuzzy, 20% semantic (auto-adjusted by query type).

## Notes

- `scope='object'` enables sub-file search: functions, classes, headings, etc.
- `uri_glob` forces `scope='document'`; cannot filter objects by path.
- `semn` may be NULL briefly after startup; embeddings fill progressively.
- Objects get 5% score boost vs. documents when both match.
- Tests downranked 0.7x; docs upranked 1.2x for semantic queries.
