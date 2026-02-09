---
description: Design for Word document (.docx) format support — extracting structure, text, and collaboration artifacts from OpenXML binary format
tags: [format, word, docx, openxml, design]
audience: { human: 45, agent: 55 }
purpose: { design: 85, flow: 15 }
---

# Word Document Format — Design

## North Star

An agent should understand what a Word document argues, how it's organized, and what structured content it contains — without opening it. The heading tree, tables, and collaboration state are all queryable from the graph. Reading a section of a `.docx` feels like reading Markdown.

**Informed by:** `docs/north-star/formats/word.md`

## Context

Word documents appear in repositories as specifications, proposals, contracts, reports, and templates. They're opaque binaries that contain rich structured text. The goal: extract the document's skeleton — heading tree, tables, images, comments, properties — and make it navigable through the same explore/query/read surface as every other format.

The XLSX loader already established the pattern for binary OpenXML formats in RepoQL. This design follows that pattern and focuses on what's different about Word.

**Key difference from XLSX:** Word documents contain extractable text content. XLSX stores `Text = null` on artifacts because the content is tabular data best queried through `read_csv_auto()`. Word documents are prose — the artifact should carry extracted text so agents can read sections, search content, and navigate by heading. This is the primary design divergence.

## Constraints

| Constraint | Source |
|------------|--------|
| Single writer to DuckDB | Hard constraint — all writes through `DuckDbDataStore` |
| Frozen schema — 5 tables only | Extend via views/macros/UDFs, never new tables |
| Errors never cascade | One malformed `.docx` must never stop indexing |
| OpenXML SDK 3.2.0 | Already in dependency tree via XLSX loader |
| `.docx` only, not `.doc` | Legacy binary format is a different spec entirely — out of scope |
| No text extraction libraries | OpenXML SDK + string concatenation, not Aspose or third-party extractors |

---

## Design

### Classification

The classifier refines the provisional media type (already set from `.docx` extension) with a semantic kind based on structure cues.

| Extension | Media Type | Kind |
|-----------|-----------|------|
| `.docx` | `application/docx` | `docx.document` |
| `.docm` | `application/docm` | `docx.document` |
| `.dotx` | `application/dotx` | `docx.template` |

Kind refinement is deferred — the classifier does not attempt to distinguish specs from proposals from reports. That's a structure-level concern better handled by agents querying heading patterns and properties. The classifier's job: confirm it's a Word document and flag templates.

`.docm` files are treated identically to `.docx` for structure extraction. Macro presence is surfaced as a document property, not a separate kind.

### Surface Model

The parser extracts a `DocumentSurface` — a pure data model carrying everything needed for materialization. No OpenXML types escape the parser.

```
DocumentSurface
├── Properties          — title, author, created, modified, custom props
├── Sections[]          — page-level divisions (section breaks)
│   └── (not materialized as nodes — too low-signal)
├── Headings[]          — style-based (Heading 1-9), with text, level, paragraph index
├── Tables[]            — dimensions, header row detection, column names, cell text
│   └── Cells[][]       — row-major, merged cells tracked
├── Images[]            — alt text, caption (from adjacent paragraph), content type, size
├── Comments[]          — author, date, text, anchor range, resolved state
├── Footnotes[]         — text content, anchor position
├── Endnotes[]          — text content, anchor position
├── Hyperlinks[]        — URL, anchor text, target type (external/internal/bookmark)
├── Body               — extracted text with structural markers
└── Stats              — page count, word count, paragraph count
```

**Text extraction strategy:** Walk paragraphs in document order. For each paragraph, concatenate all runs' text content. Skip deleted runs from tracked changes (extract final state). Insert structural markers:

- `# Heading Text` for headings (using `#` depth matching heading level)
- `[Table: Name (cols x rows)]` for table positions
- `[Image: alt text]` for image positions
- `[^n]` for footnote references

This produces readable text with navigation markers — similar to what Markdown looks like as source.

### Tracked Changes — Contained Complexity

Tracked changes are the most complex part of the OpenXML Word spec (~40 elements). The design choice: **extract final state only**.

- `<w:ins>` (insertion) runs: include the text
- `<w:del>` (deletion) runs: skip the text
- `<w:rPr>` formatting changes: ignore (we strip formatting anyway)

Tracked change *metadata* is surfaced at the document level:
- `has_tracked_changes: true/false`
- `tracked_change_authors: ["Alice", "Bob"]`
- `tracked_change_count: 8`

This contains the complexity behind a simple interface. Agents see that a document has unresolved changes and who made them. They don't need to navigate the revision markup.

**Why not model individual changes:** Each tracked change interleaves with content at the run level. Modeling them as nodes would require spans that point into the middle of paragraphs, creating a parallel structure that fights the heading-based navigation the rest of the design enables. The cost exceeds the value for v1.

### Comments

Comments are extracted as individual surface objects with:
- `Author`, `Date`, `Text` — the comment itself
- `AnchorStartParagraph`, `AnchorEndParagraph` — what text it annotates
- `Resolved` — whether the comment is resolved

**Threading:** Word stores comment replies via `CommentsExPart` with `paraIdParent` links. The design extracts flat comments for v1. Threading is a materialization concern that can be added later without changing the surface model (add a `ParentCommentId` field).

**Resolved state:** The `CommentsExPart` (`w16cex:commentExtensible`) stores `done` attribute for resolved/unresolved. Older documents without this part: treat all comments as unresolved.

### Heading Detection

Headings are identified by paragraph style, not by formatting:

1. Read `ParagraphProperties.ParagraphStyleId.Val`
2. Match against `Heading1` through `Heading9` (built-in style IDs)
3. Walk `BasedOn` chain if custom styles inherit from heading styles

**Edge case:** Documents that use font size and bold instead of styles will have no heading tree. This is the correct behavior — the structure reflects what the author declared, not what looks like a heading visually. The headline will show no headings, which accurately communicates the document's lack of navigable structure.

### Tables

Tables are extracted with:
- **Dimensions** — row count, column count (accounting for merged cells)
- **Header row** — detected via `<w:tblHeader/>` on row properties, or heuristic (first row with different formatting)
- **Column names** — text from header row cells
- **Cell text** — concatenated runs per cell, structural markers for nested content
- **Merged cells** — tracked via `HorizontalMerge` and `VerticalMerge` elements, represented as spans in the cell grid

Tables used purely for layout (single-column, no borders, no header) are detected and excluded from the table inventory. Heuristic: tables with 1 column and no header row styling are likely layout tables.

### Content Stories

Word documents contain multiple content streams ("stories"):

| Story | Extracted | Rationale |
|-------|-----------|-----------|
| Main body | Yes | Primary content |
| Footnotes | Yes | Carry real content referenced from body |
| Endnotes | Yes | Same as footnotes |
| Headers | Metadata only | Usually boilerplate (page numbers, titles) — surface as property, not content |
| Footers | Metadata only | Same as headers |
| Text boxes | **STUB** — needs investigation | May contain meaningful content in some document styles |

Footnotes and endnotes are appended after the main body text with clear delimiters:

```
---
Footnotes:
[1] Full text of footnote one
[2] Full text of footnote two
```

### Graph Materialization

Following the XLSX pattern — state transfer via `DocxDocumentState` in `DocumentModel.Metadata`.

**Artifact:**

| Field | Value |
|-------|-------|
| `Text` | Extracted body text with structural markers (unlike XLSX which stores `null`) |
| `Headline` | Rendered via Liquid template |
| `Summary` | Rendered via Liquid template |
| `Structure` | Rendered via Liquid template — full heading tree with table/image positions |
| `TokenCount` | Estimated from `Text` content (not binary size, not rendered summaries) |

**Nodes:**

| Kind | What | Props |
|------|------|-------|
| `document` | Root node | title, author, created, modified, page_count, word_count, has_tracked_changes, has_comments, custom properties |
| `heading` | Each heading (H1-H9) | level, text |
| `table` | Each data table | row_count, col_count, column_names, has_header |
| `image` | Each image | alt_text, caption, content_type |
| `comment` | Each comment | author, date, text, resolved |

**Node kind naming:** Uses bare concept names (`heading`, `table`, `comment`) not prefixed (`docx_heading`). The parent document's media type already identifies the format. Cross-format queries like "find all headings" should work without knowing the source format.

**STUB** — Node kind naming is a codebase-wide concern. Markdown uses `md_heading`, XLSX uses `xlsx_worksheet`. This design uses bare names as the aspiration; actual implementation should follow whatever convention is established. If bare names cause collisions, fall back to `docx.heading` (dot-separated, following C#/CSS/Terraform pattern).

**Edges:**

| Type | From | To | Ordinal |
|------|------|----|---------|
| `HAS_PART` | document | heading | heading order in document |
| `HAS_PART` | document | table | position in document |
| `HAS_PART` | document | image | position in document |
| `HAS_PART` | document | comment | comment order |
| `REFERS_TO` | hyperlink reference | target URI | — |

**Spans:** Created for headings and tables — mapping nodes to line ranges in the extracted text. This enables `read("file:///doc.docx#symbol=FeeSchedule")` to return just that section.

### X-Ray Templates

**Headline:**

```liquid
{{ title | default: file_name }} | {{ kind }} | {{ page_count }} pg, {{ token_count | tokens }}{% if top_headings.size > 0 %} | {{ top_headings | join: ", " }}{% endif %}{% if open_comment_count > 0 %} | {{ open_comment_count }} open comment{{ open_comment_count | pluralize: "", "s" }}{% endif %}{% if has_tracked_changes %} | tracked changes{% endif %}{% if form_field_count > 0 %} | {{ form_field_count }} form field{{ form_field_count | pluralize: "", "s" }}{% endif %}
```

Follows the principle from the north-star review: heading text over counts. Open comment count and form field count earn their place because they signal *document state* (under review, fillable template), not content.

**Structure:** Full heading tree with tables and images positioned in flow — same format shown in the north-star document.

### Token Counting

Unlike XLSX (tokens from rendered summaries), Word artifacts carry extracted text. Token count is estimated from `Text` content using `TokenEstimator.EstimateTokensSafe()`. This gives agents an accurate budget for reading the actual document content.

### Error Handling

| Failure | Behavior |
|---------|----------|
| Can't open package (corrupted ZIP) | `PipelineResult.Error` with diagnostic — no partial results possible |
| Password-protected / encrypted | `PipelineResult.Error` with diagnostic explaining encryption |
| Missing main document part | `PipelineResult.Error` — nothing to extract |
| Malformed paragraph XML | Skip paragraph, log warning, continue |
| Table parsing failure | Skip table, log warning, continue — heading tree still valid |
| Image part missing | Record image node with `missing: true` in props |
| Comments part malformed | Skip comments, surface document without collaboration data |
| Style resolution failure | Fall back to style ID string matching (no inheritance walk) |

Each extraction phase (headings, tables, images, comments) is independently try/caught. A corrupt table never prevents heading extraction.

---

## Cross-Cutting Concerns

**URI addressing:** Word documents use the same `file:///path#symbol=HeadingText` pattern as other formats. Heading slugs are generated by the same slugification rules as Markdown headings.

**Search integration:** Extracted text in `Artifact.Text` participates in semantic search automatically. Heading text, table content, and comment text are all searchable.

**View support:** The `tree`, `history`, `blame` views work on `.docx` files like any other. The `question` view can answer questions about document content using the extracted text.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Final state only | Modeling tracked changes as nodes | Complexity containment — 40+ XML elements avoided; state signal preserved via metadata |
| Flat comments | Threaded comment graph | `CommentsExPart` threading is poorly documented; flat comments deliver 90% of value |
| Style-based heading detection | Visual heuristics (font size, bold) | Semantic correctness — styles reflect author intent; heuristics guess |
| Extract text to `Artifact.Text` | `Text = null` (XLSX pattern) | Word content is prose, not tabular data — agents need to read it as text |
| Skip `.doc` entirely | NPOI for legacy support | Different format spec, minimal value in modern repositories |
| Skip text boxes for v1 | Extract text box content | Unknown complexity; can add later without schema changes |

## Alternatives Considered

**Aspose.Words instead of OpenXML SDK:** Higher-level API would simplify text extraction. Rejected: commercial license, and OpenXML SDK is already in the dependency tree. The extraction logic isn't complex enough to justify a new dependency.

**Store rendered Markdown instead of structural text:** Convert the entire document to Markdown and store that. Rejected: lossy (tables, images lose fidelity), and we'd be inventing a conversion format instead of modeling the source document's actual structure.

**Model sections as nodes:** Word documents have section breaks (page layout changes). Rejected: sections are a layout concern, not a content concern. They don't help agents find information. Headings are the navigational structure.

## Risks

| Risk | Mitigation |
|------|------------|
| Documents without styles have empty heading trees | Accurate — the headline shows no structure, which is honest. Agent falls back to full-text search |
| `CommentsExPart` resolved state is underdocumented | Treat missing extended part as "all unresolved" — conservative default |
| Text extraction misses content in unusual locations (text boxes, SmartArt text) | v1 logs a diagnostic when these elements are detected but not extracted. Agents see the gap |
| Node kind naming collision with future formats | **STUB** annotation above — follow codebase convention, revisit when naming is standardized |
| Large documents (100+ pages) produce large extracted text | Token count on headline lets agents make budget decisions. `#symbol=` addressing lets them read sections |

## Extension Points

- `DocumentSurface` can carry additional stories (text boxes, SmartArt text) without changing materialization
- Comment threading can be added by extending `CommentInfo` with `ParentCommentId` — no schema change needed
- Table content can be made queryable via a `read_docx_table()` UDF if demand exists — same pattern as `read_csv_auto()` for CSV
- Kind refinement (spec vs proposal vs report) can be added as a classifier without changing the loader

---

*Extract the skeleton. Preserve the text. Contain the complexity. Everything else is the same pipeline.*
