---
description: "RepoQL quickstart - xray and read for exploration, query for SQL, search for discovery. Views: Files, Types, Functions, Annotations."
tags: ["quickstart", "xray", "read", "query", "search", "views"]
audience: ["LLMs"]
categories: ["Guide[100%]"]
---

# RepoQL Quickstart

RepoQL indexes your codebase into a queryable graph. Three tools, progressive depth.

## Tools Overview

| Tool | Purpose | When to Use |
|------|---------|-------------|
| **xray** | Token-budgeted exploration | First choice for most tasks |
| **read** | Fetch content by URI | When you know what file you want |
| **query** | Raw SQL access | Complex analysis, aggregations, joins |

**Start with xray. Fall back to query only when needed.**

---

## xray Tool

Explore the codebase with controlled token spend. Four intents, progressive depth.

| Intent | Use When | Token Budget |
|--------|----------|--------------|
| `Explore` | Don't know what exists | 800-2000 |
| `Find` | Looking for specific code | 1000-2000 |
| `Examine` | Need structure details | 2000-5000 |
| `Understand` | Want synthesized explanation | 1000-3000 |

**Examples:**
```
xray intent=Explore scope="file:///src/**" tokenBudget=1500
xray intent=Find keywords="authentication" tokenBudget=1500
xray intent=Examine scope="file:///src/Auth.cs" tokenBudget=3000
xray intent=Understand keywords="How does JWT validation work?" tokenBudget=2000
```

**Key parameters:**
- `scope`: Glob pattern (`file:///src/**/*.cs`, `help:///**`)
- `keywords`: Search terms (questions work best for Understand)
- `boost`: Regex to elevate matches (`(?i)service|handler`)
- `penalize`: Regex to demote (`(?i)test|mock`)

See `help:///repoql/tools/xray/using-xray.md` for details.

---

## read Tool

Fetch content when you know the URI. Budget controls detail level.

```
read("file:///src/Auth.cs", 3000)                    -- Full content if fits
read("file:///src/Auth.cs#line=50,100", 2000)        -- Line range
read("file:///src/Auth.cs#symbol=ValidateToken", 1500) -- Symbol
read("file:///src/**/*.cs", 8000)                    -- Glob (budget distributed)
read("file:///src/** => tree", 2000)                 -- Directory tree
read("file:///src/Auth.cs // How does auth work?", 2000) -- LLM synthesis
```

**Progressive disclosure:**
- Budget < structure cost → headline only
- Budget < full cost → structure
- Budget >= full cost → complete content

See `help:///repoql/tools/read/read-command.md` for details.

---

## query Tool

SQL access to the full graph. Use for aggregations, joins, complex analysis.

### Convenience Views

Start with views, not raw tables:

```sql
-- File inventory
SELECT uri, lang, lines, error_count FROM Files WHERE lang = 'code.csharp';

-- Find functions
SELECT file_uri, name, signature FROM Functions WHERE name LIKE '%Validate%';

-- Find types
SELECT file_uri, name, type_kind FROM Types WHERE type_kind = 'class';

-- Find errors
SELECT target_uri, severity, message FROM Annotations WHERE severity = 'error';
```

| View | Purpose | Key Columns |
|------|---------|-------------|
| `Files` | Document inventory | uri, lang, lines, error_count, headline |
| `Types` | Classes, interfaces, structs | name, type_kind, namespace, signature |
| `Functions` | Methods, constructors | name, signature, declaring_type |
| `Annotations` | Lint errors, warnings | severity, message, target_uri |

See `help:///repoql/tools/query/views/files.md`, `types.md`, `functions.md`, `annotations.md`.

### Search

Hybrid semantic + lexical search:

```sql
SELECT uri, score FROM search('authentication', k := 20);
SELECT uri, score FROM search('config', scope := 'file:///src/%', k := 15);
SELECT uri, score FROM search('handler', negative_pattern := '(?i)test', k := 20);
```

Compose with `snippet()` for code context:
```sql
SELECT s.uri, sn.line_number, sn.text
FROM search('error handling', k := 5) s,
     LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

See `help:///repoql/tools/query/functions/search.md`.

---

## Core Data Model

Five tables underpin everything:

| Table | Purpose |
|-------|---------|
| `artifact` | File content + pre-computed `headline`, `summary`, `structure` |
| `node` | Entities: documents, functions, classes, headings |
| `edge` | Relationships: CALLS, IMPORTS, REFERS_TO, HAS_PART |
| `span` | Locations: line/byte ranges within documents |
| `annotation` | Facts: lint errors, metrics, analysis results |

**URIs address precisely:**
- `file:///src/auth.cs` - whole file
- `file:///src/auth.cs#line=42,50` - line range
- `file:///src/auth.cs#symbol=AuthService.Validate` - symbol
- `help:///quickstart.md` - embedded documentation

See `help:///repoql/tools/query/core-tables.md`.

---

## Format-Specific Features

Each file format has specialized macros and views:

### C# (`help:///repoql/tools/query/formats/csharp.md`)
```sql
SELECT * FROM csharp_types WHERE kind = 'class';
SELECT * FROM csharp_members WHERE kind = 'method';
```

### Markdown (`help:///repoql/tools/query/formats/markdown.md`)
```sql
SELECT document_uri, level, text FROM markdown_headings;
SELECT document_uri, href, link_text FROM markdown_links;
```

### Excel (`help:///repoql/tools/query/formats/xlsx.md`)
```sql
SELECT * FROM xlsx('file:///data/expenses.xlsx');
SELECT * FROM xlsx_sheets('file:///data/workbook.xlsx');
SELECT * FROM xlsx_union('**/expenses*.xlsx');
```

---

## Common Workflows

### Explore unfamiliar codebase
```
xray intent=Explore scope="file:///src/**" tokenBudget=2000
```

### Find where something is implemented
```
xray intent=Find keywords="authentication token" tokenBudget=1500
```

### Understand how something works
```
xray intent=Understand keywords="How does the caching layer work?" tokenBudget=2500
```

### Get codebase statistics
```sql
SELECT lang, COUNT(*) as files, SUM(lines) as total_lines
FROM Files GROUP BY lang ORDER BY total_lines DESC;
```

### Find all errors
```sql
SELECT target_uri, message FROM Annotations WHERE severity = 'error';
```

### Read specific file with context
```
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
```

---

## SQL Power Patterns

For complex analysis via the query tool:

| Pattern | Example |
|---------|---------|
| LATERAL composition | `FROM search(...) s, LATERAL snippet(s.uri, 2) sn` |
| Conditional count | `count(*) FILTER (WHERE severity = 'error')` |
| Regex extraction | `regexp_extract_all(text, 'TODO:\s*(.+)', 1)` |
| JSON access | `properties->>'$.name'` |
| Top-N per group | `QUALIFY row_number() OVER(...) <= n` |
| Exclude columns | `SELECT * EXCLUDE (large_column)` |

See `help:///repoql/tools/query/sql-reference.md` for full reference.

---

## Documentation Map

| Topic | URI |
|-------|-----|
| This quickstart | `help:///quickstart.md` |
| xray tool | `help:///repoql/tools/xray/using-xray.md` |
| read tool | `help:///repoql/tools/read/read-command.md` |
| search function | `help:///repoql/tools/query/functions/search.md` |
| Core tables | `help:///repoql/tools/query/core-tables.md` |
| Files view | `help:///repoql/tools/query/views/files.md` |
| Types view | `help:///repoql/tools/query/views/types.md` |
| Functions view | `help:///repoql/tools/query/views/functions.md` |
| Annotations view | `help:///repoql/tools/query/views/annotations.md` |
| SQL reference | `help:///repoql/tools/query/sql-reference.md` |
| C# format | `help:///repoql/tools/query/formats/csharp.md` |
| Markdown format | `help:///repoql/tools/query/formats/markdown.md` |
| Excel format | `help:///repoql/tools/query/formats/xlsx.md` |

**Explore docs:**
```
xray intent=Explore scope="help:///**" tokenBudget=2000
```
