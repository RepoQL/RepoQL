---
description: Plan for Word format loader — project scaffolding, text extraction, heading tree, and materialization
tags: [format, word, docx, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Word Loader — Skeleton, Text, and Headings

Implements: [Word Document Format Design](../designs/current/word-format.md) — Classification, Surface Model, Text Extraction, Heading Detection, Graph Materialization, X-Ray Templates, Error Handling

## Scope

**Covers:**
- New project `RepoQL.Formats.Docx` with DI registration
- `DocxClassifier` pipeline processor
- `DocxLoader` implementing `IFormatLoader` and `IFormatMaterializer`
- `DocumentSurface` with Properties, Headings, Body, Stats
- `DocxDocumentState` for state transfer between load and materialize
- Text extraction from main document body (paragraph walking, run concatenation)
- Tracked change handling (final state extraction — include insertions, skip deletions)
- Heading detection via paragraph styles (Heading 1-9)
- Style inheritance walk for custom heading styles
- Liquid templates for headline, summary, structure
- Artifact creation with extracted text and token count
- Document node, heading nodes, HAS_PART edges, spans
- Tests for core scenarios

**Does not cover:**
- Table extraction (Plan: word-02-tables)
- Image extraction (Plan: word-03-images-comments-properties)
- Comment extraction (Plan: word-03-images-comments-properties)
- Document properties beyond title (Plan: word-03-images-comments-properties)
- Footnotes, endnotes, hyperlinks (Plan: word-04-footnotes-endnotes-hyperlinks)

## Enables

Once this exists:
- **Agents can discover Word documents** — `explore` finds `.docx` files with meaningful headlines showing top-level headings
- **Agents can read Word documents** — `read` returns extracted text navigable by heading
- **Section-level addressing works** — `#symbol=ExecutiveSummary` returns just that section
- **Semantic search covers Word content** — extracted text in `Artifact.Text` is automatically indexed
- **Plans 2-4 can proceed** — all build on the loader, surface model, and materialization pipeline established here

This is the foundation. Every subsequent plan adds node types and enriches the surface model without changing the core structure.

## Prerequisites

- OpenXML SDK 3.2.0 (`DocumentFormat.OpenXml`) — already in `Directory.Packages.props` via XLSX loader
- `LiquidTemplateRenderer` and `StandardFilters` from `RepoQL.Templating`
- `TokenEstimator` from `RepoQL.Contracts`
- Pipeline infrastructure: `IAsyncPipeline`, `FormatDescriptor`, `AddIndexingProcessor`

## North Star

A `.docx` file with headings should be indistinguishable from a `.md` file in the explore results — same headline shape, same section-level navigation, same heading tree in structure view. An agent that knows how to work with Markdown documents should be able to work with Word documents without learning anything new.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Docx` shall build and be referenced from the solution
- The project shall register its services via `AddDocxFormat()` extension method following the XLSX pattern
- The `FormatDescriptor` shall declare supported extensions (`.docx`, `.docm`, `.dotx`) and media types

### Classification
- The `DocxClassifier` shall assign `docx.document` kind for `.docx` and `.docm` files
- The `DocxClassifier` shall assign `docx.template` kind for `.dotx` files
- When a file has a `.docx` extension but is not a valid OpenXML package, the classifier shall return `null`

### Text Extraction
- The loader shall extract body text by walking paragraphs in document order
- The loader shall concatenate all runs within a paragraph into a single text line
- When a paragraph contains `<w:ins>` (insertion) runs, the loader shall include the text
- When a paragraph contains `<w:del>` (deletion) runs, the loader shall skip the deleted text
- The loader shall insert `# ` markers (depth matching heading level) before heading paragraphs
- The extracted text shall preserve paragraph boundaries as line breaks

### Heading Detection
- The loader shall identify headings by matching `ParagraphStyleId.Val` against `Heading1` through `Heading9`
- The loader shall walk `BasedOn` chains to detect custom styles that inherit from heading styles
- When a document uses no heading styles, the heading list shall be empty
- Each heading shall capture: level (1-9), text content, paragraph index in document order

### Tracked Change Metadata
- The document node shall include `has_tracked_changes` (boolean) in props
- When tracked changes exist, props shall include `tracked_change_count` and `tracked_change_authors` (array of distinct author names)

### Surface Model
- `DocumentSurface` shall carry: Properties (title), Headings list, Body (extracted text), Stats (page count, word count, paragraph count)
- `DocxDocumentState` shall carry: Surface, Digest, Size, MediaType, StoreUri
- No OpenXML types shall appear in the surface model or state

### Materialization
- The materializer shall create one artifact with `Text` set to extracted body text
- The materializer shall create one `document` node with title, page count, word count, tracked change metadata
- The materializer shall create one `heading` node per heading with level and text in props
- The materializer shall create `HAS_PART` edges from document to each heading with ordinals preserving document order
- The materializer shall create spans for each heading mapping to line ranges in extracted text
- Token count shall be estimated from `Artifact.Text` using `TokenEstimator.EstimateTokensSafe()`

### X-Ray Templates
- The headline template shall render: title (or filename), kind, page count, token count, top-level heading names
- The structure template shall render the full heading tree with `#` depth markers
- The summary template shall render document stats and heading overview

### Error Handling
- When the file cannot be opened as an OpenXML package, the loader shall return `PipelineResult.Error` with a diagnostic message
- When the file is password-protected, the loader shall return `PipelineResult.Error` explaining encryption
- When paragraph XML is malformed, the loader shall skip the paragraph and continue
- When style resolution fails, the loader shall fall back to exact style ID matching (no inheritance walk)

### Tests
- The test project shall include tests for: document with headings at multiple levels, document with no headings, document with tracked changes (verify final state), document with custom heading styles, empty document, corrupted/invalid file
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions
- Tests shall verify artifact text content, node creation, edge relationships, and span accuracy

## Constraints

- **OpenXML SDK only** — no Aspose, NPOI, or third-party extraction libraries; design chose existing dependency
- **No visual heuristics** — heading detection uses styles only, not font size or bold; design chose semantic correctness
- **Final state only** — tracked changes are resolved by including insertions and skipping deletions; design contained the 40-element complexity
- **Follow XLSX patterns** — classifier, loader, state transfer, template rendering, DI registration should mirror XLSX structure; design chose consistency

## References

- [Word Format Design](../designs/current/word-format.md) — architecture, surface model, materialization, error handling
- [Word Format North Star](../north-star/formats/word.md) — what great looks like
- XLSX loader (`src/Formats/RepoQL.Formats.Xlsx/`) — reference implementation for binary OpenXML pattern
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor implementation patterns
- [Testing Guidelines](../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy conventions
- `DocumentFormat.OpenXml.Wordprocessing` namespace — `WordprocessingDocument`, `Paragraph`, `Run`, `ParagraphProperties`

## Error Policy

Errors must not cascade. When extraction fails for a specific element:
1. Log warning with file path, element type, and exception details
2. Skip the element
3. Continue processing remaining elements
4. Surface partial results — a document with a corrupt paragraph still has its other paragraphs, headings, and metadata

Package-level failures (can't open, encrypted) are the only case where no partial results are possible — return `PipelineResult.Error`.
