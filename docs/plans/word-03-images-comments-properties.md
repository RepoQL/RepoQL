---
description: Plan for Word format loader — images, comments, and document properties
tags: [format, word, docx, plan, comments, images, properties]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Word Loader — Images, Comments, and Document Properties

Implements: [Word Document Format Design](../designs/current/word-format.md) — Comments, Document Properties, Images, Graph Materialization (image/comment nodes), X-Ray Templates (headline enrichment)

## Scope

**Covers:**
- Image extraction (alt text, caption detection, content type, embedded vs linked)
- Comment extraction (author, date, text, anchor range, resolved state via `CommentsExPart`)
- Document properties (core: title, author, created, modified; extended: page count, word count; custom properties)
- Image nodes, comment nodes with `HAS_PART` edges
- Image and comment position markers in body text
- Headline template enrichment: open comment count, tracked changes signal, form field count
- Document node props enriched with full property set
- Accessibility diagnostic: images without alt text
- Tests for image, comment, and property scenarios

**Does not cover:**
- Comment threading / reply chains (extension point — design defers to v2)
- Image content analysis or OCR
- SmartArt or chart extraction

## Enables

Once this exists:
- **Agents can query review state** — "which specs have unresolved comments?" works across the corpus
- **Agents can find documents by author, date, or custom property** — document properties are queryable metadata
- **Agents can see image inventory** — where images appear, what they depict (via alt text/caption)
- **Accessibility diagnostics** — images without alt text surfaced as annotations
- **Headlines signal document state** — open comments and tracked changes visible in explore results

## Prerequisites

- Plan: word-01-skeleton-text-headings complete — loader, surface model, materialization pipeline
- Plan: word-02-tables complete (soft dependency — images and comments are independent, but the surface model additions follow the same pattern established by tables)

## North Star

An agent scanning 80 Word documents should be able to answer "which ones have open review comments?" and "who authored the billing specs?" without opening any of them. Review state is a first-class queryable signal, not hidden inside the binary.

## Done Criteria

### Document Properties
- The loader shall extract core properties: title, author, last modified by, created date, modified date, description, subject, keywords
- The loader shall extract extended properties: page count, word count, paragraph count, application name
- The loader shall extract custom properties as key-value pairs
- The document node props shall include all extracted properties
- When core properties are missing (no `CoreFilePropertiesPart`), the loader shall use defaults (filename for title, empty for others)

### Images
- The loader shall identify images from `Drawing` and `Picture` elements in paragraph runs
- For each image, the loader shall extract: alt text (from `DocProperties`), content type (JPEG, PNG, etc.), relationship type (embedded vs linked)
- The loader shall detect captions by checking the paragraph immediately following an image for caption style (`Caption` style ID) or `SEQ` field codes
- The loader shall insert `[Image: alt text]` markers at each image's position in body text
  - When alt text is empty, use `[Image]`
- The materializer shall create one node per image with alt_text, caption, content_type in props
- The materializer shall create `HAS_PART` edges from document to each image with ordinals
- When an image relationship references a missing part, the image node shall include `missing: true` in props

### Image Accessibility Diagnostic
- When an image has no alt text and no caption, the loader shall emit an annotation with kind `lint`, severity `warning`, rule_id `docx.image-no-alt`, and a message identifying the image position
- The annotation shall reference the image's span in the extracted text

### Comments
- The loader shall extract comments from `CommentsPart` (`word/comments.xml`)
- For each comment, the loader shall extract: id, author, date, text content (concatenated paragraph runs)
- The loader shall extract the anchor range: start and end paragraph indices that the comment annotates
- The loader shall attempt to read resolved state from `WordprocessingCommentsExPart` (`word/commentsExtensible.xml`)
  - When extended part exists, match comments by `paraId` and read `done` attribute
  - When extended part is missing, treat all comments as unresolved
- The materializer shall create one node per comment with author, date, text, resolved in props
- The materializer shall create `HAS_PART` edges from document to each comment with ordinals

### Headline Enrichment
- When the document has open (unresolved) comments, the headline shall include the count: `3 open comments`
- When the document has tracked changes (from Plan 01 metadata), the headline shall include `tracked changes`
- When the document has form fields (content controls with `SdtProperties`), the headline shall include the count: `5 form fields`

### Tests
- Test document with core, extended, and custom properties — verify all extracted to document node props
- Test document with no properties part — verify defaults applied
- Test document with images: with alt text, without alt text, with caption, missing image part
- Test alt text accessibility diagnostic — verify annotation created for images without alt text
- Test document with comments: single comment, multiple comments, comments on different paragraphs
- Test document with `CommentsExPart` resolved state — verify resolved flag
- Test document without `CommentsExPart` — verify all comments treated as unresolved
- Test headline rendering with open comments and tracked changes
- Test malformed comments part — verify skip and continue

## Constraints

- **Flat comments only** — no threading via `CommentsExPart` `paraIdParent`; design defers threading to v2
- **No image content** — images are inventory nodes (metadata about what's there), not content nodes; no extraction of image bytes or OCR
- **Caption detection is heuristic** — adjacent paragraph with caption style or SEQ field; won't catch all caption patterns
- **Form field detection is lightweight** — count content controls (`SdtBlock`, `SdtRun`), don't parse their structure

## References

- [Word Format Design](../designs/current/word-format.md) — Comments, Images, Document Properties sections
- [Word Format North Star](../north-star/formats/word.md) — Comments and Tracked Changes, Images, Document Properties sections
- `DocumentFormat.OpenXml.Wordprocessing` — `Comment`, `Comments`, `Drawing`, `Picture`, `SdtBlock`, `SdtRun`
- `DocumentFormat.OpenXml.Packaging` — `CoreFilePropertiesPart`, `ExtendedFilePropertiesPart`, `CustomFilePropertiesPart`, `WordprocessingCommentsPart`
- XLSX loader properties extraction (`src/Formats/RepoQL.Formats.Xlsx/XlsxLoader.cs`) — analogous pattern for document properties

## Error Policy

Each extraction phase is independently try/caught:
- **Properties:** If core/extended/custom parts fail, use defaults. Never block on metadata.
- **Images:** If an image element fails to parse, skip it. Log warning with position.
- **Comments:** If `CommentsPart` fails, surface document without comments. If `CommentsExPart` fails, treat all as unresolved. Log warning.
- **Accessibility annotations:** If diagnostic emission fails, skip silently — diagnostics are best-effort.
