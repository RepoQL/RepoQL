---
description: Vision for PDF format support — what agents should be able to do with PDF files in a repository
tags: [north-star, format, pdf, binary, documents]
audience: { human: 40, agent: 60 }
purpose: { north-star: 100 }
---

# PDF Format: What Great Looks Like

> An agent should understand what a PDF document contains, how it's organized, and what it argues — without opening it.

An agent exploring a repository encounters 80 PDF files — specifications, contracts, research papers, generated reports, scanned receipts, slide decks exported as PDF. It scans 80 headlines and knows what each one is: a 42-page financial report with sections on revenue, risk, and outlook; a 3-page scanned invoice from 2023; a 200-page API specification with 14 chapters and a full table of contents; a 1-page certificate that's just an image. It narrows to 6 documents about compliance, reads their structures — chapter outlines, section headings, page counts — and understands the regulatory landscape. It queries the graph: "which PDFs reference GDPR?" and finds them all, including one whose text was extracted from a scan. It reads a specific section from the API spec by page range without paying for the other 190 pages. Every PDF was built differently — some had rich bookmarks, some had none, some were tagged for accessibility, some were flat scans. The agent noticed none of this. The format handler extracted what each document could give and presented it through one surface.

---

## Discovery

- An agent should be able to understand what a PDF document contains from a single-line headline
- An agent should be able to see page count, approximate token cost, and key topics without opening the file
- An agent should be able to distinguish PDF types — text-based documents, scanned images, fillable forms, slide exports — from structure alone
- An agent should be able to scan 200 PDF files and filter to the 10 relevant ones without reading any
- An agent should be able to tell the difference between a 3-page memo and a 500-page specification from the headline

```
headline  →  "api-spec.pdf | pdf.document | 2.4 MB, ~18k tok | 200 pages | API Specification v3.1 | 14 chapters"
headline  →  "receipt-2023-04.pdf | pdf.scan | 84 KB, ~0.2k tok | 1 page | scanned, no text layer"
headline  →  "Q3-Report.pdf | pdf.document | 1.1 MB, ~8.4k tok | 42 pages | Financial Results, Risk Factors, Outlook"
headline  →  "onboarding-form.pdf | pdf.form | 340 KB, ~1.2k tok | 4 pages | 23 fields | Employee Onboarding"
```

---

## Structure

- An agent should be able to see whatever document outline is available — bookmarks, tagged headings, or an honest "no outline detected" — as a navigable tree
- An agent should be able to navigate from outline entry to page range without reading content
- An agent should be able to see a document's metadata — title, author, subject, creation date, producer — from the structure
- An agent should be able to see page dimensions and orientation to distinguish portrait documents from landscape slides

PDFs vary wildly in the structure they expose. A well-authored document has rich bookmarks, tagged headings, and a logical reading order. A scanned receipt has none. A 150-page report with no bookmarks and no tagged headings still has pages — the floor is always a page inventory with metadata. The agent sees what's actually there: a rich outline when the document provides one, a flat page list when it doesn't, and never a fabricated structure in between.

```
structure →
  API Specification v3.1 (200 pages, ~18k tok)
    Author: Engineering Team | Created: 2025-03-15 | Producer: LaTeX
    Outline:
      1. Introduction (p1-4)
      2. Authentication (p5-22)
        2.1 OAuth2 Flow (p5-12)
        2.2 API Keys (p13-18)
        2.3 Rate Limiting (p19-22)
      3. Endpoints (p23-142)
        ...
      14. Appendix: Error Codes (p188-200)
```

---

## Content Access

- An agent should be able to read a PDF's extracted text without dealing with binary encoding
- An agent should be able to read specific page ranges without paying for the whole document
- An agent should be able to read a specific section by outline entry without knowing page numbers
- An agent should be able to get content as readable, well-formed text — not raw extraction artifacts
- An agent should be able to see the approximate token cost of a page range before committing to read it

```
read("file:///docs/api-spec.pdf#page=5,12", 3000)      → OAuth2 Flow section text
read("file:///docs/api-spec.pdf#section=Authentication", 5000)  → full chapter
read("file:///docs/api-spec.pdf", 500)                  → headline + structure (budget too small for content)
```

---

## Text Fidelity

- An agent should be able to trust that extracted text faithfully represents the document's content
- An agent should be able to get text with preserved paragraph boundaries, not a wall of concatenated lines
- An agent should be able to see where text extraction is uncertain — regions flagged rather than silently mangled
- An agent should be able to trust that multi-column layouts are read in the correct order
- An agent should be able to get useful text from documents with mixed content — text paragraphs alongside images, charts, and tables

Text extraction is the core capability. A PDF that looks perfect on screen can extract into gibberish — ligatures as wrong characters, columns interleaved, reading order reversed. The format handler must produce text an agent can reason about, or honestly flag that it couldn't.

---

## Searchability

- An agent should be able to search across all PDF files in a repository using the same semantic search that works on code and markdown
- An agent should be able to find PDFs by what they discuss, not just their filenames
- An agent should be able to find specific passages within large PDFs through search, then read just those pages
- An agent should be able to find passages inside PDFs through the same search that finds code and prose — nothing extractable is invisible to search

A repository's knowledge lives in its PDFs as much as its code. A compliance specification, an architecture diagram's accompanying text, a vendor contract — these inform decisions every day. If they can't be searched alongside code and docs, they're invisible.

---

## Scanned Documents

- An agent should be able to distinguish a text-based PDF from a scanned image PDF from the headline
- An agent should be able to see when a PDF has a text layer (searchable scan) versus no text at all (image-only)
- An agent should be able to get whatever text is available — native text where it exists, OCR text where it's been embedded, honest "no text" where it hasn't
- An agent should be able to distinguish between text that came from the document and the absence of extractable text

Many PDFs arrive with OCR already embedded — scanner software, OCRmyPDF, Adobe's "Recognize Text" — and that text layer is available for extraction. An image-only PDF with no text layer is indexed with its metadata and page count, headline honestly reports "scanned, no text layer," and the agent knows exactly what it's working with.

---

## Forms

- An agent should be able to see that a PDF is a form and how many fields it contains from the headline
- An agent should be able to see form field names, types, and any default values in the structure
- An agent should be able to find all forms in a repository through the query surface
- An agent should be able to distinguish filled forms from blank templates

```sql
-- All PDF forms in the repository
SELECT uri, headline FROM Files
WHERE lang = 'pdf' AND kind = 'pdf.form'

-- Form fields for a specific document
SELECT n.props->>'field_name' AS field, n.props->>'field_type' AS type
FROM Nodes n
WHERE n.kind = 'pdf_field' AND n.uri LIKE '%onboarding%'
```

---

## Tables and Figures

- An agent should be able to see when pages contain tabular data
- An agent should be able to see when pages contain figures, diagrams, or images — with captions or alt text when available
- An agent should be able to find all PDFs in a repository that contain tables through the query surface

Tables in PDFs are notoriously difficult to extract — the format has no "table" primitive, just positioned text and lines. The north-star here is awareness, not perfect extraction: an agent should know a table exists and where it is, even when extracting its cells as structured data isn't reliable.

---

## Annotations and Links

- An agent should be able to see annotations (comments, highlights, stamps) as queryable metadata
- An agent should be able to trace hyperlinks to their targets — within the document, to other files, or to external URLs
- An agent should be able to find all PDFs that link to a given URL
- An agent should be able to see annotation counts and types in the structure without reading content

---

## Embedded Files

- An agent should be able to see when a PDF contains embedded attachments (PDF portfolios, attached spreadsheets)
- An agent should be able to list embedded files from the structure
- An agent should be able to trust that embedded file metadata is indexed even if the embedded files themselves aren't extracted

---

## Integrity

- An agent should be able to trust that a corrupted PDF still gets whatever indexing is possible — metadata, page count, partial text — with diagnostics on what failed
- An agent should be able to see which PDFs are encrypted or password-protected and therefore couldn't be fully indexed
- An agent should be able to distinguish "this PDF has no text" from "this PDF failed to parse"
- An agent should be able to trust that one malformed PDF never prevents other files from being indexed
- An agent should be able to see the producer software and PDF version

---

## Relationships

- An agent should be able to find code that references a PDF by filename or path
- An agent should be able to find markdown documents that link to a PDF
- An agent should be able to discover which PDFs cover related topics through semantic similarity

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Understand any PDF from its headline | 80 files become navigable in one scan |
| See whatever outline the document provides | Find the right section without reading 200 pages |
| Read specific pages or sections with known token cost | Token budget spent precisely |
| Distinguish text PDFs from scans from forms | Agents know what to expect before committing |
| Search PDF content alongside code and docs | Repository knowledge isn't trapped in binary files |
| Know when pages contain tables or figures | Awareness of visual content without perfect extraction |
| Distinguish extracted text from absent text | Scanned PDFs honestly report their limitations |
| Survive corrupt and encrypted files gracefully | One bad PDF never breaks the index |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Read a PDF to learn what it's about | An agent should see the topic from the headline |
| Conflate "no text extracted" with "no text exists" | An agent should see what text is available and what isn't |
| Pretend all PDFs have structure | An agent should see what each document actually provides |
| Treat PDF as opaque binary | An agent should see outline, metadata, text, and form fields |
| Pay for 200 pages to read one section | An agent should read specific pages or sections by reference |
| Silently skip image-only pages | An agent should see honest "no text" rather than gaps |
| Build PDF-specific query tools | An agent should query PDFs through the same SQL surface as everything else |

---

*An agent should be able to understand a repository's PDF documents — their topics, their structure, their content — through the same surface it uses for code and docs, with honest fidelity about what each document can give.*
