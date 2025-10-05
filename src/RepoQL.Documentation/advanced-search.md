# Advanced Search (Terse)

Search = lexical + semantic. One name. No flags.

- `file_search(q, k := 50, max_cand := 5000)` → `uri, score` (and `bm25n, fuzzn, semn` if you want them)

Quick use
```sql
-- Top files by intent
SELECT uri, score, semn
FROM file_search('mermaid diagram classes', k := 10);

-- Semantics-first view (when ready)
SELECT uri, semn, score
FROM file_search('embedding runtime broadcast error', k := 20)
ORDER BY semn DESC NULLS LAST;

-- Filter by file type/location
WITH r AS (
  SELECT doc_id, uri, score FROM file_search('frontmatter', k := 50)
)
SELECT r.uri, r.score
FROM r JOIN document_search ds USING (doc_id)
WHERE lower(ds.basename) LIKE '%.md' AND lower(ds.dirname) LIKE '%/docs%';

-- Headings for top hits
WITH s AS (
  SELECT doc_id, uri, ROW_NUMBER() OVER (ORDER BY score DESC) rn
  FROM file_search('mermaid graph classes', k := 10)
)
SELECT s.uri, h.level, h.text
FROM s JOIN markdown_headings h ON h.document_uri = s.uri
WHERE s.rn <= 3
ORDER BY s.rn, h.level, h.start_line;
```

Notes
- Intent‑only: write what you want; the host blends signals.
- k controls breadth; ORDER BY lets you favor what you care about.
- Compose with joins (e.g., `document_search`) to facet by path, kind, or extension.
- semn may be NULL briefly after startup; semantics fill in progressively.
