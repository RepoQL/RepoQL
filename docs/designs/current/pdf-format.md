---
description: Design for PDF format support — extracting text, structure, and metadata from PDF documents using PdfPig
tags: [format, pdf, binary, design, pdfpig]
audience: { human: 45, agent: 55 }
purpose: { design: 85, flow: 15 }
---

# PDF Format — Design

## North Star

An agent should understand what a PDF document contains, how it's organized, and what it argues — without opening it. Bookmarks, headings, metadata, form fields, and annotations are all queryable from the graph. Reading a section of a PDF by page range feels like reading Markdown by heading.

**Informed by:** `docs/north-star/formats/pdf.md`
**Research:** `docs/research/pdf-parsing-libraries.md`

## Context

PDF files appear in repositories as specifications, contracts, research papers, generated reports, scanned receipts, and exported slide decks. They're opaque binaries with wildly inconsistent internal structure — some have rich bookmarks and tagged headings, others are flat scans with no text at all.

The XLSX and Word loaders established the pattern for binary formats in RepoQL. This design follows that pattern. The key challenge unique to PDF: the format makes no guarantees about text ordering, structure quality, or even the presence of extractable text. The design must handle this variance honestly.

**Key difference from Word:** Word documents have explicit heading styles. PDFs may have bookmarks, tagged structure, or nothing — the design must work at each level of structural richness. Text extraction requires layout analysis algorithms rather than simple paragraph walking.

## Constraints

| Constraint | Source |
|------------|--------|
| Single writer to DuckDB | Hard constraint — all writes through `DuckDbDataStore` |
| Frozen schema — 5 tables only | Extend via views/macros/UDFs, never new tables |
| Errors never cascade | One malformed PDF must never stop indexing |
| PdfPig (Apache 2.0) | Only viable library under license constraint. Pure .NET, no native dependencies. NuGet: `PdfPig` v0.1.13 |
| No OCR at index time | Extract existing text layers only — never fabricate text |
| Pre-1.0 library | PdfPig API may change between minor versions |

---

## Design

### Classification

The classifier refines the provisional media type (already set from `.pdf` extension at `ClassificationExtensions.cs:166`) with a semantic kind based on structure cues detected during load.

| Cue | Kind | How detected |
|-----|------|-------------|
| Has extractable text | `pdf.document` | Any page yields text via PdfPig |
| Has form fields (AcroForm) | `pdf.form` | `document.TryGetForm()` returns fields |
| No extractable text on any page | `pdf.scan` | All pages yield empty text, page count > 0 |

Kind is determined during loading, not classification. The classifier's only job: confirm the file extension is `.pdf` and return the base media type `application/pdf`. The loader refines the kind after parsing. This avoids opening the PDF twice.

```csharp
// Classifier returns:
SemanticMediaType.Create("application", "pdf")

// Loader refines to one of:
SemanticMediaType.Create("application", "pdf").WithKind("pdf.document")
SemanticMediaType.Create("application", "pdf").WithKind("pdf.form")
SemanticMediaType.Create("application", "pdf").WithKind("pdf.scan")
```

Form detection takes priority over document: a PDF with both text and form fields gets `pdf.form`. A form with filled values gets `has_values: true` in document props — this distinguishes filled forms from blank templates.

### Surface Model

The parser extracts a `PdfDocumentSurface` — a pure data model carrying everything needed for materialization. No PdfPig types escape the parser.

```
PdfDocumentSurface
├── Metadata            — title, author, subject, keywords, creator, producer, dates, version
├── Pages[]             — page number, width, height, rotation, has_text, text_rendering_modes
├── Bookmarks[]         — title, level, target page, children (tree)
├── FormFields[]        — name, type (text/checkbox/combo/list/signature), value, page
├── Annotations[]       — type, page, content text, author, date (comments, highlights, stamps)
├── Links[]             — page, URL, anchor text (link annotations — become edges)
├── Images[]            — page, bounding box, dimensions (width x height in samples)
├── PageTexts[]         — per-page extracted text (joined into Artifact.Text during materialization)
└── Stats               — page count, has_bookmarks, has_form, annotation_count, text_page_count
```

### Text Extraction Strategy

PDF text extraction is the core complexity. The design uses PdfPig's Document Layout Analysis pipeline in three layers, tried in order:

**Layer 1 — Tagged structure tree.** Check for marked content regions via `page.GetMarkedContents()`. When a PDF has a tagged structure tree (common in Word/LaTeX/Google Docs exports), the marked content provides correct reading order and semantic structure. This is the highest-quality path.

**STUB** — PdfPig's tagged PDF support is low-level. Building a high-level structure tree reader from `GetMarkedContents()` and the raw `StructTreeRoot` has unknown complexity. Issues [#391](https://github.com/UglyToad/PdfPig/issues/391) and [#873](https://github.com/UglyToad/PdfPig/issues/873) document edge cases. v1 may skip this layer and go directly to Layer 2. If so, tagged PDF support becomes the first extension point.

**Layer 2 — Layout analysis (primary path for v1).** For each page:

```csharp
var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
```

This produces text blocks in reading order.

**Layer 3 — Content order fallback.** If layout analysis produces no output (degenerate page structure), fall back to `ContentOrderTextExtractor.GetText(page)`. This is wrong for multi-column layouts but better than nothing.

**Header/footer stripping:** The `DecorationTextBlockClassifier` identifies repeating headers, footers, and page numbers across a multi-page document. This requires cross-page context — it sees all pages' text blocks to detect repetition. Therefore, text extraction always runs in two passes:

1. **Extract pass:** Open document, iterate pages, run layout analysis per page, collect text blocks per page. For large PDFs using reopen-per-page, this pass collects only the lightweight block data (text + bounding box), not the full PdfPig document state.
2. **Decoration pass:** Run `DecorationTextBlockClassifier` across all collected blocks, then strip identified decorations and assemble final per-page text.

**Scanned document detection:** During the extract pass, check each page's `Letters` collection. If all letters have `TextRenderingMode = Invisible`, the page contains OCR text (extracted normally — invisible text is still text). If a page has zero letters, it's image-only. The kind is set to `pdf.scan` when *no* page yields text.

**Text assembly and page addressing:** Per-page extracted text is stored in the `PageTexts[]` array on the surface model. During materialization, page texts are joined into `Artifact.Text` as a single string. Page byte offsets (start/end byte of each page's text within the joined string) are stored in the document node's props as `page_byte_offsets` — a JSON array of `[start, end]` pairs. The `#page=5,12` fragment is resolved at read time by slicing `Artifact.Text` using these offsets. This avoids fragile text markers that could collide with document content.

### Bookmark Extraction

```csharp
if (document.TryGetBookmarks(out Bookmarks bookmarks))
{
    // Walk bookmark tree, extract title + target page for each
}
```

Bookmarks become the primary navigational structure when present. They map directly to the outline tree shown in the north-star. When absent, the outline falls back to page inventory only.

### Form Fields

```csharp
if (document.TryGetForm(out AcroForm form))
{
    foreach (var field in form.Fields)
    {
        // field.FieldType, field.Name, field.Value
    }
}
```

Field types supported by PdfPig: `Text`, `Checkbox`, `ComboBox`, `ListBox`, `PushButton`, `Signature`. Each becomes a node with type and value in props.

### Annotations and Links

```csharp
var annotations = page.GetAnnotations();
```

PDF annotations split into two categories based on what they are in RepoQL's model:

**Comments, highlights, stamps** → RepoQL `annotation` table records. These are out-of-band facts about the document, not structural parts of it. Mapped as:

| Annotation field | Value |
|-----------------|-------|
| `kind` | `"comment"`, `"highlight"`, `"stamp"` |
| `severity` | `"info"` |
| `source` | `"repoql.formats.pdf"` |
| `rule_id` | `null` (no rule — these are document artifacts, not diagnostics) |
| `message` | Comment text, highlight context, or stamp label |
| `data` | JSON: `{ "annotation_type": "...", "page": N, "author": "...", "date": "..." }` |
| `scope_document_id` | Document node ID |
| `target_span_id` | One span per annotation, pointing to its page (`StartLine = page, EndLine = page`) |

**Link annotations** → `REFERS_TO` edges from the document node to the target URI via `DstUri`. These are references, not commentary.

### Image Detection

```csharp
foreach (var image in page.GetImages())
{
    // image.Bounds — position on page
    // image.WidthInSamples, image.HeightInSamples — original dimensions
}
```

Images are detected and their positions recorded. No image content is extracted or analyzed — just presence, location, and dimensions.

### Graph Materialization

Following the XLSX/Word pattern — state transfer via `PdfDocumentState` in `DocumentModel.Metadata`.

```csharp
internal sealed class PdfDocumentState
{
    public required PdfDocumentSurface Surface { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
```

**Artifact:**

| Field | Value |
|-------|-------|
| `Text` | Extracted body text — per-page texts joined with newlines (like Word, not like XLSX). Page boundaries tracked via `page_byte_offsets` in document props |
| `Headline` | Rendered via Liquid template |
| `Summary` | Rendered via Liquid template |
| `Structure` | Rendered via Liquid template — bookmark tree or page inventory |
| `TokenCount` | Estimated from `Text` content via `TokenEstimator` |

**Nodes:**

| Kind | What | Props |
|------|------|-------|
| `document` | Root node | title, author, subject, producer, page_count, text_page_count, has_bookmarks, has_form, annotation_count, version |
| `pdf_bookmark` | Each bookmark entry | title, level, target_page |
| `pdf_form_field` | Each form field | field_name, field_type, value, page |

**Why no `pdf_page` nodes:** Pages are spans, not entities. A page has no identity beyond its number — it's a location in the document, not a thing with properties. Page information (dimensions, has_text, image_count) lives in the document node's props as arrays indexed by page number, or in per-page `image_count` and `has_text` summary stats.

**Why no `pdf_image` nodes for v1:** Images in PDFs lack the metadata that makes nodes useful (no alt text, no captions, no titles — unlike Word images). Image presence per page is tracked as a stat on the document. If image metadata extraction becomes valuable, `pdf_image` nodes can be added without schema changes.

**Node kind naming:** Uses `pdf_` prefix (underscore-separated), matching the CSV and XLSX convention (`csv_column`, `xlsx_worksheet`). The code format convention (dot-separated: `csharp.type`) is reserved for formats with language-level semantics.

**Annotations (annotation table):**

PDF comments, highlights, and stamps are stored in the `annotation` table — they are out-of-band facts about the document, not structural parts of it. See "Annotations and Links" section above for field mapping. `Materialize()` returns these in `Records.Annotations` with `Records.AnnotationSources = ["repoql.formats.pdf"]` so the indexing engine can clean up stale annotations on re-index.

**Edges:**

| Type | From | To | Ordinal |
|------|------|----|---------|
| `HAS_PART` | document | pdf_bookmark | bookmark tree order |
| `HAS_PART` | document | pdf_form_field | field order |
| `REFERS_TO` | document | target URI (via `DstUri`) | — |

**Spans:** Bookmarks get spans with `StartLine`/`EndLine` mapping to page numbers (like XLSX uses row numbers). `StartLine = target_page`, `EndLine = next_bookmark_target_page - 1` (or last page). This enables `read("file:///spec.pdf#symbol=Authentication")` to resolve a bookmark title to a page range.

Form fields get spans with page number only (`StartLine = page, EndLine = page`). Annotations in the `annotation` table reference a `target_span_id` for their page location.

### X-Ray Templates

**Headline:**

```liquid
{{ title | default: file_name }} | {{ kind }} | {{ size_bytes | filesize }}, {{ token_count | tokens }} | {{ page_count }} pg{% if bookmark_count > 0 %} | {{ bookmark_count }} bookmark{{ bookmark_count | pluralize: "", "s" }}{% endif %}{% if top_bookmarks.size > 0 %} | {{ top_bookmarks | join: ", " }}{% endif %}{% if form_field_count > 0 %} | {{ form_field_count }} field{{ form_field_count | pluralize: "", "s" }}{% endif %}{% if kind == "pdf.scan" %} | scanned, no text layer{% endif %}
```

Examples:

```
api-spec.pdf | pdf.document | 2.4 MB, ~18k tok | 200 pg | 14 bookmarks | Introduction, Authentication, Endpoints
receipt-2023-04.pdf | pdf.scan | 84 KB, ~0.2k tok | 1 pg | scanned, no text layer
Q3-Report.pdf | pdf.document | 1.1 MB, ~8.4k tok | 42 pg | Financial Results, Risk Factors, Outlook
onboarding-form.pdf | pdf.form | 340 KB, ~1.2k tok | 4 pg | 23 fields
```

**Structure:** Bookmark tree (when present) with page ranges. Falls back to page inventory with per-page stats (has text, image count) when no bookmarks exist.

```
API Specification v3.1 (200 pg, ~18k tok)
  Author: Engineering Team | Producer: LaTeX | PDF 1.7
  Outline:
    1. Introduction (p1-4)
    2. Authentication (p5-22)
      2.1 OAuth2 Flow (p5-12)
      2.2 API Keys (p13-18)
    3. Endpoints (p23-142)
    ...
  14 annotations | 0 form fields
```

For a structureless PDF:

```
report.pdf (42 pg, ~8.4k tok)
  Author: Unknown | Producer: Scanner Pro | PDF 1.4
  No outline detected
  Pages: 42 text, 0 image-only
  3 annotations (2 links, 1 highlight)
```

### SQL Views

Embedded resource `Schema/pdf_views.sql`, registered via `IFormatSchemaProvider`.

```sql
-- pdf_bookmarks: queryable bookmark tree
CREATE OR REPLACE VIEW pdf_bookmarks AS
SELECT
    n.id,
    parent.uri AS file_uri,
    n.properties->>'title' AS title,
    CAST(n.properties->>'level' AS INTEGER) AS level,
    CAST(n.properties->>'target_page' AS INTEGER) AS target_page,
    s.start_line AS start_page,
    s.end_line AS end_page,
    parent.headline AS document_headline
FROM node n
JOIN edge e ON e.destination_node_id = n.id AND e.type = 'HAS_PART'
JOIN node parent ON parent.id = e.source_node_id AND parent.kind = 'document'
LEFT JOIN span s ON s.id = n.span_id
WHERE n.kind = 'pdf_bookmark';

-- pdf_form_fields: queryable form inventory
CREATE OR REPLACE VIEW pdf_form_fields AS
SELECT
    n.id,
    parent.uri AS file_uri,
    n.properties->>'field_name' AS field_name,
    n.properties->>'field_type' AS field_type,
    n.properties->>'value' AS value,
    CAST(n.properties->>'page' AS INTEGER) AS page,
    parent.headline AS document_headline
FROM node n
JOIN edge e ON e.destination_node_id = n.id AND e.type = 'HAS_PART'
JOIN node parent ON parent.id = e.source_node_id AND parent.kind = 'document'
WHERE n.kind = 'pdf_form_field';

-- pdf_annotations: queryable annotation inventory (from annotation table, not nodes)
CREATE OR REPLACE VIEW pdf_annotations AS
SELECT
    a.id,
    doc.uri AS file_uri,
    a.kind AS annotation_type,
    CAST(json_extract_string(a.data, '$.page') AS INTEGER) AS page,
    a.message AS content,
    json_extract_string(a.data, '$.author') AS author,
    json_extract_string(a.data, '$.date') AS date,
    doc.headline AS document_headline
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
WHERE a.source = 'repoql.formats.pdf';
```

### Token Estimation

Like Word, PDF artifacts carry extracted text. Token count is estimated from `Text` content using `TokenEstimator.EstimateTokensSafe()`. For `pdf.scan` documents with no text, token count is estimated from the rendered summary + structure (XLSX pattern).

Per-page token estimates are stored as a JSON array in the document node's props (`page_token_counts`). This enables agents to estimate the cost of reading a page range before committing.

### Memory Management

PdfPig loads the entire file into memory and does not release internal caches between pages. For large PDFs (reported: 15MB file consuming 4-6 GB memory), the reopen-per-page workaround is necessary.

**Two extraction modes:**

| Mode | When | How |
|------|------|-----|
| Single-open | File < 10 MB and < 100 pages | Open once, iterate all pages, extract everything |
| Reopen-per-page | File >= 10 MB or >= 100 pages | Extract document-level features first (single open), then reopen per page for text/images/annotations |

**Document-level features** (bookmarks, forms, metadata, embedded files, page count/dimensions) are always extracted with a single open — they don't iterate page content and don't trigger the memory issue.

**Per-page features** (text extraction, image detection, page-level annotations) use the reopen-per-page loop for large PDFs:

```csharp
for (int i = 1; i <= pageCount; i++)
{
    using var document = PdfDocument.Open(bytes);
    var page = document.GetPage(i);
    // extract text blocks, images, annotations for this page
    // collect lightweight block data for decoration classifier post-pass
}
```

The 10 MB / 100 page defaults are configurable. They're conservative — profiling against representative PDFs during implementation may shift them.

### Error Handling

| Failure | Behavior |
|---------|----------|
| Can't open PDF (corrupted, truncated) | Attempt metadata-only extraction (page count from xref table if available). If even that fails, `PipelineResult.Error` with diagnostic. Partial results beat no results |
| Password-protected / encrypted (no password) | `PipelineResult.Error` with diagnostic explaining encryption |
| Zero-page PDF (valid per spec) | Index with `pdf.document`, `page_count: 0`, empty text. Not an error |
| Page text extraction failure | Skip page, log warning, continue — other pages still extracted |
| Bookmark extraction failure | Skip bookmarks, surface document without outline |
| Form field extraction failure | Skip forms, surface document without form data |
| Annotation extraction failure | Skip annotations, surface document without annotation records |
| Image detection failure | Skip images for that page, continue |
| Layout analysis produces empty output | Fall back to content order extraction |
| File exceeds size limit (default: 200 MB) | Skip with diagnostic — PdfPig loads entire file into memory |

Each extraction phase (text, bookmarks, forms, annotations, images) is independently try/caught. A corrupt annotation stream never prevents text extraction.

**Process-fatal exceptions:** `StackOverflowException` and `AccessViolationException` cannot be caught in .NET — they terminate the process. PdfPig is pure managed .NET so `AccessViolationException` should not occur, but `StackOverflowException` from deeply recursive PDF object references is possible with malicious inputs. The file size limit is the primary defense. Future mitigation: process isolation via out-of-process extraction for untrusted PDFs.

---

## Cross-Cutting Concerns

**URI addressing:** PDF documents use `file:///path#page=5,12` for page ranges and `file:///path#symbol=BookmarkTitle` for bookmark-based navigation. Page addressing uses the `page_byte_offsets` array in document props to slice `Artifact.Text`. Symbol addressing resolves bookmark titles to page ranges via bookmark spans, using the existing symbol resolution mechanism that matches node names.

**Deferred:** The north-star shows a `#section=Authentication` fragment syntax. For v1, `#symbol=` provides the same capability via bookmark title matching. A dedicated `#section=` fragment could be added later if the semantics need to differ from symbol resolution (e.g., fuzzy matching on section titles).

**Search integration:** Extracted text in `Artifact.Text` participates in semantic search automatically. Bookmark titles, form field names, and annotation content are searchable through node headlines.

**Explore/read integration:** The `tree`, `history`, `blame` views work on PDF files like any other. The `question` view answers questions about document content using extracted text.

**Embedded file detection:** `document.Advanced.TryGetEmbeddedFiles()` checks for PDF attachments. Embedded files are recorded in the document node's props (`embedded_file_names`, `embedded_file_count`) but not extracted as separate artifacts for v1.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| PdfPig (Apache 2.0) | Docnet.Core, PDFsharp, commercial libraries | Only option: pure .NET, full feature set, commercially usable, actively maintained |
| Layout analysis (Docstrum + reading order) | Raw content stream order | Content stream order is wrong for multi-column layouts — layout analysis is correct more often |
| Text in `Artifact.Text` | `Text = null` (XLSX pattern) | PDF content is prose — agents need to read it as text, search it, navigate by page |
| Page byte offsets in props | Separate per-page storage | Enables page-range `#page=` addressing via offset array — no fragile text markers, no second storage mechanism |
| No `pdf_page` nodes | Page as a first-class entity | Pages are locations, not entities. Node overhead (N nodes per document) with minimal queryable properties |
| No `pdf_image` nodes for v1 | Image as a first-class entity | PDF images lack alt text and captions — minimal metadata to query. Image presence tracked as stats |
| No OCR | Tesseract or cloud OCR | Scope containment. OCR is slow, adds native deps, and fabricates text. Existing OCR layers are extracted |
| Reopen-per-page for large PDFs | Single open with memory risk | PdfPig leaks memory between pages. Reopen adds I/O but caps memory |
| Skip tagged PDF for v1 | Build structure tree reader | Unknown complexity, low-level API, edge cases. Layout analysis delivers usable results for most documents |

## Alternatives Considered

**Docnet.Core (PDFium wrapper):** Native PDFium for text extraction. Rejected: no bookmarks, no metadata, no forms, no annotations — missing everything except text. Also stalled since September 2023. See `docs/research/pdf-parsing-libraries.md`.

**pdftotext as primary extractor:** Process-based, GPL-safe via process isolation. Rejected as primary: no structure extraction (bookmarks, forms, annotations). Viable as a future fallback for text quality on tough PDFs.

**Kreuzberg (Rust + PDFium):** Newer entrant, MIT licensed. Rejected for v1: unverified claims, thin .NET bindings, unknown maturity. Worth evaluating if PdfPig hits limitations.

**Page nodes for every page:** Model each page as a `pdf_page` node with width, height, image_count, text_length. Rejected: creates N nodes per document with minimal queryable value. Page dimensions are rarely queried. Image presence is better surfaced as a document-level stat.

## Risks

| Risk | Mitigation |
|------|------------|
| PdfPig memory on large PDFs | Reopen-per-page above threshold; monitor in production |
| Layout analysis wrong for complex layouts | Falls back to content order; future pdftotext fallback as extension |
| PdfPig pre-1.0 API instability | Pin version; isolate PdfPig types behind surface model (no PdfPig types escape parser) |
| Scanned PDFs with no text produce thin graph | Honest headline ("scanned, no text layer"), metadata and page count still indexed |
| Bookmarks absent in most PDFs | Structure falls back to page inventory — honest about what the document provides |
| Tagged PDF support deferred | Extension point preserved; layout analysis covers most cases adequately |
| Malicious PDFs causing StackOverflow | File size limit (200 MB default); future: out-of-process extraction for untrusted sources |

## Extension Points

- **Tagged PDF structure tree:** Build a high-level reader on PdfPig's `GetMarkedContents()` for correct reading order and semantic headings when structure tree exists. Highest-leverage future improvement
- **pdftotext fallback:** Shell out to poppler `pdftotext -layout` for PDFs where PdfPig produces empty or garbled text. GPL-safe via process isolation
- **Table detection:** Add `Tabula` NuGet package (v0.1.5, built on PdfPig) for table zone detection and cell extraction
- **`pdf_image` nodes:** Add when image metadata extraction becomes valuable (e.g., if alt text or caption detection improves)
- **Page-level token estimates:** Already in design via `page_token_counts` prop — enables `#page=` budget decisions
- **Embedded file extraction:** Extract PDF attachments as separate artifacts rather than just listing them
- **`pdf_table` nodes:** After Tabula integration, surface detected tables as queryable nodes

---

## Project Structure

```
src/Formats/RepoQL.Formats.Pdf/
    PdfLoader.cs                          # IFormatLoader + IFormatMaterializer + IFormatSchemaProvider
    PdfClassifier.cs                      # IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    PdfParser.cs                          # IAsyncPipeline<IClassifiedArtifact, Records?>
    PdfDocumentState.cs                   # State transfer between Load and Materialize
    Surface/
        PdfDocumentSurface.cs             # Root surface model
        BookmarkInfo.cs                   # Bookmark tree node
        FormFieldInfo.cs                  # Form field data
        PdfAnnotationInfo.cs              # PDF annotation data (for annotation table records)
        PageInfo.cs                       # Per-page extraction results
    TextExtraction/
        PdfTextExtractor.cs              # Layout analysis pipeline (Layer 2) + decoration stripping
        PageTextAssembler.cs             # Joins per-page text, computes byte offsets
    Templates/
        explore/
            headline.liquid
            summary.liquid
            structure.liquid
    Schema/
        pdf_views.sql
    PdfServiceCollectionExtensions.cs
    RepoQL.Formats.Pdf.csproj            # References: PdfPig, RepoQL.Contracts, RepoQL.Templating

src/tests/RepoQL.Formats.Pdf.Tests/
    PdfLoaderTests.cs                     # Round-trip tests with programmatic PDF creation
    PdfTextExtractionTests.cs             # Layout analysis quality tests
    RepoQL.Formats.Pdf.Tests.csproj       # References: PdfPig (for test PDF creation), TUnit, AwesomeAssertions, FakeItEasy
```

---

*Extract the text. Preserve the outline. Contain the layout complexity. Be honest about what each document can give.*
