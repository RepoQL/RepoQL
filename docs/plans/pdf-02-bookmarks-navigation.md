---
description: Plan for PDF format loader — bookmark tree extraction, symbol-based navigation, and structure template enrichment
tags: [format, pdf, plan, bookmarks, navigation]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: PDF Loader — Bookmarks and Navigation

Implements: [PDF Format Design](../designs/current/pdf-format.md) — Bookmark Extraction, Graph Materialization (pdf_bookmark nodes, spans), X-Ray Templates (structure enrichment), Cross-Cutting Concerns (URI addressing)

## Scope

**Covers:**
- Bookmark tree extraction via `document.TryGetBookmarks()`
- `BookmarkInfo` in `PdfDocumentSurface`
- `pdf_bookmark` nodes with title, level, target_page in props
- `HAS_PART` edges from document to bookmarks with tree order ordinals
- Spans mapping bookmarks to page ranges (`StartLine = target_page`, `EndLine = next_bookmark_target_page - 1`)
- `#symbol=BookmarkTitle` addressing via bookmark title → page range resolution
- Structure template: bookmark tree with page ranges when bookmarks exist, page inventory fallback when they don't
- Headline template enrichment: bookmark count and top bookmark titles
- `has_bookmarks` flag and `bookmark_count` on document node props
- Tests for bookmark scenarios

**Does not cover:**
- Form field extraction (Plan: pdf-03-forms-annotations-metadata)
- Annotation extraction (Plan: pdf-03-forms-annotations-metadata)
- SQL views (Plan: pdf-03-forms-annotations-metadata)
- Tagged PDF headings (extension point — design defers)

## Enables

Once this exists:
- **Agents can navigate by section** — `read("file:///spec.pdf#symbol=Authentication", 5000)` returns just that section
- **Agents can see document outlines** — structure view shows the bookmark tree with page ranges, just like heading trees for Markdown and Word
- **Agents can scan document topics from headlines** — top bookmark titles in the headline reveal what the document covers without reading it
- **Agents can query bookmarks across PDFs** — find all documents with bookmarks about "Authentication" or "Risk Factors"
- **The gap between PDF and Word/Markdown closes** — all three formats support heading-based navigation through the same surface

## Prerequisites

- Plan: pdf-01-skeleton-text-extraction complete — loader, surface model, text extraction, page byte offsets, materialization pipeline

## North Star

A well-bookmarked PDF should feel like a well-structured Markdown file. An agent scanning explore results should see the topic structure from the headline. An agent reading a section by name should get exactly that section's pages. A PDF without bookmarks should honestly report "no outline detected" — no fabrication, just honest absence.

## Done Criteria

### Bookmark Extraction
- The loader shall attempt bookmark extraction via `document.TryGetBookmarks()`
- When bookmarks exist, the loader shall walk the bookmark tree recursively, extracting: title, nesting level, target page number
- Each bookmark shall have a unique node ID assigned during extraction
- Bookmark extraction shall use a single document open (not reopen-per-page) since bookmarks are a document-level feature

### Surface Model
- `BookmarkInfo` shall carry: NodeId, SpanId, Title, Level, TargetPage, Children (recursive tree)
- `PdfDocumentSurface.Bookmarks[]` shall contain the flattened bookmark list (tree structure preserved via Level)
- `Stats` shall include `has_bookmarks` (boolean) and `bookmark_count` (total including nested)

### Materialization
- The materializer shall create one `pdf_bookmark` node per bookmark entry
- Node props shall include: `title`, `level`, `target_page`
- The materializer shall create `HAS_PART` edges from document to each bookmark with ordinals preserving tree order (depth-first)
- The materializer shall create spans for each bookmark:
  - `StartLine = target_page`
  - `EndLine = next_sibling_or_parent_sibling_target_page - 1` (or last page for the final bookmark at each level)
  - This enables page range reads via symbol addressing

### Symbol Addressing
- `#symbol=BookmarkTitle` shall resolve via the existing symbol resolution mechanism that matches node names
- Bookmark titles shall be used as the node name so that `#symbol=Authentication` finds the bookmark titled "Authentication" and returns its page range
- Resolution uses the bookmark's span to determine the page range, then slices `Artifact.Text` using `page_byte_offsets`

### Headline Template
- When bookmarks exist, the headline shall include bookmark count and top-level bookmark titles (up to 3)
- Example: `api-spec.pdf | pdf.document | 2.4 MB, ~18k tok | 200 pg | 14 bookmarks | Introduction, Authentication, Endpoints`
- When no bookmarks exist, no bookmark information appears in the headline

### Structure Template
- When bookmarks exist, the structure template shall render the bookmark tree with page ranges:
  ```
  API Specification v3.1 (200 pg, ~18k tok)
    Author: Engineering Team | Producer: LaTeX | PDF 1.7
    Outline:
      1. Introduction (p1-4)
      2. Authentication (p5-22)
        2.1 OAuth2 Flow (p5-12)
        2.2 API Keys (p13-18)
      3. Endpoints (p23-142)
  ```
- When no bookmarks exist, the structure template shall show the page inventory from Plan 01
- The structure template shall include annotation count and form field count placeholders (populated by Plan 03)

### Document Node Enrichment
- The document node props shall include `has_bookmarks` (boolean) and `bookmark_count` (integer)
- When no bookmarks exist, `has_bookmarks = false` and `bookmark_count = 0`

### Tests
- Test with a PDF containing a flat bookmark list (5 bookmarks, all top-level) — verify nodes, edges, spans
- Test with a nested bookmark tree (3 levels deep) — verify level props and tree-order ordinals
- Test with a PDF containing no bookmarks — verify `has_bookmarks = false`, structure falls back to page inventory
- Test bookmark span calculation — verify page ranges: first bookmark starts at target page, ends before next sibling
- Test `#symbol=` addressing — verify bookmark title resolves to correct page range text
- Test bookmark with target page beyond document length — verify graceful handling (clamp to last page)
- Test bookmark extraction failure — verify document still materializes with text and metadata from Plan 01
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions

## Constraints

- **No tagged PDF headings** — design defers structure tree reader; bookmarks are the only v1 outline source
- **No `#section=` fragment** — design defers; `#symbol=` provides the same capability via bookmark title matching
- **Bookmarks use single-open** — document-level feature, not page-level; always extracted in one open regardless of reopen-per-page threshold
- **Bookmark titles used verbatim** — no slugification or normalization; exact title matching for symbol resolution

## References

- [PDF Format Design](../designs/current/pdf-format.md) — Bookmark Extraction, Graph Materialization, Cross-Cutting Concerns sections
- [PDF Format North Star](../north-star/formats/pdf.md) — Structure section
- Plan: pdf-01-skeleton-text-extraction — prerequisite (loader, surface model, text extraction)
- PdfPig `Bookmarks` class — `document.TryGetBookmarks()`, `BookmarkNode` tree structure
- Word loader heading materialization (`src/Formats/RepoQL.Formats.Docx/DocxLoader.cs`) — analogous pattern for heading nodes, spans, and HAS_PART edges

## Error Policy

Bookmark extraction is independently try/caught. When bookmark extraction fails:
1. Log warning with file path and exception details
2. Set `has_bookmarks = false`, `bookmark_count = 0` on document node
3. Structure template falls back to page inventory
4. Text extraction, page addressing, and document metadata from Plan 01 remain intact

Individual bookmark failures (e.g., a bookmark with an invalid target page) are handled per-bookmark: skip the bad bookmark, continue extracting the rest.
