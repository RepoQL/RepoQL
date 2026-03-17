# Markdown Quick Reference

## Views

```sql
markdown_headings(file_uri, heading_uri, level, text, slug, start_line, end_line)
markdown_links(file_uri, link_uri, href, link_text, link_title, start_line, end_line)
```

URIs ready for `snippet()`:
- `heading_uri` = `file_uri#slug`
- `link_uri` = `file_uri#line=N`

## Queries

```sql
-- Document outline
SELECT level, text, start_line
FROM markdown_headings
WHERE file_uri = 'file:///docs/Vision.md'
ORDER BY start_line

-- Missing H1
SELECT file_uri
FROM markdown_headings
GROUP BY file_uri
HAVING MIN(level) > 1

-- Broken links
SELECT resolved_target_uri, message
FROM annotations
WHERE rule_id = 'markdown/broken-link'

-- Search → structure
WITH hits AS (
  SELECT uri FROM file_search('docs', 'Show markdown auth patterns', k := 5)
)
SELECT h.uri, mh.level, mh.text
FROM hits h
JOIN markdown_headings mh ON mh.file_uri = h.uri
ORDER BY mh.start_line
```

## URIs

```sql
-- Resolve location
SELECT * FROM entities_by_uri('file:///docs/Schema.md#line=100')
SELECT * FROM entities_by_uri('file:///docs/Vision.md#installation')

-- Preview
SELECT line_number, text FROM snippet('file:///docs/Schema.md#line=42', 5)

-- Heading section content
SELECT text FROM snippet(heading_uri, 100)
FROM markdown_headings
WHERE file_uri = 'file:///docs/Vision.md' AND text = 'Core model'
```

## X-Ray

```sql
-- Understand without reading
SELECT headline, summary, structure
FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///docs/Vision.md'
```

## Lint Rule

`markdown/broken-link` - Unresolved `#anchors` or missing files.

```ini
# .editorconfig
[*.md]
repoql.analyzer.markdown/broken-link.severity = error
```
