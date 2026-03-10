# Word Document Format: What Great Looks Like

> An agent should understand what a Word document argues, how it's organized, and what structured content it contains — without opening it.

An agent exploring a repository encounters 80 Word documents scattered across specs, proposals, contracts, reports, and templates. It scans 80 headlines and knows what each one covers: a technical specification spanning Executive Summary through API Spec to Appendix, a contract amendment with open comments from two reviewers, a proposal template with placeholder headings and form fields, a quarterly report covering Revenue, Regional Analysis, and Outlook. It narrows to 12 documents about the billing system, reads their structures — heading trees with tables placed in context, comment threads anchored to specific sections — and understands the project history: the original spec, three rounds of review comments, a final sign-off, and a change request that was never resolved. It finds the table in the spec that defines the fee schedule, reads just that table, and understands the pricing model. It never extracted a binary. It never guessed what a file contained. The format handler turned 80 opaque `.docx` files into 80 queryable documents.

---

## Discovery

- An agent should be able to understand what a Word document contains from a single-line headline
- An agent should be able to distinguish document types (specification, proposal, report, contract, template, letter, manual) from structure alone
- An agent should be able to see page count, top-level headings, and approximate token cost without opening the file
- An agent should be able to scan 200 documents and filter to the 5 relevant ones without reading any
- An agent should be able to tell the difference between a finished report and a half-written draft from the headline

```
headline  →  "billing-spec-v3.docx | docx.specification | 42 pages, ~18k tok | Executive Summary, System Overview, Fee Schedule, API Spec, Timeline, Appendix"
headline  →  "Q3-review.docx | docx.report | 8 pages, ~3.2k tok | Executive Summary, Revenue, Regional Analysis, Outlook"
headline  →  "nda-template.docx | docx.template | 2 pages, ~0.4k tok | Parties, Terms, Signatures | 5 form fields"
headline  →  "api-change-request.docx | docx.document | 5 pages, ~2.1k tok | Background, Proposed Changes, Impact, Approval | 12 open comments"
```

Top-level headings tell you what the document covers — a count never does. Counts earn their place only when they signal *state* rather than content: "12 open comments" tells you the document is under active review, which changes how you approach it. "5 form fields" tells you this is a fillable template, not a static document. These are decisions, not inventories.

---

## Structure

- An agent should be able to see a document's complete heading tree — every heading, derived from paragraph styles, no truncation
- An agent should be able to read any section by its heading without reading the whole document
- An agent should be able to see where tables, images, and lists appear in the document's flow
- An agent should be able to understand a document's arc from its structure alone — what it opens with, what it builds toward, what it concludes with
- An agent should be able to navigate by heading slug the same way it navigates Markdown — `#symbol=ExecutiveSummary` works

The heading tree is the skeleton. A 42-page specification with six top-level sections and three levels of nesting tells you exactly what the document covers before you read a word. The format handler extracts this from Word's style system — Heading 1 through Heading 9 — not from font sizes or bold text. Styles are semantic. Formatting is not.

```
structure →
  # Technical Specification: Billing Engine v3
    ## 1. Executive Summary
    ## 2. System Overview
      ### 2.1 Architecture
        Table: Component Responsibilities (4 cols, 8 rows)
      ### 2.2 Data Flow
        [Image: System Architecture Diagram]
    ## 3. Fee Schedule
      Table: Standard Fee Matrix (6 cols, 24 rows)
      Table: Volume Discount Tiers (3 cols, 5 rows)
    ## 4. API Specification
      ### 4.1 Endpoints
        Table: REST API Reference (5 cols, 12 rows)
      ### 4.2 Error Codes
        Table: Error Code Reference (3 cols, 30 rows)
    ## 5. Implementation Timeline
      [Numbered list: 5 phases]
    ## 6. Appendix
      Table: Glossary (2 cols, 18 rows)

read("file:///docs/billing-spec-v3.docx#symbol=FeeSchedule", 3000)  →  just that section with its tables
```

---

## Document Properties

- An agent should be able to see a document's title, author, creation date, and last-modified date from the graph — without opening the file
- An agent should be able to query documents by author, by date range, by custom property
- An agent should be able to find documents whose title doesn't match their filename
- An agent should be able to see custom document properties (department, status, version, classification) as queryable metadata

Document properties are Word's equivalent of frontmatter. They're set explicitly by authors or by templates, and they carry intent: who wrote this, when, for what purpose. A template that sets `Status: Draft` and `Department: Legal` on every document it creates makes those 200 legal drafts filterable in one query.

```sql
-- Who authored the most specs?
SELECT f.author, COUNT(*) AS docs
FROM Files f
WHERE f.lang = 'docx'
GROUP BY f.author
ORDER BY docs DESC

-- Find documents modified in the last quarter
SELECT f.uri, f.headline, f.modified
FROM Files f
WHERE f.lang = 'docx'
  AND f.modified > CURRENT_DATE - INTERVAL '90 days'
```

---

## Tables

- An agent should be able to see every table's dimensions, header row, and column names without reading the document
- An agent should be able to read a specific table by its position or nearby heading
- An agent should be able to distinguish data tables from layout tables (tables used for formatting, not data)
- An agent should be able to see merged cells and spanning headers as structural facts, not rendering artifacts
- An agent should be able to find tables across documents by column name or content pattern

Tables in Word documents are where the structured data lives. A specification's fee schedule, an API reference's endpoint table, a test plan's case matrix — these are the parts agents actually need. The heading tree says where to look. The table inventory says what's there. Reading the table says what it contains. Three levels, each cheaper than reading the whole document.

```
read("file:///docs/billing-spec-v3.docx#symbol=StandardFeeMatrix", 2000)  →

  Standard Fee Matrix (6 cols, 24 rows)
  ┌──────────┬────────┬──────┬───────────┬──────────┬───────────┐
  │ Category │ Type   │ Rate │ Min Fee   │ Max Fee  │ Currency  │
  ├──────────┼────────┼──────┼───────────┼──────────┼───────────┤
  │ Payment  │ Credit │ 2.9% │ $0.30     │ $50.00   │ USD       │
  │ Payment  │ Debit  │ 1.5% │ $0.15     │ $25.00   │ USD       │
  │ ...      │        │      │           │          │           │
  └──────────┴────────┴──────┴───────────┴──────────┴───────────┘
```

---

## Comments and Tracked Changes

- An agent should be able to see all comments on a document with their authors, dates, and the text they annotate
- An agent should be able to see all tracked changes with their type (insertion, deletion, formatting), author, and date
- An agent should be able to find documents with unresolved comments or pending changes
- An agent should be able to trace a review conversation — comment, reply, resolution — as a thread
- An agent should be able to query the review state across documents: "which specs have unresolved comments from the legal team?"

Comments and tracked changes are the collaboration record. They tell you not just what the document says, but what was debated, what was challenged, what was agreed. A specification with 30 resolved comments and no pending changes is finished. One with 12 open comments concentrated in section 4 has a known problem area. This is discoverable from the graph without reading the document.

```sql
-- Documents with unresolved review comments
SELECT doc.uri, doc.headline, COUNT(*) AS open_comments
FROM document_comments c
JOIN Files doc ON doc.uri = c.file_uri
WHERE c.resolved = false
GROUP BY doc.uri, doc.headline
ORDER BY open_comments DESC

-- What did a specific reviewer flag?
SELECT doc.uri, c.author, c.text
FROM document_comments c
JOIN Files doc ON doc.uri = c.file_uri
WHERE c.author LIKE '%Sarah%'
```

The query surface should expose comments as a view — agents shouldn't need to know the underlying node kind or join pattern. Whether the implementation uses `docx_comment` or `comment` or something else is a concern for the format handler, not the agent.

---

## Content

- An agent should be able to read a Word document's text content without paying for the binary format
- An agent should be able to read specific sections, specific pages, or specific elements (a table, a list)
- An agent should be able to see text with structural markers (headings, list items, table boundaries) but without formatting noise (bold, italic, font changes)
- An agent should be able to trust that the extracted text preserves the document's reading order — body text, footnotes, endnotes in logical sequence
- An agent should be able to get a useful representation at any budget — 500 tokens gets the heading tree, 2000 gets headings plus table summaries, 5000 gets readable prose

Word's binary format is a packaging problem, not a complexity problem. The content is text with structure. The format handler's job is to extract the text, preserve the structure, and discard the rendering. An agent reading a Word document should have the same experience as reading Markdown — navigable by heading, readable by section, searchable by content — just sourced from `.docx` instead of `.md`.

---

## Images

- An agent should be able to see which images a document contains, with their captions, and where they appear in the document's flow
- An agent should be able to see alt text and captions for images that have them
- An agent should be able to find documents that contain images without alt text (accessibility diagnostic)
- An agent should be able to distinguish diagrams, charts, logos, and photographs by context (surrounding headings, captions)

Images can't be queried as text, but their metadata and context can. A diagram under "2.2 Architecture" with the caption "System Architecture Diagram" tells you exactly what it depicts. An image with no alt text and no caption is an accessibility problem and an indexing gap — both worth surfacing.

---

## Relationships

- An agent should be able to find code files, configs, or other documents that a Word document references by name
- An agent should be able to find all documents that describe a given system, API, or component
- An agent should be able to trace from a specification's table to the code that implements it
- An agent should be able to find the Markdown docs that cover the same topic as a Word doc (cross-format discovery)
- An agent should be able to discover document families — a spec and its amendments, a template and its instances

Word documents don't exist in isolation. A billing specification describes the system that `BillingService.cs` implements. A test plan references the API endpoints that `openapi.yaml` defines. The graph connects them — not because the format handler understands billing or testing, but because it exposes the names, references, and structural elements that the graph can match across formats.

---

## Integrity

- An agent should be able to find Word documents that are corrupted or password-protected, distinguished from documents that simply have no content
- An agent should be able to find documents with broken internal references (cross-references to missing bookmarks, TOC entries that don't match headings)
- An agent should be able to find documents with missing images (referenced but not embedded)
- An agent should be able to trust that a malformed document still gets partial indexing — properties and whatever structure is extractable, with diagnostics on what failed
- An agent should be able to find template documents (containing form fields, content controls, or placeholder text)

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Understand any Word document from its headline | 80 opaque binaries become navigable in one scan |
| See complete heading tree from paragraph styles | Navigate a 50-page spec without opening it |
| See tables as structured, queryable data | The spec's fee schedule is findable and readable |
| Surface comments and tracked changes as graph entities | Review state is queryable across the entire corpus |
| Extract text content without binary overhead | Reading a section of a .docx feels like reading Markdown |
| Query document properties like frontmatter | Author, date, status, version — all filterable |
| Find images with missing alt text | Accessibility problems surfaced automatically |
| Connect documents to the code they describe | Specs, code, tests, and configs form one graph |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Open a Word document to learn what it contains | An agent should see the topic, scale, and structure from the headline |
| Extract text to a temp file and index that | An agent should read .docx content through the same URI surface as any format |
| Ignore tables as "too complex" | An agent should see tables as the most valuable structured content in a document |
| Treat comments as noise | An agent should see comments as the review record — who questioned what, and whether it was resolved |
| Depend on filename for document type | An agent should classify documents by structure (heading patterns, properties, content) |
| Strip all formatting to plain text | An agent should preserve structural markers (headings, lists, tables) while discarding visual formatting |
| Index every paragraph as a node | An agent should index structural elements (headings, tables, images, comments) — paragraphs are content, not structure |
| Fail completely on a corrupted file | An agent should get partial results with diagnostics on what couldn't be parsed |

---

*An agent should be able to understand a repository's Word documents as structured, queryable, connected artifacts — navigable from headline to heading tree to table to text — without ever confronting the binary format underneath.*
