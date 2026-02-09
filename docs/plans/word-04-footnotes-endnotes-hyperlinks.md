---
description: Plan for Word format loader — footnotes, endnotes, and hyperlinks
tags: [format, word, docx, plan, footnotes, hyperlinks]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Word Loader — Footnotes, Endnotes, and Hyperlinks

Implements: [Word Document Format Design](../designs/current/word-format.md) — Content Stories (footnotes, endnotes), Hyperlinks, Graph Materialization (REFERS_TO edges)

## Scope

**Covers:**
- Footnote extraction from `FootnotesPart` (`word/footnotes.xml`)
- Endnote extraction from `EndnotesPart` (`word/endnotes.xml`)
- Footnote/endnote reference markers in body text (`[^n]`)
- Footnote/endnote content appended to body text after main body
- Hyperlink extraction (external URLs, internal bookmarks)
- `REFERS_TO` edges for hyperlinks
- Header/footer metadata extraction (surface as document property, not content)
- Tests for footnote, endnote, and hyperlink scenarios

**Does not cover:**
- Text box content extraction (design STUB — deferred pending investigation)
- SmartArt text extraction (design deferred)
- Bookmark nodes (could be added later without schema change)

## Enables

Once this exists:
- **Complete text extraction** — footnotes and endnotes are included in `Artifact.Text`, so semantic search covers the full document
- **Cross-reference graph** — hyperlinks create `REFERS_TO` edges, enabling "what does this document reference?" queries
- **Word format support is complete for v1** — all design sections implemented

This is the final plan. After this, the Word format loader delivers everything described in the north-star for v1.

## Prerequisites

- Plan: word-01-skeleton-text-headings complete — body text extraction, materialization pipeline
- Plan: word-02-tables complete — body text markers pattern established
- Plan: word-03-images-comments-properties complete — surface model fully populated

## North Star

An agent reading a section of a Word document should see the complete authored content — including the footnotes that carry supporting evidence, the endnotes that hold citations, and the hyperlinks that connect this document to others. Nothing the author wrote should be invisible.

## Done Criteria

### Footnotes
- The loader shall extract footnotes from `FootnotesPart`
- For each footnote, the loader shall extract: id, text content (concatenated paragraph runs)
- The loader shall skip separator and continuation footnotes (system-generated, not author content)
- The loader shall insert `[^n]` reference markers in body text at each footnote reference position
- The loader shall append footnote content after the main body with a delimiter:
  ```
  ---
  Footnotes:
  [1] Full text of footnote one
  [2] Full text of footnote two
  ```
- When no footnotes exist, no delimiter or section shall be appended

### Endnotes
- The loader shall extract endnotes from `EndnotesPart`
- The same extraction pattern as footnotes: id, text, skip system-generated notes
- Endnote references shall use `[*n]` markers to distinguish from footnotes
- Endnote content shall be appended after footnotes (if any) with its own delimiter:
  ```
  ---
  Endnotes:
  [*1] Full text of endnote one
  ```
- When no endnotes exist, no delimiter or section shall be appended

### Hyperlinks
- The loader shall extract hyperlinks from `Hyperlink` elements in paragraph runs
- For each hyperlink, the loader shall extract: display text, target URL (external) or bookmark name (internal)
- External hyperlinks shall resolve via the relationship part to get the actual URL
- Internal hyperlinks (bookmarks) shall resolve to the bookmark's location if identifiable
- The materializer shall create `REFERS_TO` edges from the document node to target URIs for external hyperlinks
  - Target URIs shall be stored as the edge's target reference (not as nodes — external targets may not be in the graph)
- Hyperlinks shall not generate separate nodes — they're properties of the text flow, surfaced through edges

### Header/Footer Metadata
- The loader shall detect header and footer parts
- The loader shall extract header/footer text content and store as document node properties: `header_text`, `footer_text`
- Header/footer content shall NOT be included in `Artifact.Text` — it's metadata, not document content
- When headers/footers contain only page numbers or field codes, the property shall reflect that (e.g., `"Page {PAGE}"`)

### Token Count Update
- Token count shall be re-estimated after footnotes and endnotes are appended to body text
- The headline token count shall reflect the full extractable content including footnotes/endnotes

### Tests
- Test document with footnotes — verify reference markers in body and appended content
- Test document with endnotes — verify separate markers and section
- Test document with both footnotes and endnotes — verify ordering (footnotes first)
- Test document with system-generated separator footnotes — verify excluded
- Test document with external hyperlinks — verify `REFERS_TO` edges with correct URLs
- Test document with internal bookmark hyperlinks — verify resolution
- Test document with headers/footers — verify metadata extraction, not body inclusion
- Test document with no footnotes/endnotes/hyperlinks — verify no empty sections appended
- Test malformed footnotes part — verify skip and continue

## Constraints

- **No text box extraction** — design marks this as a STUB; out of scope for v1
- **Hyperlinks are edges, not nodes** — design chose lightweight representation; agents query connectivity, not hyperlink inventories
- **Headers/footers are metadata only** — design determined these are usually boilerplate; included in props but not in searchable text
- **No bookmark graph** — internal hyperlinks resolve where possible, but bookmarks are not materialized as navigable nodes

## References

- [Word Format Design](../designs/current/word-format.md) — Content Stories, Hyperlinks sections
- `DocumentFormat.OpenXml.Wordprocessing` — `Footnotes`, `Endnotes`, `FootnoteReference`, `EndnoteReference`, `Hyperlink`
- `DocumentFormat.OpenXml.Packaging` — `FootnotesPart`, `EndnotesPart`, `HyperlinkRelationship`

## Error Policy

Each extraction phase is independently try/caught:
- **Footnotes:** If `FootnotesPart` fails, body text has no footnote markers and no appended section. Log warning.
- **Endnotes:** Same as footnotes — independent failure.
- **Hyperlinks:** If a hyperlink element fails to parse or its relationship is missing, skip it. Don't create a broken `REFERS_TO` edge.
- **Headers/footers:** If parts fail, omit the metadata properties. Log warning.

Body text and heading tree from Plan 01 remain intact regardless of failures here.
