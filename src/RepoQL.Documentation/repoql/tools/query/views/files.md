---
description: "Files(uri, source, path, dirname, name, extension, lang, mime, byte_size, lines, headline, summary, structure, mtime, error_count, warning_count, node_id, artifact_id)"
tags: ["Files", "Documents", "Inventory", "Diagnostics", "Xray", "Codebase", "SQL"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Files View

Denormalized view of all indexed documents with identity, type, size, diagnostics, and x-ray columns.

## Quick Reference

```sql
-- Full inventory
SELECT * FROM Files;

-- Codebase stats by language
SELECT lang, COUNT(*) as files, SUM(lines) as total_lines
FROM Files GROUP BY lang ORDER BY files DESC;

-- Files with lint errors
SELECT uri, error_count, warning_count FROM Files WHERE error_count > 0;
```

---

## Capsule: FilesBasic

**Invariant**
`Files` joins `node` + `artifact` + `annotation` for documents, exposing all common attributes in one flat view.

**Example**
```sql
SELECT uri, lang, lines FROM Files WHERE lang = 'code.csharp';
SELECT name, byte_size FROM Files ORDER BY byte_size DESC LIMIT 5;
SELECT * FROM Files WHERE source LIKE 'github://%';
```
//BOUNDARY: Only documents (kind='document'); sub-document nodes (symbols, sections) are not included.

**Depth**
- Pre-joined: no manual joins needed for common queries
- Includes x-ray summaries (`headline`, `summary`, `structure`)
- Includes diagnostics (`error_count`, `warning_count`)
- Use `node_id`/`artifact_id` for advanced joins
- SeeAlso: `Types`, `Functions` views for sub-document entities

---

## Capsule: FilesFiltering

**Invariant**
Filter by language, extension, path, directory, or source using standard WHERE clauses.

**Example**
```sql
-- By language (from media type)
SELECT uri FROM Files WHERE lang = 'code.csharp';
SELECT uri FROM Files WHERE lang = 'markdown.doc';

-- By extension
SELECT uri FROM Files WHERE extension = '.cs';
SELECT uri FROM Files WHERE extension IN ('.ts', '.tsx', '.js');

-- By path pattern
SELECT uri FROM Files WHERE path LIKE '/src/Services/%';
SELECT uri FROM Files WHERE dirname LIKE '%/tests/%';

-- By source (for imports)
SELECT uri FROM Files WHERE source = 'github://owner/repo';
SELECT uri FROM Files WHERE source = 'file://';
```
//BOUNDARY: `lang` comes from semantic media type; `extension` is literal file extension. They may differ.

**Depth**
- `lang`: Semantic type from indexer (`code.csharp`, `markdown.doc`, `dotnet.csproj`)
- `extension`: Raw file extension (`.cs`, `.md`, `.csproj`)
- `source`: Scheme prefix; `file://` for local, `github://owner/repo` for imports
- `dirname`: Parent directory path; use LIKE for nested matching
- Case-sensitive by default; use `LOWER()` for case-insensitive

---

## Capsule: FilesAggregation

**Invariant**
GROUP BY columns to compute codebase statistics (file counts, line totals, size distributions).

**Example**
```sql
-- Language distribution
SELECT lang, COUNT(*) as files, SUM(lines) as lines
FROM Files GROUP BY lang ORDER BY files DESC;

-- Extension breakdown
SELECT extension, COUNT(*) FROM Files GROUP BY extension;

-- Directory sizes
SELECT dirname, COUNT(*) as files, SUM(byte_size) as bytes
FROM Files GROUP BY dirname ORDER BY bytes DESC LIMIT 10;

-- Source breakdown (local vs imports)
SELECT source, COUNT(*) FROM Files GROUP BY source;
```

**Depth**
- `SUM(lines)` for total line count
- `SUM(byte_size)` for total size
- `AVG(lines)` for average file length
- Combine with WHERE for filtered stats: `WHERE lang = 'code.csharp'`

---

## Capsule: FilesDiagnostics

**Invariant**
`error_count` and `warning_count` aggregate lint annotations per file.

**Example**
```sql
-- Files with errors
SELECT uri, error_count FROM Files WHERE error_count > 0 ORDER BY error_count DESC;

-- Files with any diagnostics
SELECT uri, error_count, warning_count
FROM Files WHERE error_count + warning_count > 0;

-- Error-free codebase check
SELECT COUNT(*) as clean_files FROM Files WHERE error_count = 0;

-- Most problematic files
SELECT uri, error_count + warning_count as issues
FROM Files ORDER BY issues DESC LIMIT 10;
```
//BOUNDARY: Counts are pre-aggregated; for individual annotations use `annotations` view or `annotations_for()`.

**Depth**
- Aggregates `annotation` table where `severity = 'error'` or `'warning'`
- COALESCE to 0 for files with no annotations
- Use `annotations_for(uri, 'lint', 'error')` for detailed diagnostics
- SeeAlso: `annotations` view for raw annotation access

---

## Capsule: FilesXray

**Invariant**
X-ray columns provide pre-computed summaries: `headline` (one-line), `summary` (brief), `structure` (detailed).

**Example**
```sql
-- Quick inventory with headlines
SELECT uri, headline FROM Files LIMIT 20;

-- Summaries for understanding
SELECT uri, summary FROM Files WHERE lang = 'markdown.doc';

-- Structure for detailed outline
SELECT uri, structure FROM Files WHERE name = 'README.md';

-- Token-efficient scanning
SELECT uri, headline FROM Files WHERE headline LIKE '%authentication%';
```
//BOUNDARY: X-ray content varies by file type; some files may have NULL summary/structure.

**Depth**
- `headline`: ~50-100 chars, includes name, type, size, line count
- `summary`: ~200-500 chars, key content overview
- `structure`: Full outline/TOC; can be large for complex files
- Generated during indexing; reflects file at index time
- Use for scanning without reading full content

---

## Common Patterns

| Goal | Query |
|------|-------|
| All files | `SELECT * FROM Files` |
| By language | `WHERE lang = 'code.csharp'` |
| By extension | `WHERE extension = '.md'` |
| By directory | `WHERE dirname LIKE '%/src/%'` |
| Imported repos | `WHERE source LIKE 'github://%'` |
| With errors | `WHERE error_count > 0` |
| Largest files | `ORDER BY lines DESC LIMIT 10` |
| Language stats | `GROUP BY lang` |
| Quick scan | `SELECT uri, headline FROM Files` |

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `uri` | string | Full RepoQL URI |
| `source` | string | Scheme prefix (`file://`, `github://owner/repo`) |
| `path` | string | Path without scheme |
| `dirname` | string | Parent directory |
| `name` | string | Filename with extension |
| `extension` | string | File extension (`.cs`, `.md`) or NULL |
| `lang` | string | Semantic language/kind from media type |
| `mime` | string | Base MIME type |
| `byte_size` | integer | File size in bytes |
| `lines` | integer | Line count (NULL for binary) |
| `headline` | string | X-ray one-line summary |
| `summary` | string | X-ray brief overview |
| `structure` | string | X-ray detailed structure |
| `mtime` | timestamp | Last modified time |
| `error_count` | integer | Lint error count (0 if none) |
| `warning_count` | integer | Lint warning count (0 if none) |
| `node_id` | uuid | Foreign key to `node` table |
| `artifact_id` | uuid | Foreign key to `artifact` table |
