---
description: How Markdown documents are modeled in RepoQL (nodes, edges, spans, annotations) with practical table-first queries.
documentationCategory: comprehensive
tags: [repoql, markdown, headings, code-blocks, links, spans, annotations, duckdb, sql]
---

# Markdown Representation in RepoQL (DuckDB Views, Macros, and Patterns)

This document explains how Markdown files are modeled in the RepoQL graph and how to query them directly from DuckDB. It focuses on the concrete node/edge kinds, spans, and the small set of macros/views that make Markdown results easy to explore.

## What Gets Indexed

- **Document Node**: `kind='document'`, `uri` is the container RepoURI (`file://…` or `embed://…`).
- **Artifact**: Bytes and optional text for the document (`artifact.text_content` is populated for Markdown).
- **Child Nodes** (via composition):
  - `md_heading`: Markdown heading blocks.
  - `md_code_block`: Fenced or indented code blocks.
  - `md_link`: Inline links.
- **Edges**:
  - Composition: `HAS_PART` from document → child (`is_composition=true`, `ordinal` set for file order).
  - Reference: `REFERS_TO` from `md_link` → target `md_heading` for intra‑document `#anchor` links (slug match).
- **Spans**: 
  - For headings, code blocks, and links, a `span` is created and assigned to the node (`node.span_id`).
  - Span fields include lines and columns (1‑based) and byte range (UTF‑8), enabling snippets and precise locations.

## Properties

- **`md_heading`**: JSON properties include
  - `level`: heading level (1..6)
  - `text`: raw heading text
  - `slug`: lowercased, ASCII slug used for anchor matching
- **`md_code_block`**: JSON properties include
  - `language`: fenced info string (or empty for indented blocks)
  - `fenced`: `true` for fenced, `false` for indented
  - `lines`: number of lines in the block
- **`md_link`**: JSON properties include
  - `href`: link destination (may be `#anchor`)
  - `title`: optional title
  - `text`: rendered link label

## Markdown‑Specific Annotation

- **Outline Annotation**: The single‑writer emits a best‑effort outline for Markdown documents.
  - `annotation.kind='outline'`, `severity='info'`, `source='markdown-parser'`.
  - `message`: lines with the document URI on the first line, then an indented list of headings (indent scaled by `level`).
  - `data`: `{ "lines": [ ... ] }` containing the same list.
  - Scope: `scope_document_id` is the document. No explicit target node/span is set.
  - Query via `annotations_for(document_uri, 'outline', 'info')` or by selecting from `annotations` view and filtering `kind='outline'`.

## Helpful Macros and UDFs

- **`snippet(uri, context_lines)`**: Returns a focused window of lines for a document and span (driven by `#line=` or `#edge=` fragment).
- **`annotations_for(u, kinds, min_severity)`**: Returns annotations scoped to the document for given kinds/severity.
- **`entities_by_uri(u)`**: Resolves line/char fragments into matching spans; also resolves `edge=` to edges.
- **`repository_uri_*` UDFs**: Extract/manipulate URIs (container, fragment, `repository_uri_join`, `fragment_from_line_range`, etc.).
- **`node_display_label(kind, properties)`**: Pick a friendly label from node properties (`text`, `name`, `slug`).

Note: `explore_*` macros are documented in Schema.md; they are not Markdown‑specific but work well to summarize documents and items.

## Query Recipes (Tables First)

- List Markdown documents (by media type or URI):
  ```sql
  SELECT n.uri, a.media_type
  FROM node n
  LEFT JOIN artifact a ON a.id = n.artifact_id
  WHERE n.kind = 'document'
    AND (lower(a.media_type) LIKE '%markdown%' OR lower(n.uri) LIKE '%.md%')
  ORDER BY lower(n.uri);
  ```

- Headings in file order with levels and text:
  ```sql
  SELECT child.properties->>'level' AS level,
         child.properties->>'text'  AS heading,
         e.ordinal
  FROM node doc
  JOIN edge e    ON e.source_node_id = doc.id AND e.is_composition = TRUE AND e.type = 'HAS_PART'
  JOIN node child ON child.id = e.destination_node_id AND child.kind = 'md_heading'
  WHERE doc.uri = $DOC_URI
  ORDER BY e.ordinal;
  ```

- Headings with precise locations (spans):
  ```sql
  SELECT s.start_line, s.end_line,
         child.properties->>'level' AS level,
         child.properties->>'text'  AS heading
  FROM node doc
  JOIN edge e     ON e.source_node_id = doc.id AND e.is_composition = TRUE AND e.type = 'HAS_PART'
  JOIN node child ON child.id = e.destination_node_id AND child.kind = 'md_heading'
  JOIN span s     ON s.id = child.span_id
  WHERE doc.uri = $DOC_URI
  ORDER BY s.start_line;
  ```

- Code blocks with language and span:
  ```sql
  SELECT child.properties->>'language' AS lang,
         child.properties->>'lines'    AS lines,
         s.start_line, s.end_line
  FROM node doc
  JOIN edge e     ON e.source_node_id = doc.id AND e.is_composition = TRUE AND e.type = 'HAS_PART'
  JOIN node child ON child.id = e.destination_node_id AND child.kind = 'md_code_block'
  LEFT JOIN span s ON s.id = child.span_id
  WHERE doc.uri = $DOC_URI
  ORDER BY COALESCE(s.start_line, 2147483647), e.ordinal;
  ```

- Intra‑document references (links to headings):
  ```sql
  SELECT src.properties->>'text'  AS link_text,
         src.properties->>'href'  AS href,
         dst.properties->>'text'  AS heading
  FROM node doc
  JOIN edge part  ON part.source_node_id = doc.id AND part.is_composition = TRUE AND part.type = 'HAS_PART'
  JOIN node src   ON src.id = part.destination_node_id AND src.kind = 'md_link'
  JOIN edge ref   ON ref.source_node_id = src.id AND ref.type = 'REFERS_TO'
  JOIN node dst   ON dst.id = ref.destination_node_id AND dst.kind = 'md_heading'
  WHERE doc.uri = $DOC_URI
  ORDER BY part.ordinal;
  ```

- Read full Markdown text:
  ```sql
  SELECT a.text_content
  FROM node n
  JOIN artifact a ON a.id = n.artifact_id
  WHERE n.kind = 'document' AND n.uri = $DOC_URI;
  ```

- Snippet around a heading (using its start line):
  ```sql
  WITH hs AS (
    SELECT s.start_line
    FROM node doc
    JOIN edge e     ON e.source_node_id = doc.id AND e.is_composition = TRUE AND e.type = 'HAS_PART'
    JOIN node h     ON h.id = e.destination_node_id AND h.kind = 'md_heading'
    JOIN span s     ON s.id = h.span_id
    WHERE doc.uri = $DOC_URI AND h.properties->>'text' = $HEADING
    LIMIT 1
  )
  SELECT *
  FROM hs, LATERAL snippet(repository_uri_join($DOC_URI, 'line=' || CAST(hs.start_line AS VARCHAR)), 3);
  ```

- Outline annotation lines (writer‑generated):
  ```sql
  SELECT message, data
  FROM annotations_for($DOC_URI, 'outline', 'info');
  ```

## Notes and Trade‑offs

- Headings, code blocks, and links are indexed as separate nodes to enable structural queries and precise locations.
- The Markdown outline annotation is intentionally simple and best‑effort; it is not a replacement for structured queries above.
- `REFERS_TO` edges are only created for intra‑document `#anchor` links that resolve to a heading slug; external links are represented as `md_link` nodes without reference edges.

## Testing Considerations

- Verify that Markdown docs produce:
  - `document` node with `artifact_id` and `artifact.text_content` present.
  - `md_heading`, `md_code_block`, and `md_link` children connected by `HAS_PART` with increasing `ordinal`.
  - `span` rows for children with correct line/byte mapping (sample headings and code fences).
  - `REFERS_TO` edges for `#anchor` links that match heading slugs.
  - Outline annotation row (`kind='outline'`, `source='markdown-parser'`) scoped to the document.
- Assert that queries above return expected rows for both `file://…` and `embed://…` documents.

$DOC_URI is a parameter placeholder; substitute with a real RepoURI like `embed:///Docs/Primer.md` or `file:///path/to/README.md`.
