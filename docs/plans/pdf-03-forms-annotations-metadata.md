---
description: Plan for PDF format loader — form fields, annotations, links, image detection, embedded files, and SQL views
tags: [format, pdf, plan, forms, annotations, metadata, views]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: PDF Loader — Forms, Annotations, Links, Images, and SQL Views

Implements: [PDF Format Design](../designs/current/pdf-format.md) — Form Fields, Annotations and Links, Image Detection, Graph Materialization (pdf_form_field nodes, annotation records, REFERS_TO edges), SQL Views, Cross-Cutting Concerns (embedded files)

## Scope

**Covers:**
- Form field extraction via `document.TryGetForm()` (AcroForm)
- `pdf_form_field` nodes with field_name, field_type, value, page in props
- Form kind refinement: `pdf.form` when form fields exist (takes priority over `pdf.document`)
- Filled vs blank form detection (`has_values` prop on document node)
- Annotation extraction via `page.GetAnnotations()`
- PDF comments, highlights, stamps → RepoQL `annotation` table records
- Link annotations → `REFERS_TO` edges from document to target URI
- Image detection via `page.GetImages()` — presence, dimensions, page location
- Embedded file detection via `document.Advanced.TryGetEmbeddedFiles()`
- Document metadata enrichment: annotation_count, form_field_count, embedded_file_count, embedded_file_names
- SQL views: `pdf_bookmarks`, `pdf_form_fields`, `pdf_annotations`
- `IFormatSchemaProvider` implementation for view registration
- Headline template enrichment: field count for forms
- Structure template enrichment: annotation and form field counts
- Tests for form, annotation, link, image, and view scenarios

**Does not cover:**
- `pdf_image` nodes (extension point — design defers; images lack queryable metadata)
- `pdf_table` nodes (extension point — requires Tabula integration)
- Table detection (extension point — requires `Tabula` NuGet package)
- Tagged PDF structure tree (extension point)
- Embedded file extraction as separate artifacts (extension point — only inventory for v1)
- pdftotext fallback (extension point)

## Enables

Once this exists:
- **Agents can find all PDF forms in a repository** — `SELECT * FROM pdf_form_fields WHERE field_type = 'Signature'` finds all signature fields
- **Agents can query review annotations** — `SELECT * FROM pdf_annotations WHERE annotation_type = 'comment'` finds all commented PDFs
- **Agents can find documents by what they link to** — `REFERS_TO` edges connect PDFs to referenced URLs
- **Agents can distinguish forms from documents** — headline and kind clearly signal fillable PDFs
- **Agents can find PDFs with embedded attachments** — embedded file inventory in document props
- **PDF format support is complete for v1** — all design sections implemented

This is the final plan. After this, the PDF format loader delivers everything described in the design.

## Prerequisites

- Plan: pdf-01-skeleton-text-extraction complete — loader, surface model, text extraction, materialization pipeline
- Plan: pdf-02-bookmarks-navigation complete — bookmark nodes establish the pattern for child node creation

## North Star

An agent scanning 80 PDF files should be able to answer "which ones are fillable forms?", "which have review comments?", and "which reference this URL?" without opening any of them. Forms, annotations, and links are queryable metadata, not buried inside the binary.

## Done Criteria

### Form Field Extraction
- The loader shall attempt form extraction via `document.TryGetForm()`
- For each field, the loader shall extract: field name, field type (Text, Checkbox, ComboBox, ListBox, PushButton, Signature), value (if populated), page number
- Form fields shall be stored in `PdfDocumentSurface.FormFields[]`
- Form extraction shall use a single document open (document-level feature)

### Form Kind Refinement
- When the document has form fields, the loader shall set kind to `pdf.form` (overrides `pdf.document`)
- When any form field has a non-empty value, the document node shall include `has_values: true`
- When all form field values are empty, the document node shall include `has_values: false`
- This distinguishes filled forms from blank templates in the graph

### Form Materialization
- The materializer shall create one `pdf_form_field` node per field
- Node props shall include: `field_name`, `field_type`, `value`, `page`
- The materializer shall create `HAS_PART` edges from document to each form field with ordinals preserving field order
- The materializer shall create spans for form fields: `StartLine = page, EndLine = page`

### Annotation Extraction
- The loader shall extract annotations from each page via `page.GetAnnotations()`
- PDF annotation types shall be categorized:
  - Comments, highlights, stamps → RepoQL `annotation` table records
  - Link annotations → `REFERS_TO` edges (see Links below)
  - Other annotation types (line, square, circle, etc.) → counted in `annotation_count` but not individually materialized
- For comments, highlights, and stamps, the loader shall extract: type, page, content text, author (if available), date (if available)

### Annotation Table Records
- Comments, highlights, and stamps shall be stored in the `annotation` table with:
  - `kind` = `"comment"`, `"highlight"`, or `"stamp"`
  - `severity` = `"info"`
  - `source` = `"repoql.formats.pdf"`
  - `message` = comment text, highlight context, or stamp label
  - `data` = JSON: `{ "annotation_type": "...", "page": N, "author": "...", "date": "..." }`
  - `scope_document_id` = document node ID
  - `target_span_id` = span pointing to the page (`StartLine = page, EndLine = page`)
- `Materialize()` shall return these in `Records.Annotations`
- `Records.AnnotationSources` shall include `"repoql.formats.pdf"` so the indexing engine can clean up stale annotations on re-index

### Link Extraction
- Link annotations with URLs shall become `REFERS_TO` edges from the document node to the target URI
- The edge shall use `DstUri` for the target (external targets may not be in the graph)
- Link annotations without URLs (e.g., internal page links) shall be counted but not materialized as edges

### Image Detection
- The loader shall detect images on each page via `page.GetImages()`
- For each image, the loader shall record: page number, bounding box, dimensions (width x height in samples)
- Image data shall be stored in `PdfDocumentSurface.Images[]`
- No `pdf_image` nodes for v1 — image presence is summarized on the document node as `image_count` and `pages_with_images` (count)
- Image detection runs during the per-page extraction pass (respects single-open vs reopen-per-page threshold)

### Embedded File Detection
- The loader shall check for embedded files via `document.Advanced.TryGetEmbeddedFiles()`
- Embedded file names and count shall be stored in document node props: `embedded_file_names` (string array), `embedded_file_count` (integer)
- Embedded files are not extracted as separate artifacts — inventory only

### Document Node Enrichment
- The document node props shall include: `annotation_count`, `form_field_count`, `has_form` (boolean), `has_values` (boolean for filled forms), `image_count`, `pages_with_images`, `embedded_file_count`, `embedded_file_names`

### Headline Template Enrichment
- When kind is `pdf.form`, the headline shall include field count: `23 fields`
- Example: `onboarding-form.pdf | pdf.form | 340 KB, ~1.2k tok | 4 pg | 23 fields`

### Structure Template Enrichment
- The structure template shall include annotation and form field summary lines:
  ```
  14 annotations | 0 form fields
  ```
  or for a structureless PDF:
  ```
  3 annotations (2 links, 1 highlight)
  ```

### SQL Views
- The loader shall implement `IFormatSchemaProvider`
- The schema script `Schema/pdf_views.sql` shall define three views:
  - `pdf_bookmarks` — joins bookmark nodes with document via edges and spans
  - `pdf_form_fields` — joins form field nodes with document via edges
  - `pdf_annotations` — joins annotation records with document node, filtering by `source = 'repoql.formats.pdf'`
- Views shall be registered as an embedded resource

### Tests
- Test with a PDF containing form fields (text, checkbox, combo) — verify `pdf_form_field` nodes and `pdf.form` kind
- Test with a filled form vs blank template — verify `has_values` distinction
- Test with a PDF containing comments — verify annotation table records with correct kind, source, message
- Test with a PDF containing highlights — verify annotation table records
- Test with a PDF containing link annotations — verify `REFERS_TO` edges with correct target URIs
- Test with a PDF containing images — verify `image_count` and `pages_with_images` on document node
- Test with a PDF containing embedded files — verify `embedded_file_names` and `embedded_file_count`
- Test `Records.AnnotationSources` includes `"repoql.formats.pdf"`
- Test form extraction failure — verify document still materializes with text and bookmarks from Plans 01-02
- Test annotation extraction failure — verify document still materializes without annotation records
- Test SQL views against materialized graph — verify joins produce correct results
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions

## Constraints

- **No `pdf_image` nodes** — design defers; PDF images lack alt text and captions, minimal queryable metadata. Image presence tracked as stats
- **No table detection** — design defers to Tabula integration extension point
- **No embedded file extraction** — design defers; inventory only for v1 (names and count in props)
- **Annotation source is `"repoql.formats.pdf"`** — follows `repoql.formats.{format}` convention
- **Forms and annotations use single-open** — document-level features extracted in one open; page-level annotations use the reopen-per-page pass if applicable

## References

- [PDF Format Design](../designs/current/pdf-format.md) — Form Fields, Annotations and Links, Image Detection, SQL Views, Cross-Cutting Concerns sections
- [PDF Format North Star](../north-star/formats/pdf.md) — Forms, Annotations and Links, Embedded Files sections
- Plan: pdf-01-skeleton-text-extraction — prerequisite (loader, surface model, text extraction)
- Plan: pdf-02-bookmarks-navigation — prerequisite (bookmark nodes, structure template)
- PdfPig `AcroForm` — `document.TryGetForm()`, field types
- PdfPig `Annotation` — `page.GetAnnotations()`, annotation types
- PdfPig `IPdfImage` — `page.GetImages()`, bounding boxes, dimensions
- XLSX loader schema provider (`src/Formats/RepoQL.Formats.Xlsx/XlsxLoader.cs`) — reference for `IFormatSchemaProvider` implementation
- Word loader comment extraction (`src/Formats/RepoQL.Formats.Docx/DocxLoader.cs`) — analogous pattern for out-of-band facts
- Annotation table schema (`src/RepoQL.Data.DuckDB/Schema/Tables/annotation.sql`) — field definitions

## Error Policy

Each extraction phase is independently try/caught:
- **Forms:** If `TryGetForm()` fails, no form field nodes. Kind remains `pdf.document` (not `pdf.form`). Log warning.
- **Annotations:** If `page.GetAnnotations()` fails for a page, skip that page's annotations. Other pages unaffected.
- **Links:** If a link annotation has a malformed URL, skip it. Don't create a broken `REFERS_TO` edge.
- **Images:** If `page.GetImages()` fails for a page, skip image detection for that page. Image counts may be underreported.
- **Embedded files:** If `TryGetEmbeddedFiles()` fails, set `embedded_file_count = 0`. Log warning.
- **SQL views:** View creation failures at schema registration time are logged; queries against missing views fail with standard SQL errors.

Text extraction, page addressing, bookmarks, and document metadata from Plans 01-02 remain intact regardless of failures here.
