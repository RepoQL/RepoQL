# Unresolved References: What Great Looks Like

> An agent should be able to find every reference that doesn't resolve — across all files, all formats, all reference types — in one query.

An agent inherits a repository with 2,000 files across markdown, C# projects, PDFs, and Word documents. It asks "what's unresolved?" and gets back a complete inventory: 14 markdown files reference a design doc that was renamed, 3 project files reference a package that no longer exists, a PDF links to a URL that 404s, and two documents reference headings that were removed in last week's refactor. Each unresolved reference shows where it is, what it expected, and why it failed to resolve. The agent fixes the renamed references in one pass, flags the dead URL for a human, and updates the stale anchors. It never opened a file to check. It never missed one. The repository's reference integrity is now a queryable surface, not a manual audit.

---

## Detection

- An agent should be able to find unresolved file references — links to paths that don't exist in the repository
- An agent should be able to find unresolved anchor references — links to headings or sections that don't exist in the target document
- An agent should be able to find unresolved cross-document anchor references — links like `other.md#section` where the file exists but the anchor doesn't
- An agent should be able to find unresolved project references — imports or dependencies that don't resolve
- An agent should be able to find ambiguous anchors — duplicate headings that make anchor links unreliable
- An agent should be able to find dead external URLs — links to resources that no longer respond

---

## Scope

- An agent should be able to check link integrity across every format that contains references — not just markdown
- An agent should be able to validate links that cross format boundaries — a markdown file linking to a PDF, a Word doc referencing a code file
- An agent should be able to validate links across imported repositories — references between repos resolve or fail explicitly
- An agent should be able to trust that link validation is exhaustive within indexed scope — no silent gaps where some links are checked and others aren't

---

## Querying

- An agent should be able to find all unresolved references in one query without knowing which files contain them
- An agent should be able to filter unresolved references by severity, format, rule, or directory
- An agent should be able to see unresolved references grouped by target — "these 14 files all link to the same missing document"
- An agent should be able to see unresolved references grouped by source — "this one file has 7 broken references"
- An agent should be able to trace a link from source to intended target, even when the target is missing
- An agent should be able to distinguish "target doesn't exist" from "target exists but anchor doesn't" from "target hasn't been indexed yet"

```sql
-- All reference integrity issues, anywhere, any format
SELECT source_uri, rule_id, message
FROM Annotations
WHERE rule_id LIKE '%/unresolved-%' OR rule_id LIKE '%/ambiguous-%'

-- Unresolved references grouped by missing target
SELECT data->>'href' AS href, COUNT(*) AS referencing_files
FROM Annotations
WHERE rule_id LIKE '%/unresolved-%'
GROUP BY href
ORDER BY referencing_files DESC
```

---

## Severity and Configuration

- An agent should be able to control which unresolved reference rules are active and at what severity
- An agent should be able to suppress unresolved reference warnings for intentional patterns — known-external URLs, template placeholders, generated paths
- An agent should be able to treat unresolved references in documentation differently from unresolved references in project configuration
- An agent should be able to escalate unresolved references to errors in CI without changing repository configuration

---

## Recovery

- An agent should be able to see what an unresolved reference was probably trying to reference — fuzzy matching against similar paths or headings
- An agent should be able to find when a link target was renamed or moved using git history
- An agent should be able to fix an unresolved reference by seeing the correct current path or anchor
- An agent should be able to verify that a fix resolved the unresolved reference without reindexing the entire repository

---

## Topology

- An agent should be able to see the complete link graph — what references what, whether it resolves or not
- An agent should be able to find orphaned documents — files that nothing links to
- An agent should be able to find documents whose incoming links are all broken — targets that were effectively abandoned
- An agent should be able to find link cycles and clusters — groups of documents that form a connected subgraph
- An agent should be able to ask "if I rename this file, what breaks?" before making the change

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Find every unresolved reference in one query | Reference integrity becomes a queryable surface, not a manual audit |
| Cross-format, cross-document validation | A renamed file breaks links in markdown, Word, and project files — find them all |
| Distinguish missing target from missing anchor from not-yet-indexed | Actionable diagnosis, not just "broken" |
| Group by target | 14 files linking to a renamed doc is one fix, not fourteen |
| See what a link was probably trying to reference | Recovery, not just detection |
| "If I rename this, what breaks?" | Prevention before repair |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Open files to check if links work | An agent should be able to query unresolved references across all files |
| Check only markdown links | An agent should be able to validate links across every format that contains references |
| Report "broken" with no diagnosis | An agent should be able to distinguish why a link is broken |
| Find unresolved references but not suggest fixes | An agent should be able to see what the link was probably trying to reference |
| Treat all unresolved references equally | An agent should be able to control severity per rule, format, and context |

---

*An agent should be able to trust a repository's references the way it trusts its tests — validated continuously, failures surfaced immediately, fixes guided by the tool itself.*
