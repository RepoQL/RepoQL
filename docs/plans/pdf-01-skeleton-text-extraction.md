---
description: Plan for PDF format loader — project scaffolding, text extraction with layout analysis, page addressing, and basic materialization
tags: [format, pdf, plan, text-extraction, pdfpig]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: PDF Loader — Skeleton, Text Extraction, and Page Addressing

Implements: [PDF Format Design](../designs/current/pdf-format.md) — Classification, Surface Model, Text Extraction Strategy, Graph Materialization (artifact + document node), X-Ray Templates, Memory Management, Error Handling

## Scope

**Covers:**
- New project `RepoQL.Formats.Pdf` with PdfPig dependency and DI registration
- `PdfClassifier` pipeline processor
- `PdfLoader` implementing `IFormatLoader` and `IFormatMaterializer`
- `PdfDocumentSurface` with Metadata, Pages, PageTexts, Stats
- `PdfDocumentState` for state transfer between load and materialize
- Text extraction via PdfPig Document Layout Analysis pipeline (Layer 2: Docstrum + reading order)
- Content order fallback (Layer 3) when layout analysis produces nothing
- Header/footer stripping via `DecorationTextBlockClassifier` (two-pass extraction)
- Scanned document detection (invisible text layers, image-only pages)
- Kind refinement: `pdf.document`, `pdf.scan`
- Page byte offset tracking for `#page=N,M` fragment addressing
- Per-page token estimates in document props
- Single-open vs reopen-per-page memory management
- Artifact with extracted text, headline, summary, structure
- Document node with metadata and page stats
- Liquid templates for headline, summary, structure
- File size limit (200 MB default)
- Tests for core scenarios

**Does not cover:**
- Bookmark extraction (Plan: pdf-02-bookmarks-navigation)
- Form field extraction (Plan: pdf-03-forms-annotations-metadata)
- Annotation extraction (Plan: pdf-03-forms-annotations-metadata)
- Link extraction (Plan: pdf-03-forms-annotations-metadata)
- Image detection (Plan: pdf-03-forms-annotations-metadata)
- SQL views (Plan: pdf-03-forms-annotations-metadata)
- Tagged PDF structure tree (extension point — design defers)

## Enables

Once this exists:
- **Agents can discover PDF files** — `explore` finds `.pdf` files with meaningful headlines showing page count and document vs scan distinction
- **Agents can read PDFs** — `read` returns extracted text with correct reading order for most documents
- **Page-range addressing works** — `read("file:///spec.pdf#page=5,12", 3000)` returns pages 5-12
- **Semantic search covers PDF content** — extracted text in `Artifact.Text` is automatically indexed
- **Scanned PDFs are honest** — headline says "scanned, no text layer" rather than silently producing empty results
- **Large PDFs don't exhaust memory** — reopen-per-page caps memory for documents over threshold
- **Plans 2-3 can proceed** — both build on the loader, surface model, and materialization pipeline established here

This is the foundation. Every subsequent plan adds node types and enriches the surface model without changing the core structure.

## Prerequisites

- [PdfPig](https://github.com/UglyToad/PdfPig) — NuGet package `PdfPig` v0.1.13. Apache 2.0 license. Add to `Directory.Packages.props`
- `LiquidTemplateRenderer` and `StandardFilters` from `RepoQL.Templating`
- `TokenEstimator` from `RepoQL.Contracts`
- Pipeline infrastructure: `IAsyncPipeline`, `FormatDescriptor`, `AddIndexingProcessor`

## North Star

A `.pdf` file should be as discoverable as a `.md` file in explore results — page count, token estimate, document type clearly visible. An agent should know from the headline whether a PDF has extractable text or is a scan. Reading a page range should feel like reading a line range from a text file.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Pdf` shall build and be referenced from the solution
- The project shall register its services via `AddPdfFormat()` extension method following the XLSX pattern
- The `FormatDescriptor` shall declare `.pdf` as the supported extension and `application/pdf` as the media type

### Classification
- The `PdfClassifier` shall assign `application/pdf` media type for `.pdf` files
- When a file has a `.pdf` extension but PdfPig cannot open it, the classifier shall return `null`

### Kind Refinement
- When any page yields extractable text, the loader shall set kind to `pdf.document`
- When no page yields extractable text and page count > 0, the loader shall set kind to `pdf.scan`
- Kind refinement shall happen during loading, not classification (avoids opening the file twice)

### Text Extraction
- The loader shall extract text from each page using PdfPig's Document Layout Analysis pipeline:
  1. `NearestNeighbourWordExtractor.Instance` for word segmentation
  2. `DocstrumBoundingBoxes.Instance` for block detection
  3. `UnsupervisedReadingOrderDetector.Instance` for reading order
- When layout analysis produces no text blocks for a page, the loader shall fall back to `ContentOrderTextExtractor.GetText(page)`
- The loader shall run a two-pass extraction: first pass collects text blocks per page, second pass runs `DecorationTextBlockClassifier` across all pages to strip repeating headers, footers, and page numbers
- Each page's text shall be stored in `PdfDocumentSurface.PageTexts[]`

### Scanned Document Detection
- The loader shall check each page's `Letters` collection
- When all letters on a page have `TextRenderingMode = Invisible`, the page contains OCR text (extracted normally)
- When a page has zero letters, it is image-only
- The `Stats` shall track `text_page_count` (pages with extractable text) and `image_only_page_count`

### Page Addressing
- During materialization, per-page texts shall be joined into `Artifact.Text` with page breaks
- Page byte offsets (start byte, end byte for each page's text within the joined string) shall be stored in the document node's props as `page_byte_offsets` — a JSON array of `[start, end]` pairs
- The `#page=5,12` fragment shall be resolvable at read time by slicing `Artifact.Text` using these offsets
- Per-page token estimates shall be stored as a JSON array in `page_token_counts` prop

### Memory Management
- When file size < 10 MB and page count < 100, the loader shall use single-open mode (open once, iterate all pages)
- When file size >= 10 MB or page count >= 100, the loader shall use reopen-per-page mode
- In reopen-per-page mode, the extract pass shall collect lightweight block data (text + bounding box) per page, then assemble final text after all pages are processed
- The thresholds shall be configurable

### Surface Model
- `PdfDocumentSurface` shall carry: Metadata (title, author, subject, keywords, creator, producer, dates, version), Pages (per-page width, height, rotation, has_text), PageTexts (extracted text per page), Stats (page_count, text_page_count, image_only_page_count)
- `PdfDocumentState` shall carry: Surface, Digest, Size, MediaType, StoreUri
- No PdfPig types shall appear in the surface model or state

### Materialization
- The materializer shall create one artifact with `Text` set to joined per-page text
- The materializer shall create one `document` node with: title, author, subject, producer, creator, page_count, text_page_count, image_only_page_count, version, page_byte_offsets, page_token_counts
- Token count shall be estimated from `Artifact.Text` using `TokenEstimator.EstimateTokensSafe()`
- For `pdf.scan` documents with no text, token count shall be estimated from rendered summary + structure

### X-Ray Templates
- The headline template shall render: title (or filename), kind, file size, token count, page count, "scanned, no text layer" when kind is `pdf.scan`
- The structure template shall render: metadata (author, producer, version), page inventory with text/image-only counts
- The summary template shall render: document stats overview

### Error Handling
- When PdfPig cannot open the file (corrupted, truncated), the loader shall attempt metadata-only extraction. If that also fails, return `PipelineResult.Error` with diagnostic
- When the file is password-protected, the loader shall return `PipelineResult.Error` explaining encryption
- When a page's text extraction fails, the loader shall skip the page, log warning, continue with remaining pages
- When the file exceeds 200 MB, the loader shall skip with diagnostic (PdfPig loads entire file into memory)
- A zero-page PDF (valid per spec) shall be indexed with `pdf.document`, `page_count: 0`, empty text

### Tests
- Test with a simple single-page PDF containing text — verify text extraction and artifact creation
- Test with a multi-page PDF — verify page byte offsets and per-page token counts
- Test with a PDF containing no text (image-only) — verify `pdf.scan` kind and honest headline
- Test with a PDF containing invisible OCR text — verify text is extracted, kind is `pdf.document`
- Test with a multi-column layout — verify layout analysis produces correct reading order
- Test with content order fallback — verify fallback triggers when layout analysis returns nothing
- Test with an empty/zero-page PDF — verify indexed without error
- Test with a corrupted file — verify `PipelineResult.Error` with diagnostic
- Test with a password-protected PDF — verify error with encryption message
- Test with single-open vs reopen-per-page thresholds — verify correct mode selection
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions

## Constraints

- **PdfPig only** — no Docnet, Kreuzberg, or commercial libraries; design chose the only viable option under license constraints
- **No tagged PDF for v1** — design defers structure tree reader to extension point; layout analysis is the primary path
- **No OCR at index time** — extract existing text layers only; design chose scope containment
- **Follow XLSX/Word patterns** — classifier, loader, state transfer, template rendering, DI registration mirror existing binary format handlers
- **No bookmarks or forms in this increment** — those are Plan 02 and Plan 03; this plan delivers text extraction and page-level navigation only

## References

- [PDF Format Design](../designs/current/pdf-format.md) — architecture, surface model, text extraction strategy, memory management, error handling
- [PDF Format North Star](../north-star/formats/pdf.md) — what great looks like
- [PDF Parsing Research](../research/pdf-parsing-libraries.md) — PdfPig capabilities, limitations, memory concerns
- XLSX loader (`src/Formats/RepoQL.Formats.Xlsx/`) — reference implementation for binary format pattern
- Word loader (`src/Formats/RepoQL.Formats.Docx/`) — reference for text extraction to `Artifact.Text`
- [Processor Guide](../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor implementation patterns
- [Testing Guidelines](../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy conventions
- [PdfPig](https://github.com/UglyToad/PdfPig) — `PdfPig` NuGet v0.1.13, Apache 2.0
- [PdfPig Document Layout Analysis](https://github.com/UglyToad/PdfPig/wiki/Document-Layout-Analysis) — algorithm details

## Error Policy

Errors must not cascade. When extraction fails for a specific page:
1. Log warning with file path, page number, and exception details
2. Skip the page's text (leave a gap in `page_byte_offsets`)
3. Continue processing remaining pages
4. Surface partial results — a document with a corrupt page still has its other pages and metadata

File-level failures (can't open, encrypted, oversized) are the only cases where no partial results are possible — return `PipelineResult.Error`.

`StackOverflowException` from malicious PDFs with deeply recursive object references cannot be caught in .NET. The file size limit (200 MB) is the primary defense. Future mitigation via process isolation is an extension point.
