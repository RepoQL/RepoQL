# Format Support: What Great Looks Like

> Every file in a repository has structure. An agent should be able to query that structure without reading the file.

An agent lands in an unfamiliar repository — 12,000 files across 30 languages, configs, schemas, docs, data files, build scripts. It doesn't open a single one. It scans headlines and knows what each file is: a service class with 8 public methods, a design doc about caching, a GraphQL schema with 40 types, an Excel workbook with 3 sheets of test data, a CSS file defining a design system. It filters to the 50 files relevant to authentication, reads their structures — method signatures, heading trees, type definitions, config keys — and understands the system. It queries the graph: "what depends on TokenService?" and gets answers that cross file boundaries — C# callers, configuration references, documentation links, test coverage. Every format spoke its own syntax. The agent heard one language.

---

## Legibility

- An agent should be able to understand what any file contains from a single-line headline
- An agent should be able to see a file's internal structure without reading its content
- An agent should be able to distinguish files of the same extension by what they actually contain (a `.json` config vs a `.json` data file vs a `package.json`)
- An agent should be able to scan 1000 files and filter to 20 candidates without opening any
- An agent should be able to judge the cost of reading a file before committing — every headline carries 1-2 size proxies, and one must be approximate token count

---

## Progressive Disclosure

- An agent should be able to choose its depth of understanding: existence, relevance, structure, or content
- An agent should be able to see actual items at every level — method names not method counts, heading text not heading counts, package names not package counts
- An agent should be able to navigate from headline to structure to specific content without re-querying
- An agent should be able to read a single symbol, section, or region without paying for the whole file
- An agent should be able to find any element through semantic search if it appears in a file's structure — structure is vector-indexed, and what's hidden can't be found
- An agent should be able to see a file's complete structure without truncation — a concise representation of every element, not a verbose representation of some elements
- An agent should be able to read any file's content as text, regardless of whether the file is text — the format handler decides what "content" means (e.g. extracted text for PDFs, descriptions for images, manifests for archives, parsed data for spreadsheets)

```
headline  →  "PaymentService.cs | PaymentService : IPaymentService | ProcessPayment, Refund | 450 ln, ~2.1k tok"
structure →  +Task<PaymentResult> ProcessPayment(PaymentRequest request)    #symbol=ProcessPayment
content   →  read("file:///src/PaymentService.cs#symbol=ProcessPayment", 2000)

headline  →  "Q3-Report.pdf | 42 pages, ~18k tok | Financial Results, Risk Factors, Outlook"
structure →  Table of Contents: Executive Summary, Revenue Breakdown, Regional Analysis, ...
content   →  extracted text, not raw bytes if not a text format
```

---

## Format Essence

- An agent should be able to query each format in terms natural to that format — headings for docs, types for code, keys for config, endpoints for APIs, sheets for spreadsheets
- An agent should be able to ask the same structural question across formats and get format-appropriate answers
- An agent should be able to trust that the graph captures what the format means, not how it's parsed

Every format has a natural essence — the question someone asks when they encounter it. Code: "what can I call?" Docs: "what does this argue?" Config: "what knobs exist?" The format handler's job is to find that question and make structure answer it. If the structure feels forced, the essence hasn't been found yet.

```sql
-- Each format speaks its own language through the query surface
SELECT * FROM markdown_headings WHERE level = 2
SELECT * FROM Functions WHERE declaring_type = 'PaymentService'
SELECT * FROM csharp_enums WHERE name = 'OrderStatus'
```

---

## Relationships

- An agent should be able to traverse relationships that cross file and format boundaries
- An agent should be able to find what depends on a given entity — callers, importers, linkers, referencers
- An agent should be able to discover relationships that are implicit in syntax but explicit in the graph — a project depending on a package, a doc linking to a heading, a type implementing an interface
- An agent should be able to query the full dependency graph of any entity without knowing which files contain it

---

## Composition

- An agent should be able to get diagnostics on content embedded inside other formats — code blocks in markdown, SQL in strings, schemas in config
- An agent should be able to trust that embedded analysis results map correctly to the containing file's coordinates
- An agent should be able to find all instances of a format regardless of whether they're standalone files or embedded fragments

---

## Integrity

- An agent should be able to find broken references across all files in one query — broken links, missing imports, unresolved dependencies
- An agent should be able to trust that diagnostics are actionable problems, not style preferences
- An agent should be able to configure diagnostic severity per rule, per file pattern, including disabling rules entirely
- An agent should be able to find problems that only appear at repository scale — duplicate identifiers, conflicting declarations, orphaned files

---

## Uniformity

- An agent should be able to use the same tools (explore, query, read) on any format
- An agent should be able to combine results from different formats in a single SQL query
- An agent should be able to learn the query patterns once and apply them to any format
- An agent should be able to search semantically across all formats simultaneously

```sql
-- Cross-format query: what changed recently in auth-related files?
SELECT f.uri, f.lang, f.headline
FROM Files f
JOIN search('authentication', k := 20) s ON s.uri = f.uri
ORDER BY f.lang
```

---

## Extensibility

- An agent should be able to benefit from a new format handler without learning new tools or syntax
- Adding a format should be adding a parser, a materializer, and templates — not modifying the core
- A new format should automatically participate in search, explore, read, and query
- A format author should be able to express the format's essence through the existing graph schema without new tables

---

## Failure

- An agent should be able to trust that a single malformed file never prevents other files from being indexed
- An agent should be able to see which files failed to parse and why
- An agent should be able to get partial results from a file that partially parsed — some structure is better than none
- An agent should be able to distinguish "this file has no structure" from "this file failed to parse"

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Understand any file from its headline | 12,000 files become navigable in one scan |
| See complete structure without truncation | Hidden elements can't be found — concise beats verbose |
| Find any structural element through search | Structure is vector-indexed; what's omitted is invisible |
| Query each format in its natural terms | Agents think in headings, types, endpoints — not nodes and edges |
| Traverse relationships across format boundaries | "What depends on X?" works regardless of format |
| Get embedded content diagnosed in place | A broken GraphQL schema in a markdown doc is found, not hidden |
| Same tools work on every format | Learn once, query anything |
| New formats join automatically | Extensibility without disruption |
| One bad file never breaks the index | Trust at repository scale |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Open files to understand what they contain | An agent should see structure from headlines |
| Truncate structure with "[6 more items...]" | An agent should see every element — pick a concise representation instead |
| Build format-specific query tools | An agent should query all formats through SQL |
| Model parser syntax in the graph | An agent should query what the format means |
| Ignore embedded content | An agent should get diagnostics on content wherever it appears |
| Let one failure cascade | An agent should trust every file is independently indexed |
| Require format-specific knowledge to search | An agent should search all formats with one query |

---

*An agent should be able to query the structure of any file in any format through one surface — and trust that the answer reflects what the file actually contains.*
