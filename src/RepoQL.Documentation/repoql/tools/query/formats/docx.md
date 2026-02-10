---
description: "Word documents (.docx/.docm/.dotx) → heading tree, tables, images, comments, tracked changes. Markdown-rendered text with section-level addressing."
tags: ["docx", "word", "document", "headings", "tables", "comments", "tracked-changes"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Word Document Format

Query Word document structure — headings, tables, images, comments, tracked changes — via graph nodes and properties. Text rendered as markdown with heading markers and table descriptions.

---

## Capsule: DocxText

**Invariant**
Document text is rendered as markdown: headings with `#` markers, tables as `[Table: ...]` descriptions, footnotes in delimited sections.

**Example**
```sql
-- Full rendered text
SELECT text_content FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///docs/report.docx' AND n.kind = 'document';

-- Read with token budget
read("file:///docs/report.docx", 3000)
```
//BOUNDARY: Text is final-state: tracked insertions shown, deletions hidden. Not raw XML.

**Depth**
- Headings render as `# H1`, `## H2`, etc.
- Tables render as `[Table: Col1, Col2, Col3 (3 cols x 10 rows)]`
- Footnotes/endnotes appended in `--- Footnotes ---` / `--- Endnotes ---` sections
- Hyperlinks render as display text (URLs tracked separately as edges)
- Headers/footers captured as separate document properties, not in body text

---

## Capsule: DocxHeadings

**Invariant**
Heading nodes (`docx_heading`) form the document's section tree with style-based or heuristic detection.

**Example**
```sql
-- All headings in document order
SELECT child.headline, child.properties->>'level' AS level
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id AND child.kind = 'docx_heading'
WHERE doc.uri = 'file:///docs/report.docx'
ORDER BY e.ordinal;

-- Top-level sections only
SELECT child.headline
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id AND child.kind = 'docx_heading'
WHERE doc.uri = 'file:///docs/report.docx'
  AND child.properties->>'level' = '1'
ORDER BY e.ordinal;
```
//BOUNDARY: Two detection modes: style-based (Heading1–Heading9 + custom styles via BasedOn chain) or heuristic (bold + short + larger font).

**Depth**
- `level`: 1–9 (where 1 = top-level heading)
- `text`: Heading text content
- `paragraph_index`: 0-based position in document body
- `symbol`: Auto-generated anchor (e.g. `ExecutiveSummary`) for URI addressing
- Heuristic fallback activates only when no heading styles are found
- Custom styles resolved via `BasedOn` inheritance chain (e.g. "My Title" based on "Heading1")

---

## Capsule: DocxTables

**Invariant**
Table nodes (`docx_table`) carry column names, dimensions, and header detection.

**Example**
```sql
-- All tables with their structure
SELECT
    child.properties->>'symbol' AS table_ref,
    child.properties->'column_names' AS columns,
    child.properties->>'row_count' AS rows,
    child.properties->>'col_count' AS cols,
    child.properties->>'has_header' AS has_header
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id AND child.kind = 'docx_table'
WHERE doc.uri = 'file:///docs/report.docx'
ORDER BY e.ordinal;
```
//BOUNDARY: Layout tables (used for formatting, not data) are detected and excluded.

**Depth**
- `row_count`: Total rows including header
- `col_count`: Number of columns
- `has_header`: First row styled as header
- `column_names`: JSON array of header cell texts
- `symbol`: Anchor reference (e.g. `Table1`, `Table2`)
- Cell spans (rowspan/colspan) tracked internally but not exposed as properties

---

## Capsule: DocxImages

**Invariant**
Image nodes (`docx_image`) track embedded and linked images with accessibility metadata.

**Example**
```sql
-- All images
SELECT
    child.properties->>'alt_text' AS alt,
    child.properties->>'caption' AS caption,
    child.properties->>'content_type' AS mime,
    child.properties->>'is_embedded' AS embedded
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id AND child.kind = 'docx_image'
WHERE doc.uri = 'file:///docs/report.docx'
ORDER BY e.ordinal;

-- Images missing accessibility info
SELECT a.message, a.severity
FROM annotation a
WHERE a.source = 'repoql.formats.docx' AND a.kind = 'lint';
```
//BOUNDARY: Images without alt text AND caption produce a lint warning (`docx.image-no-alt`).

**Depth**
- `alt_text`: Alternative text for accessibility
- `caption`: Image caption text
- `content_type`: MIME type (e.g. `image/png`, `image/jpeg`)
- `is_embedded`: true if image data is in the document, false if linked
- `missing`: true if image reference is broken
- Lint annotation: `kind='lint'`, `rule_id='docx.image-no-alt'`, `severity='warning'`

---

## Capsule: DocxComments

**Invariant**
Comment nodes (`docx_comment`) capture review markup with author, anchor, and resolved status.

**Example**
```sql
-- All comments
SELECT
    child.properties->>'author' AS author,
    child.properties->>'text' AS comment_text,
    child.properties->>'resolved' AS resolved,
    child.properties->>'date' AS date
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id AND child.kind = 'docx_comment'
WHERE doc.uri = 'file:///docs/report.docx'
ORDER BY e.ordinal;

-- Unresolved comments only
SELECT child.properties->>'author' AS author, child.properties->>'text' AS text
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id AND child.kind = 'docx_comment'
WHERE doc.uri = 'file:///docs/report.docx'
  AND child.properties->>'resolved' = 'false';
```
//BOUNDARY: Anchor paragraphs track where the comment applies in the document body.

**Depth**
- `id`: Comment ID within the document
- `author`: Reviewer name
- `date`: Comment timestamp (ISO 8601)
- `text`: Comment content
- `anchor_start_paragraph`, `anchor_end_paragraph`: 0-based paragraph range
- `resolved`: true if marked as done
- `open_comment_count` on document node = unresolved comments

---

## Capsule: DocxTrackedChanges

**Invariant**
Tracked changes are reflected in final-state text (insertions shown, deletions hidden) with author tracking on the document node.

**Example**
```sql
-- Check for tracked changes
SELECT
    n.properties->>'has_tracked_changes' AS has_changes,
    n.properties->>'tracked_change_count' AS change_count,
    n.properties->'tracked_change_authors' AS authors
FROM node n
WHERE n.uri = 'file:///docs/draft.docx' AND n.kind = 'document';

-- Find documents with pending changes
SELECT uri
FROM node
WHERE kind = 'document' AND properties->>'has_tracked_changes' = 'true';
```
//BOUNDARY: Individual changes are not separate nodes. Count and authors are aggregated on the document node.

**Depth**
- `has_tracked_changes`: boolean
- `tracked_change_count`: Total insertions + deletions
- `tracked_change_authors`: JSON array of distinct author names, sorted
- Text rendering shows accepted state: inserted text included, deleted text omitted

---

## Capsule: DocxSummaries

**Invariant**
Artifacts carry pre-computed `headline`, `summary`, and `structure` fields — no file I/O needed.

**Example**
```sql
SELECT headline, summary, structure
FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///docs/report.docx' AND n.kind = 'document';
```
//BOUNDARY: Pre-computed during indexing. Zero I/O cost.

**Depth**
- **Headline**: `title | docx.document | N pg | tokens | Section1, Section2 | N open comments | tracked changes | form fields`
- **Summary**: Title, page/word/paragraph counts, heading count, top sections
- **Structure**: Indented heading tree with tables and images nested under their containing section
- Title from document core properties when available, falls back to filename

---

## Capsule: DocxGraph

**Invariant**
Word documents create a document node with heading, table, image, and comment children.

**Example**
```sql
-- All child nodes in document order
SELECT child.kind, child.headline
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id
WHERE doc.uri = 'file:///docs/report.docx'
ORDER BY e.ordinal;

-- Outbound hyperlinks
SELECT e.destination_uri
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'REFERS_TO'
WHERE doc.uri = 'file:///docs/report.docx';
```
//BOUNDARY: Node kinds: `document`, `docx_heading`, `docx_table`, `docx_image`, `docx_comment`. External links only (internal cross-refs excluded).

**Depth**
- `HAS_PART` edges: document → headings, tables, images, comments (ordinal = document order)
- `REFERS_TO` edges: document → external URLs from hyperlinks
- Spans: each child node has a span with line/byte offsets into the rendered markdown text
- Annotations: lint warnings for missing image alt text (`docx.image-no-alt`)

---

## Media Types

| Extension | Media Type | Kind |
|-----------|-----------|------|
| `.docx` | `application/docx` | `docx.document` |
| `.docm` | `application/docm` | `docx.document` |
| `.dotx` | `application/dotx` | `docx.template` |

---

## Document Properties

Properties stored on the `document` node (accessible via `node.properties`):

| Property | Type | Description |
|----------|------|-------------|
| `title` | string | Document title (core properties) |
| `author` | string | Document author |
| `created` | string | Creation date (ISO 8601) |
| `modified` | string | Last modified date (ISO 8601) |
| `last_modified_by` | string | Last editor name |
| `description` | string | Document description/subject |
| `subject` | string | Subject from core properties |
| `keywords` | string | Comma-separated keywords |
| `application` | string | Authoring application name |
| `custom_properties` | JSON | User-defined key-value properties |
| `header_text` | string | Document header content |
| `footer_text` | string | Document footer content |
| `page_count` | int | Number of pages |
| `word_count` | int | Word count (from extended properties) |
| `paragraph_count` | int | Body paragraph count |
| `heading_count` | int | Number of headings detected |
| `table_count` | int | Number of tables |
| `image_count` | int | Number of images |
| `comment_count` | int | Total comments |
| `open_comment_count` | int | Unresolved comments |
| `form_field_count` | int | Content control count |
| `has_tracked_changes` | bool | Document has tracked edits |
| `tracked_change_count` | int | Number of insertions + deletions |
| `tracked_change_authors` | JSON array | Distinct author names, sorted |

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find all Word docs | `SELECT uri, headline FROM node WHERE kind = 'document' AND headline LIKE '%docx%'` |
| Read document text | `read("file:///docs/report.docx", 3000)` |
| List headings | Query `docx_heading` nodes via `HAS_PART` edges, order by `e.ordinal` |
| Find a section | `WHERE child.kind = 'docx_heading' AND child.headline ILIKE '%intro%'` |
| List tables | Query `docx_table` nodes via `HAS_PART` edges |
| Check for comments | `SELECT properties->>'open_comment_count' FROM node WHERE uri = '...'` |
| Find tracked changes | `WHERE kind = 'document' AND properties->>'has_tracked_changes' = 'true'` |
| List change authors | `SELECT properties->'tracked_change_authors' FROM node WHERE uri = '...'` |
| Image accessibility | `SELECT * FROM annotation WHERE source = 'repoql.formats.docx' AND kind = 'lint'` |
| View structure | `SELECT structure FROM artifact a JOIN node n ON n.artifact_id = a.id WHERE n.uri = '...'` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Expecting raw OOXML | Text is rendered as markdown during indexing — query `text_content` or use `read()` |
| No headings found | Document may use manual bold formatting — heuristic detection kicks in, but results vary |
| Tracked changes as separate nodes | Changes are aggregated on document node (`tracked_change_count`, `tracked_change_authors`), not individual nodes |
| Searching for content controls | Only `form_field_count` (count) is stored — individual content control values not extracted |
| Comments vs annotations | Review comments are `docx_comment` nodes. Lint warnings are `annotation` table rows |
