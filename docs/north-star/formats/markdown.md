# Markdown Format: What Great Looks Like

> An agent should understand what a document argues, how it's organized, and what it connects to—without reading it.

An agent exploring a repository encounters 400 markdown files. It scans 400 headlines and knows what each document is about—an API guide, a design doc, a runbook, a changelog. It narrows to 30 documents about authentication, reads their structures, and sees the actual heading trees: which sections exist, what they cover, how deep they go. It picks 3 documents, reads specific sections by heading slug, and understands the authentication architecture. It never opened a file it didn't need. It found a broken link between two docs and a code block with invalid GraphQL—both surfaced as annotations before anyone asked. The gap between "what docs do we have?" and "what do they say?" disappeared.

---

## Discovery

- An agent should be able to distinguish document types (guide, design, runbook, changelog, ADR) from a headline alone
- An agent should be able to see a document's topic, scope, and scale without opening it
- An agent should be able to find documents by frontmatter metadata (tags, author, status) through the query surface
- An agent should be able to search documents by what they argue, not just what words they contain

---

## Structure

- An agent should be able to see a document's complete heading tree — every heading, no truncation
- An agent should be able to read any section by its heading slug without reading the whole file
- An agent should be able to see code block languages and locations without opening the document
- An agent should be able to understand a document's arc — what it opens with, what it builds toward, what it concludes with

```
headline  →  "auth-design.md | Authentication Design | markdown.doc | 4.2 KB, ~1.1k tok"
structure →
  # Authentication Design
    ## Problem Statement
    ## Approach
      ### Token Flow
      ### Refresh Strategy
    ## Decision
    ## Open Questions
read("file:///docs/auth-design.md#approach") → just that section
```

---

## Connections

- An agent should be able to trace every link in a document to its target—file, heading, URL
- An agent should be able to find all documents that link to a given document
- An agent should be able to discover which documents reference which code files
- An agent should be able to find orphaned documents that nothing links to
- An agent should be able to find clusters of documents that form a topic (mutual linking, shared tags)

```sql
-- What links to the schema doc?
SELECT file_uri, link_text
FROM markdown_links
WHERE href LIKE '%Schema.md%'
```

---

## Embedded Content

- An agent should be able to get diagnostics on code blocks without extracting them to files
- An agent should be able to see which languages appear in a document's code blocks and where
- An agent should be able to trust that a SQL example in documentation is valid SQL
- An agent should be able to find all code examples of a given language across all markdown files

```sql
-- All GraphQL examples in docs
SELECT file_uri, start_line
FROM markdown_codeblocks
WHERE language = 'graphql'
```

---

## Frontmatter

- An agent should be able to query documents by any frontmatter key-value pair
- An agent should be able to find documents with specific tags, audiences, or statuses
- An agent should be able to see frontmatter metadata in headlines and summaries without opening files
- An agent should be able to use frontmatter to distinguish document types when file paths are ambiguous

---

## Cross-Document Integrity

- An agent should be able to find every broken link across all markdown files in one query
- An agent should be able to find broken anchor references (links to headings that don't exist)
- An agent should be able to find broken file references (links to files that don't exist)
- An agent should be able to find duplicate headings that create ambiguous anchors
- An agent should be able to trust that link validation covers both local and cross-document references

---

## Tables, Lists, and Structured Content

- An agent should be able to see tables as queryable data, not just rendered text
- An agent should be able to find documents containing decision tables, comparison matrices, or checklists
- An agent should be able to extract task list completion status across documents
- An agent should be able to find definition lists and glossary entries

---

## Document Intelligence

- An agent should be able to identify a document's likely purpose from its structure (tutorial has steps, reference has tables, ADR has status+decision)
- An agent should be able to find documents that need attention (broken links, missing sections, stale references)
- An agent should be able to compare document structures across a repository to find inconsistencies
- An agent should be able to find sections that are unusually long, empty, or structurally orphaned

---

## Capsules

- An agent should be able to find capsules by name across all documents
- An agent should be able to read a capsule's invariant without reading the containing document
- An agent should be able to trace capsule cross-references (SeeAlso) to related capsules
- An agent should be able to query the capsule graph independently of document structure

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Distinguish document types from headlines alone | 400 files become navigable in one scan |
| See full heading tree as navigable outline | Find the right section without reading the file |
| Trace every link to its target | Broken links found before they break workflows |
| Get diagnostics on embedded code blocks | Documentation quality matches code quality |
| Query by frontmatter metadata | Documents become first-class queryable entities |
| Find all documents linking to a given target | Understand documentation topology |
| See tables as queryable structure | Structured content in docs isn't trapped in prose |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Read a document to learn its topic | An agent should be able to see the topic from the headline |
| Open a file to check if a link works | An agent should be able to query broken links across all files |
| Guess which docs cover a concept | An agent should be able to search by what documents argue |
| Ignore code blocks in documentation | An agent should be able to lint embedded code in place |
| Treat markdown as flat text | An agent should be able to navigate by heading, link, and section |

---

*An agent should be able to understand a repository's documentation as a connected knowledge surface—structured, queryable, and trustworthy—without reading a single file end to end.*
