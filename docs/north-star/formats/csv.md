# CSV Format: What Great Looks Like

> An agent should know what data a CSV file contains, what its columns mean, and how it relates to surrounding code — without reading it.

An agent exploring a repository encounters 60 CSV files scattered across test fixtures, seed data, configuration, and exports. It scans 60 headlines and knows what each one holds: a 12-column user table with 8,400 rows, a 3-column country reference with 195 entries, a tab-separated log with timestamps and severity levels, a semicolon-delimited European export with currency amounts. It narrows to 8 files related to billing, reads their structures — column names, inferred types, value ranges, sample data — and understands the data model. It queries the graph: "which code models map to this CSV's columns?" and finds the ORM entity, the import script, the test fixture that seeds it. Every file used a different delimiter, different quoting, different encoding. The agent noticed none of this. The format handler absorbed the chaos and spoke one language.

---

## Discovery

- An agent should be able to understand what data a CSV file contains from a single-line headline
- An agent should be able to distinguish CSV files by their domain (user data, config, test fixture, log, export) from structure alone
- An agent should be able to see column names, row count, and dominant data types without opening the file
- An agent should be able to scan 200 data files and filter to the 5 relevant ones without reading any
- An agent should be able to tell the difference between a 50-row reference table and a 500,000-row data dump from the headline

```
headline  →  "users.csv | csv.table | 1.2 MB, ~28k tok | 12 cols: id, email, name, role, ... | 8,400 rows"
headline  →  "metrics.tsv | tsv.table | 340 KB, ~7.2k tok | 5 cols: timestamp, service, latency_ms, status, trace_id | 12,300 rows"
headline  →  "countries.csv | csv.table | 4.8 KB, ~0.2k tok | 3 cols: code, name, region | 195 rows"
```

---

## Schema Intelligence

- An agent should be able to see every column's name and inferred type without reading the file
- An agent should be able to see sample values for each column — enough to understand what the data looks like, not enough to substitute for reading it
- An agent should be able to see value ranges for numeric and date columns (min/max)
- An agent should be able to see the approximate token cost per column — so it can budget reads by selecting only the columns it needs
- An agent should be able to see which columns are likely identifiers, which are categorical, which are free text
- An agent should be able to trust that type inference reflects the actual data, not just the first row

Token cost per column is the difference between "read the whole file" and "read just what matters." A 12-column CSV where 3 columns hold short codes and 2 hold free-text descriptions has wildly uneven cost. An agent that knows `description` costs ~18k tokens and `status` costs ~0.3k can make a precise budget decision.

```
structure →
  users.csv (8,400 rows, ~28k tok)
    Columns:
      id (integer, ~1.2k tok) → 1 - 8400
      email (varchar, ~4.8k tok) → "alice@example.com", "bob@corp.io"
      name (varchar, ~3.1k tok) → "Alice Chen", "Bob Smith"
      role (varchar, ~0.9k tok) → "admin", "user", "viewer"
      created_at (timestamp, ~3.4k tok) → 2022-01-15 to 2025-12-01
      is_active (boolean, ~0.3k tok) → true, false
      login_count (integer, ~1.1k tok) → 0 - 1247
      ...
```

---

## Delimiter Transparency

- An agent should never need to know or specify a delimiter — the format handler detects it
- An agent should be able to query CSV, TSV, pipe-delimited, and semicolon-delimited files through the same surface
- An agent should be able to trust that delimiter detection handles the file correctly, including edge cases like commas inside quoted fields
- An agent should be able to see which delimiter was detected, but only when it asks — the default is invisible handling

CSV is a family of formats pretending to be one. Tab-separated, semicolon-separated (common in European locales where commas are decimal separators), pipe-delimited — they all share the same structure. DuckDB's sniffer handles detection; the format handler's job is to make the distinction invisible.

---

## Data Profile

- An agent should be able to see the statistical shape of a CSV without reading it: row count, column count, null density, unique counts for categorical columns
- An agent should be able to identify columns that look like primary keys (unique, sequential)
- An agent should be able to identify columns that look like foreign keys (naming patterns, value overlap with other CSVs)
- An agent should be able to distinguish between a hand-edited reference table and a machine-generated export from structural cues (row count, regularity, column naming)

---

## Content

- An agent should be able to read any contiguous slice of rows without paying for the whole file
- An agent should be able to read specific columns without reading the full width
- An agent should be able to see the first N and last N rows as a preview
- An agent should be able to get a file's content as a readable text table, not raw comma-separated bytes
- An agent should be able to trust that content rendering handles quoting, escaping, and multi-line values correctly

```
read("file:///data/users.csv#line=1,10", 2000)   →  first 10 rows, formatted as table
read("file:///data/users.csv", 500)               →  headline + structure (budget too small for content)
```

---

## Query Integration

- An agent should be able to query CSV files directly through SQL without loading them into a separate table
- An agent should be able to join CSV data with the knowledge graph — combine file metadata with file content in one query
- An agent should be able to aggregate across CSV files (total rows, shared columns, schema comparison)
- An agent should be able to trust that DuckDB's `read_csv_auto()` powers the heavy lifting — the format handler indexes structure, not a parallel parser

```sql
-- What CSV files have a column named 'user_id'?
SELECT n.uri, n.headline
FROM Nodes n
WHERE n.kind = 'csv_column' AND n.props->>'name' = 'user_id'

-- Compare schemas across related CSVs
SELECT parent.uri, col.props->>'name' AS column, col.props->>'type' AS type
FROM Nodes col
JOIN Edges e ON e.target_id = col.id AND e.kind = 'HAS_PART'
JOIN Nodes parent ON parent.id = e.source_id
WHERE parent.kind = 'document' AND parent.uri LIKE '%user%'
ORDER BY parent.uri, e.ordinal
```

---

## Relationships

- An agent should be able to find code that reads, writes, or references a CSV file
- An agent should be able to find ORM models or data classes whose fields match a CSV's columns
- An agent should be able to find SQL table definitions that correspond to a CSV's schema
- An agent should be able to find other CSV files with overlapping columns (shared schema fragments)
- An agent should be able to discover test fixtures and the tests that use them

CSV files don't exist in isolation. A `users.csv` in `test/fixtures/` almost certainly maps to a `User` class, a `users` database table, a test that loads it. The format handler exposes column structure; the graph connects it to everything else.

---

## Integrity

- An agent should be able to find CSV files with inconsistent column counts (rows that don't match the header)
- An agent should be able to find CSV files with encoding issues (BOM markers, mixed encodings)
- An agent should be able to find CSV files with type inconsistencies (a "numeric" column with text values)
- An agent should be able to trust that a malformed CSV still gets partial indexing — headers and what's parseable, with diagnostics on what failed
- An agent should be able to see empty files, header-only files, and single-row files distinguished from each other

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Understand any CSV from its headline | 60 data files become navigable in one scan |
| See complete column schema with types | Know the data model without reading a byte |
| Detect delimiters invisibly | CSV/TSV/pipe/semicolon handled uniformly |
| Profile data shape without content | Distinguish a 50-row reference from a 500k-row dump |
| Query CSV files directly via SQL | DuckDB's `read_csv_auto()` makes data immediately accessible |
| Connect CSV columns to code models | Data files aren't isolated — they're part of the system |
| Surface integrity issues as diagnostics | Malformed rows found before they break import scripts |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Read a CSV to learn its columns | An agent should see column names and types from the headline |
| Require delimiter specification | An agent should never need to know the delimiter |
| Reimplement CSV parsing | DuckDB's sniffer handles detection — index structure, not bytes |
| Treat CSV as flat text | An agent should see columns, types, and relationships |
| Ignore malformed rows | An agent should see diagnostics on integrity issues |
| Index every cell into the graph | An agent should query content via `read_csv_auto()`, not graph nodes |
| Conflate "no data" with "parse failure" | An agent should distinguish empty, header-only, and failed files |

---

*An agent should be able to understand a repository's data files as typed, structured, connected datasets — navigable from headline to schema to content — without guessing what any file contains.*
