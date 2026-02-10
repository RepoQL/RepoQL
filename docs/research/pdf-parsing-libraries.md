---
description: Research on .NET PDF parsing libraries — free, commercially usable, for text and structure extraction
tags: [research, pdf, parsing, libraries, dotnet]
audience: { human: 50, agent: 50 }
purpose: { research: 90, reference: 10 }
---

# PDF Parsing Libraries for .NET

Research for selecting a PDF parsing library for RepoQL's PDF format handler.

*Research date: February 9, 2026*

## Context

RepoQL needs to extract text, structure (bookmarks, metadata, headings), and form fields from PDF files in indexed repositories. The format handler follows the existing pattern (see Xlsx, Markdown loaders): parse the file, populate a DocumentModel with x-ray summaries, materialize nodes/edges into the graph.

**Constraints:**
- License must be free and commercially usable (MIT, Apache 2.0, BSD). No AGPL, no revenue gates, no per-seat.
- Cross-platform (.NET 8+, Windows/Linux/macOS)
- Read-only — no PDF creation or editing needed
- Must handle corrupt/malformed PDFs gracefully (errors never cascade)
- Pure .NET preferred (no native binary dependencies) but not required

**What the format handler needs to extract:**
- Text content with reasonable reading order
- Bookmarks / outline tree
- Document metadata (title, author, subject, producer, dates)
- Page count, dimensions, orientation
- Form field inventory (names, types, values)
- Annotations (comments, links, highlights)
- Embedded file inventory
- Image presence/location on pages
- Tagged PDF structure tree (when available)

---

## PdfPig

Apache 2.0 license. Pure managed .NET. No native dependencies.

> [GitHub](https://github.com/UglyToad/PdfPig) — 2,351 stars, active development
> [NuGet](https://www.nuget.org/packages/PdfPig/) — package name `PdfPig`, 18.1M downloads

| Spec | Value |
|------|-------|
| Latest version | 0.1.13 (December 23, 2024) |
| Last repo activity | February 6, 2026 |
| Targets | .NET 8, .NET 6, .NET Standard 2.0, .NET Framework 4.6.2 |
| Dependencies (.NET 8) | None |
| Origin | Port of Apache PDFBox (Java) |

### Capabilities

| Capability | Supported | API |
|------------|-----------|-----|
| Text extraction | Yes | `page.Text`, `ContentOrderTextExtractor.GetText(page)` |
| Per-character position data | Yes | `page.Letters` — bounding box, font name, font size, color per character |
| Layout analysis | Yes | Recursive XY Cut, Docstrum, Nearest Neighbour algorithms |
| Reading order detection | Yes | `UnsupervisedReadingOrderDetector` (binary interval relations) |
| Bookmarks / outlines | Yes | `document.TryGetBookmarks()` |
| Document metadata | Yes | `document.Information` (title, author, subject, etc.) + XMP via `TryGetXmpMetadata()` |
| Page dimensions | Yes | `page.Width`, `page.Height`, `page.Rotation` |
| Form fields (AcroForms) | Yes (read-only) | `document.TryGetForm()` — text, checkbox, combo, list, push button, signature fields |
| Annotations | Yes | `page.GetAnnotations()` |
| Embedded files | Yes | `document.Advanced.TryGetEmbeddedFiles()` |
| Images | Yes | `page.GetImages()` — bounding boxes, raw/decoded bytes, PNG conversion |
| Tagged PDF (structure tree) | Partial | `page.GetMarkedContents()` — low-level only, no high-level semantic API |
| Encrypted PDFs | Yes | Password parameter in `ParsingOptions` |
| OCR text layers | Yes | Reads invisible text layers (TextRenderingMode = Invisible) |
| Export formats | PAGE XML, ALTO, hOCR |

### Text Extraction Detail

`page.Text` returns text in PDF content stream order, which is often wrong for multi-column layouts. The recommended approach is the Document Layout Analysis pipeline:

```csharp
var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
```

A separate `DecorationTextBlockClassifier` identifies headers, footers, and page numbers across multi-page documents.

> [Document Layout Analysis wiki](https://github.com/UglyToad/PdfPig/wiki/Document-Layout-Analysis) — algorithm details

### Robustness

Default parsing is lenient (error-recovery mode). Specific hardening:
- Tolerant parsing for invalid USC2, CMAP formats, CFF fonts, missing font subtypes
- `ParsingOptions.SkipMissingFonts` — continues extraction with missing/corrupt fonts
- Screened against 6,000+ documents in v0.1.11
- Prevention of OOM from decompressed streams

Known risks:
- DoS via crafted PDFs raised in [Issue #771](https://github.com/UglyToad/PdfPig/issues/771)
- Historical StackOverflow on corrupt PDFs ([Issue #33](https://github.com/UglyToad/PdfPig/issues/33)), since addressed

### Memory and Performance

PdfPig loads the entire file into memory on `Open()`. Internal caches (font references, etc.) are not freed between pages when iterating sequentially. A 15MB PDF with text and graphics was reported to consume 4-6 GB of memory.

> [Issue #371](https://github.com/UglyToad/PdfPig/issues/371) — memory not released between pages

Workaround: reopen the document per page (trades I/O for memory).

Performance was initially 4-5x slower than PDFBox. Significant optimization in v0.1.11 (`Span<T>`, `ReadOnlyMemory<T>`, LINQ removal from hot paths). No published benchmarks for current version.

> [Issue #47](https://github.com/UglyToad/PdfPig/issues/47) — performance history

### Tagged PDF Gap

PdfPig can access marked content regions (`page.GetMarkedContents()`) and the raw PDF object structure, but has no high-level API for reading the semantic structure tree (H1, P, Table, Figure tags). Building this would require traversing the `StructTreeRoot` via low-level token access.

> [Issue #873](https://github.com/UglyToad/PdfPig/issues/873) — tagged PDF support requests
> [Issue #391](https://github.com/UglyToad/PdfPig/issues/391) — StructTreeRoot resolution issues

### Table Detection

Not built in. Third-party options built on PdfPig:
- **Tabula** (NuGet: `Tabula`, v0.1.5) — C# port of tabula-java
- **Camelot Sharp** — C# port of camelot Python library

> [Issue #152](https://github.com/UglyToad/PdfPig/issues/152) — table support discussion

### Stability Note

Pre-1.0. The docs state: "While the version is below 1.0.0 minor versions will change the public API without warning."

---

## Docnet.Core

MIT license (wrapper). Apache 2.0 / BSD-3-Clause (PDFium native). Requires native binaries.

> [GitHub](https://github.com/GowenGit/docnet) — 570 stars, last commit September 2023
> [NuGet](https://www.nuget.org/packages/Docnet.Core/) — 3.8M downloads, 17.58 MB package

| Spec | Value |
|------|-------|
| Latest version | 2.6.0 (September 4, 2023) |
| Last commit | September 27, 2023 |
| Targets | .NET Standard 2.0, .NET Framework 4.5+ |
| Native binaries | Win x64/x86, Linux x64/ARM/ARM64, macOS x64/ARM64 |
| PDFium version | 5445 (not latest) |

### Capabilities

| Capability | Supported | Notes |
|------------|-----------|-------|
| Text extraction | Yes | `page.GetText()`, `page.GetCharacters()` with bounding boxes |
| Layout analysis | No | Content stream order only; no column detection |
| Bookmarks / outlines | No | PDFium supports this (`FPDFBookmark_*`), Docnet doesn't wrap it |
| Document metadata | No | PDFium supports this (`FPDF_GetMetaText`), Docnet doesn't wrap it |
| Page dimensions | Yes | `GetPageWidth()`, `GetPageHeight()` |
| PDF version | Yes | `GetPdfVersion()` |
| Form fields | No | Form rendering for images only, no field enumeration |
| Annotations | No | Open request [#91](https://github.com/GowenGit/docnet/issues/91) |
| Embedded files | No | |
| Images (extraction) | No | Cannot extract embedded images |
| Images (rendering) | Yes | Render pages to BGRA byte arrays |
| Encrypted PDFs | Yes | Password parameter |
| Tagged PDF | No | |

### Key Limitation

Docnet wraps ~30-40 of PDFium's ~436 functions. It is heavily focused on two use cases: text extraction and page-to-image rendering. Bookmarks, metadata, annotations, forms, and structure — all available in PDFium — are absent from the public API.

### Performance Concern

All PDFium calls are serialized through a global lock (`DocLib.Lock`). Text extraction from multiple PDFs in parallel is serialized at the native call level — a throughput bottleneck for batch indexing.

PDFium is unmanaged C++. A crash from a maliciously crafted PDF could crash the entire process.

> [Issue #20](https://github.com/GowenGit/docnet/issues/20) — AccessViolationException reported

### Maintenance Status

No release in over 2 years. 15 open issues. Key feature requests (bookmarks, annotations, metadata) open since August 2023 with no progress. The project appears stalled.

---

## PDFsharp 6.x

MIT license. Pure managed .NET. Primarily a PDF creation/modification library.

> [GitHub](https://github.com/empira/PDFsharp) — official empira repo
> [NuGet](https://www.nuget.org/packages/PDFsharp/) — 48.8M downloads

| Spec | Value |
|------|-------|
| Latest version | 6.2.4 (January 6, 2026) |
| Targets | .NET 8+, .NET Standard 2.0, .NET Framework 4.6.2 |
| Dependencies | None |

### Text Extraction: Effectively No

The [official FAQ](https://docs.pdfsharp.net/PDFsharp/Overview/FAQ.html) states: "This can be done at a low level. You can get at the characters in the order they are drawn... There are no high-level functions that return words, paragraphs, or whole pages."

You would need to manually tokenize the content stream using the `CLexer` class — essentially reimplementing what PdfPig already does.

### What It Can Do

- Open existing PDFs, manipulate pages (merge, split, watermark)
- Access `PdfDocument.Info` dictionary (Title, Author, Subject, etc.)
- Partial bookmark reading via `PdfOutline` (low-level)
- No form field reading, no annotation reading, no image extraction

### Assessment

PDFsharp is one of the most downloaded .NET PDF packages, but almost entirely for generation. Not viable as a PDF parser for text extraction without extensive custom work.

---

## Kreuzberg

MIT license. Rust core with .NET bindings via NuGet. Uses PDFium internally.

> [GitHub](https://github.com/kreuzberg-dev/kreuzberg) — newer project
> [NuGet](https://libraries.io/nuget/Kreuzberg) — available

| Spec | Value |
|------|-------|
| Version | v4.0 (ground-up Rust rewrite) |
| Architecture | Rust core, native PDFium, SIMD, parallelism |
| License | MIT |

### Capabilities (Claimed)

- Text extraction with automatic fallback strategies
- Document metadata
- Table extraction
- OCR via Tesseract (optional)
- Encrypted PDF support with automatic fallback
- Language detection
- 50+ format support
- CLI, REST API, and MCP server interfaces

### Assessment

Newer entrant. Claims 10-50x faster than Python alternatives, 60-90% less memory usage. Uses the same engine as Docnet (PDFium) but with a higher-level abstraction. The MCP server interface is directly relevant to RepoQL's architecture.

Risk: relatively new (v4 is a rewrite), .NET bindings may be thin. Needs hands-on evaluation to verify claims and API completeness.

> [Kreuzberg v4 Announcement](https://dev.to/t_ivanova/announcing-kreuzberg-v4-55ia) — architecture overview

---

## Eliminated Options

| Library | License | Reason |
|---------|---------|--------|
| iText 7 | AGPL / Commercial (~$45K/yr) | AGPL incompatible |
| MuPDF / PyMuPDF | AGPL / Commercial | AGPL incompatible |
| QuestPDF | Revenue-gated | Not free above $1M revenue; also generation-only |
| FreeSpire.PDF | Proprietary freeware | 10-page hard limit; unmaintained free tier |
| Pdfium.Net SDK | Commercial | Paid license required |
| NReco.PdfRenderer | Commercial | Paid license required |
| Poppler wrappers (P/Invoke) | GPL | P/Invoke = linking = GPL obligation |
| PdfSharpCore | MIT | Same limitations as PDFsharp; superseded by 6.x |
| Melville.Pdf | MIT | No text extraction; hobby project; .NET 9+ only |
| PDFtoImage | MIT | Rendering only, no text extraction |
| pdf-extract (.NET) | GPL | Wraps Xpdf; GPL; unmaintained |

---

## Process-Based Approach: pdftotext

An alternative to an in-process library: shell out to `pdftotext` from Poppler/Xpdf.

**License:** GPL v2/v3, but Glyph & Cog explicitly permits bundling the standalone executable with commercial software: "If you want to use the stand-alone executables (pdftotext for example) with your application, you're free to do so."

> [Glyph & Cog Open Source](https://www.glyphandcog.com/opensource.html) — licensing statement

| Dimension | Detail |
|-----------|--------|
| Text quality | Excellent. Layout mode (`-layout`) preserves spatial positioning |
| Structure | None — text only, no bookmarks/metadata/forms |
| Speed | Very fast (benchmarked at 10-100x faster than complex parsers) |
| Robustness | Battle-tested. Crashes cannot affect host process (process isolation) |
| Availability | Pre-built Windows binaries (Chocolatey), ubiquitous on Linux/macOS |
| Latest release | Poppler 26.02.0 (February 2026) — actively maintained |

**What it gives you:** Fast, reliable text extraction with process isolation — a crash in pdftotext cannot bring down RepoQL. This aligns with "errors never cascade."

**What it doesn't give you:** Bookmarks, metadata, form fields, annotations, images, tagged structure. It is text-only.

**Assessment:** Viable as a fallback for text extraction when an in-process library produces empty or garbled text. Not sufficient as a primary parser (missing all structure).

---

## Comparison

| Dimension | PdfPig | Docnet.Core | PDFsharp 6.x | Kreuzberg | pdftotext (process) |
|-----------|--------|-------------|--------------|-----------|---------------------|
| License | Apache 2.0 | MIT + BSD-3 | MIT | MIT | GPL (process OK) |
| Text extraction | Yes (with layout analysis) | Yes (content order only) | No | Yes (claimed) | Yes (excellent) |
| Bookmarks | Yes | No | Partial (low-level) | Unknown | No |
| Metadata | Yes + XMP | No | Partial | Yes (claimed) | No |
| Form fields | Yes (read-only) | No | No | Unknown | No |
| Annotations | Yes | No | No | Unknown | No |
| Images | Yes (extract + bounds) | No (render only) | No | Unknown | No |
| Tagged PDF | Partial (low-level) | No | No | Unknown | No |
| Table detection | Via Tabula package | No | No | Yes (claimed) | No |
| Encrypted PDFs | Yes | Yes | No | Yes | No |
| Pure .NET | Yes | No (native PDFium) | Yes | No (native Rust + PDFium) | No (external process) |
| Memory model | Full file in memory | Full file in memory | Full file in memory | Unknown | OS process isolation |
| Maintenance | Active (Feb 2026) | Stalled (Sep 2023) | Active (Jan 2026) | Active | Active (Feb 2026) |
| NuGet downloads | 18.1M | 3.8M | 48.8M | Low | N/A |

---

## Layered Strategy

No single library handles all PDFs well. The research suggests a layered approach:

| Layer | Tool | When | What it provides |
|-------|------|------|-----------------|
| 1. Tagged structure | PdfPig low-level API | PDF has structure tree | Correct reading order, semantic headings, table structure — zero heuristics |
| 2. Layout analysis | PdfPig DocumentLayoutAnalysis | Untagged PDFs | Inferred reading order via Recursive XY Cut / Docstrum algorithms |
| 3. Process fallback | `pdftotext -layout` | PdfPig produces empty/garbled text | Second opinion using completely different heuristics; process isolation |

Layer 1 is the highest-leverage investment. PDFs from Word, Google Docs, LaTeX, and modern publishing tools are increasingly tagged (PDF/UA compliance is legally required in many contexts). When the structure tree is present, it is more reliable than any visual layout algorithm. No open-source .NET library provides a high-level API for this — it would need to be built on top of PdfPig's low-level object access.

---

## Gaps

- **Kreuzberg hands-on evaluation**: Claims are unverified. Need to test actual API surface, .NET binding completeness, text quality, and structure extraction capabilities
- **PdfPig memory under RepoQL workload**: The 4-6 GB memory spike is from one report on a 15MB PDF. Need to profile against representative repository PDF collections
- **Tagged PDF prevalence in repositories**: No data on what percentage of PDFs in typical dev repos have structure trees. This affects the ROI of Layer 1
- **PdfPig tagged PDF feasibility**: Low-level access exists but building a high-level structure tree reader has unknown complexity — issues [#391](https://github.com/UglyToad/PdfPig/issues/391) and [#873](https://github.com/UglyToad/PdfPig/issues/873) show edge cases
- **pdftotext bundling logistics**: Cross-platform binary distribution for Windows/Linux/macOS has packaging complexity
- **Tabula (.NET) quality**: The C# port of tabula-java is at v0.1.5 — maturity and extraction quality are unknown
