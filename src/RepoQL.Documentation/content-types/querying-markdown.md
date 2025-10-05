# Querying Markdown with RepoQL

RepoQL indexes Markdown files as part of the unified DuckDB knowledge graph. The notes below show how Markdown content is represented, which helpers exist, and common queries you can issue immediately.

## Core Schema Mapping

Markdown parsing produces the same core tables as any other format:

| Table | Key Columns | Markdown Usage |
| ----- | ----------- | --------------- |
| `artifact` | `id`, `media_type`, `text_content` | Stores the raw Markdown text. Markdown files have `media_type = text/markdown;kind=markdown.doc`. |
| `node` | `id`, `kind`, `uri`, `properties` | `kind = 'document'` for the file itself. Child nodes include `md_heading` (`properties.level/text/slug`), `md_link` (`properties.href/title/text`), and `md_code_block` (`properties.language/lines`). |
| `edge` | `source_node_id`, `destination_node_id`, `type`, `is_composition` | `HAS_PART` edges connect the document node to each Markdown item, preserving document order via `ordinal`. Intra-document anchor links add `REFERS_TO` edges. |
| `span` | `document_id`, `start_line`, `end_line`, `start_column`, `end_column` | Provides 1-based line/column ranges for each heading, link, or code block. |

For ad-hoc exploration you can join these tables directly, e.g.:

```sql
SELECT d.uri AS document_uri,
       json_extract(h.properties,'$.text') AS heading,
       s.start_line,
       s.end_line
FROM node h
JOIN edge e ON e.destination_node_id = h.id AND e.type = 'HAS_PART' AND e.is_composition
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON h.span_id = s.id
WHERE h.kind = 'md_heading'
ORDER BY d.uri, s.start_line;
```

## Markdown Views

Two convenience views are created automatically when the DuckDB schema is initialized:

### `markdown_headings`
```
CREATE OR REPLACE VIEW markdown_headings AS
SELECT
  d.uri AS document_uri,
  h.uri AS heading_uri,
  CAST(json_extract(h.properties, '$.level') AS INTEGER) AS level,
  json_extract(h.properties, '$.text') AS text,
  json_extract(h.properties, '$.slug') AS slug,
  s.start_line,
  s.end_line,
  s.start_column,
  s.end_column
FROM node h
JOIN edge e ON e.destination_node_id = h.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON h.span_id = s.id;
```

### `markdown_links`
```
CREATE OR REPLACE VIEW markdown_links AS
SELECT
  d.uri AS document_uri,
  l.uri AS link_uri,
  json_extract(l.properties, '$.href')  AS href,
  json_extract(l.properties, '$.text')  AS link_text,
  json_extract(l.properties, '$.title') AS link_title,
  s.start_line,
  s.end_line,
  s.start_column,
  s.end_column
FROM node l
JOIN edge e ON e.destination_node_id = l.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON l.span_id = s.id;
```

These views let you avoid repeating JSON extraction boilerplate when answering questions like “which files link to X?” or “where are level-1 headings missing?”

## Markdown-Specific UDFs & Macros

You can combine the views with RepoQL’s built-in helpers:

- `repository_uri_*` family (`repository_uri_file_name`, `repository_uri_join`, `repository_uri_line_start`, etc.) for manipulating document URIs and fragments.
- `media_type_base` / `media_type_kind` to filter `artifact.media_type` values.
- Table macros:
  - `snippet(uri, context_lines)` – extract a line window around a heading or link.
  - `entities_by_uri(uri)` – resolve a Markdown URI fragment back to document nodes or spans.
  - `annotations_for(uri, kinds, min_severity)` / `annotations_all(kinds, min_severity)` – surface lint results (see below).
  - `xray_documents()` / `xray_items()` / `xray_lines()` – inventory documents and the entities extracted from them.

## Useful Query Patterns

**Headings overview with counts**
```sql
SELECT level,
       COUNT(*) AS headings
FROM markdown_headings
GROUP BY level
ORDER BY level;
```

**Find documents missing an H1**
```sql
SELECT document_uri
FROM markdown_headings
GROUP BY document_uri
HAVING MIN(level) > 1;
```

**List unresolved Markdown links (using the analyzer results)**
```sql
SELECT resolved_target_uri,
       message,
       data->>'href' AS href
FROM annotations
WHERE kind = 'lint'
  AND source = 'RepoQL.Markdown'
  AND rule_id = 'markdown/broken-link';
```

**Show a snippet around a broken link**
```sql
SELECT *
FROM snippet(
       (SELECT resolved_target_uri FROM annotations
        WHERE rule_id = 'markdown/broken-link'
        LIMIT 1), 2);
```

## Current Markdown Lints

RepoQL runs analyzers after each document is indexed. Today the following Markdown rule is available:

| Rule ID | Severity (default) | Description |
| ------- | ------------------ | ----------- |
| `markdown/broken-link` | `warning` | Flags links whose anchors or target documents cannot be resolved. Entries appear in the `annotations` view with optional autofix suggestions (configurable via `.editorconfig`). |

Enable or tune a rule through `.editorconfig`, for example:
```
[*.md]
repoql.analyzer.markdown/broken-link.severity = error
repoql.analyzer.markdown/broken-link.autofix = false
```

## Putting It Together

A typical workflow is:

1. Use `xray_documents()` to find Markdown files of interest.
2. Join `markdown_headings` or `markdown_links` to inspect structure.
3. Reach for `snippet()` or `entities_by_uri()` to pull precise context.
4. Query `annotations` (or call `repoql analyze run`) to review lint feedback such as missing anchors.

These building blocks let you answer both structural and content questions about Markdown documentation without scanning raw files.
