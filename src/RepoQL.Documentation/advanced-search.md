# Advanced Search (Terse)

Search = lexical + semantic. One macro. Two inputs.

- `file_search(keywords, question := NULL, k := 50, max_cand := 5000)` → `uri, score, bm25n, fuzzn, semn`

Quick use
```sql
-- Top files by intent (question only)
SELECT uri, score, semn
FROM file_search('', 'Where are mermaid diagram classes defined?', k := 10);

-- Semantics-first view combining literals + question
SELECT uri, semn, score
FROM file_search('embedding runtime', 'Why is there a broadcast error?', k := 20)
ORDER BY semn DESC NULLS LAST;

-- Filter by file type/location
WITH r AS (
  SELECT doc_id, uri, score FROM file_search('frontmatter docs', NULL, k := 50)
)
SELECT r.uri, r.score
FROM r JOIN document_search ds USING (doc_id)
WHERE lower(ds.basename) LIKE '%.md' AND lower(ds.dirname) LIKE '%/docs%';

-- Headings for top hits
WITH s AS (
  SELECT doc_id, uri, ROW_NUMBER() OVER (ORDER BY score DESC) rn
  FROM file_search('mermaid graph', 'Show class diagrams', k := 10)
)
SELECT s.uri, h.level, h.text
FROM s JOIN markdown_headings h ON h.document_uri = s.uri
WHERE s.rn <= 3
ORDER BY s.rn, h.level, h.start_line;
```

Notes
- Intent-only: write what you want in `question`; set `keywords := ''` if you do not have literals.
- k controls breadth; ORDER BY lets you favor what you care about.
- Compose with joins (e.g., `document_search`) to facet by path, kind, or extension.
- semn may be NULL briefly after startup; semantics fill in progressively.
