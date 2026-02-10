---
description: "pdf_bookmarks → outline navigation. pdf_form_fields → fillable fields. pdf_annotations → comments, highlights, stamps. Page-addressed text via #page=N fragments."
tags: ["pdf", "document", "bookmarks", "forms", "annotations", "binary"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# PDF Format

Query PDF document structure, bookmarks, form fields, and annotations with SQL views. Text extracted via layout analysis with page-level byte offsets for fragment addressing.

---

## Capsule: PdfText

**Invariant**
PDF text lives in artifact `text_content`, extracted in reading order via layout analysis.

**Example**
```sql
-- Full extracted text
SELECT text_content FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///docs/spec.pdf';

-- Read with page addressing
read("file:///docs/spec.pdf#page=3,5", 2000)
```
//BOUNDARY: Text is reading-order, not content-stream order. Scanned PDFs with no OCR layer produce empty text.

**Depth**
- Layout analysis pipeline: word extraction → block detection → reading order
- Headers/footers auto-detected and filtered from body text
- `#page=N` or `#page=N,M` fragments address specific pages via byte offsets
- Token count reflects extracted text, not raw PDF size
- Large PDFs (>10MB or >100 pages) use reopen-per-page extraction to limit memory

---

## Capsule: PdfPages

**Invariant**
Document node properties carry per-page metadata: counts, byte offsets, token budgets.

**Example**
```sql
-- Page inventory
SELECT
    n.properties->>'page_count' AS pages,
    n.properties->>'text_page_count' AS text_pages,
    n.properties->>'image_only_page_count' AS image_only
FROM node n
WHERE n.uri = 'file:///docs/spec.pdf' AND n.kind = 'document';

-- Per-page byte offsets for fragment addressing
SELECT n.properties->'page_byte_offsets' AS offsets
FROM node n
WHERE n.uri = 'file:///docs/spec.pdf' AND n.kind = 'document';
```
//BOUNDARY: `page_byte_offsets` is a JSON array of `[start, end)` byte pairs into `text_content`.

**Depth**
- `page_byte_offsets`: JSON array, e.g. `[[0, 1200], [1200, 3400], ...]`
- `page_token_counts`: JSON array of per-page token estimates
- `image_only_page_count`: Pages with images but no extractable text
- `text_page_count`: Pages that produced text output

---

## Capsule: PdfBookmarks

**Invariant**
`pdf_bookmarks` view exposes the document outline (table of contents) as queryable rows.

**Example**
```sql
-- All bookmarks for a document
SELECT title, level, target_page, start_page, end_page
FROM pdf_bookmarks
WHERE document_uri = 'file:///docs/spec.pdf'
ORDER BY start_page;

-- Top-level outline only
SELECT title, target_page
FROM pdf_bookmarks
WHERE document_uri = 'file:///docs/spec.pdf' AND level = 1;

-- Find section by name
SELECT title, start_page, end_page
FROM pdf_bookmarks
WHERE title ILIKE '%authentication%';
```
//BOUNDARY: Not all PDFs have bookmarks. Returns empty for documents without an outline.

**Depth**
- `title`: Bookmark text
- `level`: Nesting depth (1 = top-level)
- `target_page`: Page the bookmark points to
- `start_page`, `end_page`: Computed page range (until next same-level bookmark)
- `document_uri`: Parent document URI
- `document_headline`: Parent document headline
- Bookmarks also exist as `pdf_bookmark` nodes with `HAS_PART` edges from document

---

## Capsule: PdfFormFields

**Invariant**
`pdf_form_fields` view exposes fillable form fields with current values.

**Example**
```sql
-- All form fields
SELECT field_name, field_type, value, page
FROM pdf_form_fields
WHERE document_uri = 'file:///forms/application.pdf';

-- Filled fields only
SELECT field_name, value
FROM pdf_form_fields
WHERE document_uri = 'file:///forms/application.pdf' AND value IS NOT NULL;
```
//BOUNDARY: Only AcroForm fields. XFA forms not supported.

**Depth**
- `field_name`: Field identifier (or "unnamed")
- `field_type`: Text, CheckBox, RadioButton, ComboBox, ListBox, PushButton, Signature
- `value`: Current field value (NULL if blank)
- `page`: Page number where the field appears
- PDFs with form fields get media type `application/pdf;kind=pdf.form`

---

## Capsule: PdfAnnotations

**Invariant**
`pdf_annotations` view surfaces comments, highlights, and stamps from PDF markup.

**Example**
```sql
-- All annotations
SELECT annotation_type, page, content, author
FROM pdf_annotations
WHERE document_uri = 'file:///docs/reviewed.pdf'
ORDER BY page;

-- Comments only
SELECT page, content, author, date
FROM pdf_annotations
WHERE document_uri = 'file:///docs/reviewed.pdf' AND annotation_type = 'comment';
```
//BOUNDARY: Types: `comment`, `highlight`, `stamp`. Links are edges, not annotations.

**Depth**
- `annotation_type`: `comment` (Text/FreeText), `highlight`, `stamp`
- `content`: Annotation text content (may be NULL)
- `author`: Annotation author name
- `date`: Modification date string
- Link annotations become `REFERS_TO` edges on the document node instead

---

## Capsule: PdfSummaries

**Invariant**
Artifacts carry pre-computed `headline`, `summary`, and `structure` fields — no file I/O needed.

**Example**
```sql
SELECT headline, summary, structure
FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///docs/spec.pdf' AND n.kind = 'document';
```
//BOUNDARY: Pre-computed during indexing. Zero I/O cost.

**Depth**
- **Headline**: `title | pdf.document | 2.5 MB, ~45k tok | 120 pg | 37 bookmarks | Ch1, Ch2, Ch3`
- **Summary**: Type, page counts, author, producer, PDF version, annotation/form counts
- **Structure**: Bookmark outline tree (if present) or page inventory fallback
- Title from PDF metadata when available, falls back to filename

---

## Capsule: PdfGraph

**Invariant**
PDF files create a document node with bookmark and form field children.

**Example**
```sql
-- List bookmark nodes
SELECT child.headline, child.kind
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
JOIN node child ON e.destination_node_id = child.id
WHERE doc.uri = 'file:///docs/spec.pdf'
ORDER BY e.ordinal;

-- Outbound links
SELECT e.destination_uri
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.type = 'REFERS_TO'
WHERE doc.uri = 'file:///docs/spec.pdf';
```
//BOUNDARY: Node kinds: `document`, `pdf_bookmark`, `pdf_form_field`. Annotations use the `annotation` table.

**Depth**
- `HAS_PART` edges: document → bookmarks (ordinal by position), document → form fields
- `REFERS_TO` edges: document → linked URLs
- Spans: bookmarks and form fields have spans with `start_line`/`end_line` = page numbers
- Annotations (comments, highlights, stamps) stored in `annotation` table, not as nodes

---

## Media Types

| Media Type | Kind | When |
|-----------|------|------|
| `application/pdf` | (base) | Classification input |
| `application/pdf;kind=pdf.document` | `pdf.document` | Has extractable text |
| `application/pdf;kind=pdf.scan` | `pdf.scan` | No text layer (scanned/image-only) |
| `application/pdf;kind=pdf.form` | `pdf.form` | Contains AcroForm fields |

---

## Document Properties

Properties stored on the `document` node (accessible via `node.properties`):

| Property | Type | Description |
|----------|------|-------------|
| `title` | string | PDF metadata title |
| `author` | string | PDF metadata author |
| `subject` | string | PDF metadata subject |
| `keywords` | string | PDF metadata keywords |
| `creator` | string | Authoring application |
| `producer` | string | PDF producer library |
| `created` | string | Creation date (ISO 8601) |
| `modified` | string | Modification date (ISO 8601) |
| `version` | string | PDF specification version (e.g. "1.7") |
| `page_count` | int | Total pages |
| `text_page_count` | int | Pages with extractable text |
| `image_only_page_count` | int | Pages with images but no text |
| `has_bookmarks` | bool | Outline present |
| `bookmark_count` | int | Total bookmarks |
| `has_form` | bool | AcroForm present |
| `form_field_count` | int | Total form fields |
| `has_values` | bool | Form has filled values |
| `annotation_count` | int | PDF annotations (all types) |
| `link_count` | int | URL link annotations |
| `image_count` | int | Total images across pages |
| `pages_with_images` | int | Pages containing images |
| `embedded_file_count` | int | Embedded file attachments |
| `page_byte_offsets` | JSON array | `[[start, end], ...]` byte ranges per page |
| `page_token_counts` | JSON array | Token estimates per page |

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find all PDFs | `SELECT uri, headline FROM node WHERE kind = 'document' AND headline LIKE '%pdf%'` |
| Read PDF text | `read("file:///docs/spec.pdf", 3000)` |
| Read specific pages | `read("file:///docs/spec.pdf#page=5,8", 2000)` |
| List bookmarks | `SELECT title, target_page FROM pdf_bookmarks WHERE document_uri = '...'` |
| Find section | `SELECT * FROM pdf_bookmarks WHERE title ILIKE '%intro%'` |
| List form fields | `SELECT field_name, value FROM pdf_form_fields WHERE document_uri = '...'` |
| Find comments | `SELECT page, content FROM pdf_annotations WHERE annotation_type = 'comment'` |
| Check page count | `SELECT properties->>'page_count' FROM node WHERE uri = '...' AND kind = 'document'` |
| PDFs with bookmarks | `SELECT uri FROM node WHERE kind = 'document' AND properties->>'has_bookmarks' = 'true'` |
| View structure | `SELECT structure FROM artifact a JOIN node n ON n.artifact_id = a.id WHERE n.uri = '...'` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Expecting raw PDF bytes | Text is extracted during indexing — query `text_content` or use `read()` |
| `pdf_bookmarks` empty | Not all PDFs have outlines — check `has_bookmarks` property first |
| Searching `pdf_form_fields` for XFA | Only AcroForm fields supported — XFA (XML Forms Architecture) not extracted |
| Using `pdf_annotations` for links | Links become `REFERS_TO` edges, not annotation rows |
| Page numbers 0-based | Pages are 1-based throughout — page 1 is the first page |
