---
description: Plan for Word format loader — table extraction, header detection, layout filtering, and table nodes
tags: [format, word, docx, plan, tables]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Word Loader — Tables

Implements: [Word Document Format Design](../designs/current/word-format.md) — Tables, Graph Materialization (table nodes)

## Scope

**Covers:**
- Table extraction from document body
- Header row detection (style-based and heuristic)
- Layout table filtering
- Merged cell tracking (horizontal and vertical)
- Column name extraction from header rows
- Cell text extraction (run concatenation per cell)
- `TableInfo` and `CellInfo` in `DocumentSurface`
- Table nodes with `HAS_PART` edges from document
- Table position markers in extracted body text (`[Table: Name (cols x rows)]`)
- Spans for tables mapping to line ranges
- Structure template updated to show tables positioned in heading flow
- Tests for table scenarios

**Does not cover:**
- Nested table parsing beyond first level (extract text, don't recurse structure)
- Table content queryable via UDF (extension point — not v1)

## Enables

Once this exists:
- **Agents can find tables in Word documents** — explore results show table names and dimensions in structure view
- **Agents can read specific tables** — `#symbol=StandardFeeMatrix` returns the table and surrounding context
- **Agents can query tables across documents** — graph queries find tables by column name or dimension
- **The spec's fee schedule, the API reference table, the test matrix** — the structured content that agents actually need from Word documents — are all discoverable

## Prerequisites

- Plan: word-01-skeleton-text-headings complete — loader, surface model, materialization pipeline, Liquid templates

## North Star

A table in a Word document should be as discoverable and readable as a table in a Markdown document. An agent scanning a spec's structure view should see each table with its name, dimensions, and position in the heading tree — enough to decide whether to read it.

## Done Criteria

### Table Extraction
- The loader shall extract all `<w:tbl>` elements from the document body
- For each table, the loader shall determine row count and column count (accounting for merged cells)
- The loader shall extract cell text by concatenating all runs within each cell's paragraphs
- When a cell contains nested tables, the loader shall extract the nested table's text content inline (not as a separate table node)

### Header Row Detection
- When a table row has `<w:tblHeader/>` in its row properties, the loader shall treat it as a header row
- When no style-based header is found, the loader shall apply a heuristic: first row is header if it contains text and has different formatting from subsequent rows
- Column names shall be the text content of header row cells
- When no header row is detected, column names shall be empty

### Layout Table Filtering
- The loader shall identify layout tables by heuristic: single-column tables with no header row styling and no visible borders
- Layout tables shall be excluded from the table inventory and from table nodes
- Layout tables shall not produce `[Table:]` markers in the extracted text

### Merged Cells
- The loader shall track horizontal merges via `<w:hMerge>` (or `<w:gridSpan>`) elements
- The loader shall track vertical merges via `<w:vMerge>` elements
- Merged cells shall be represented in the surface model with span information (rows spanned, columns spanned)
- Row count and column count shall reflect the logical grid, not the physical cell count

### Body Text Integration
- The loader shall insert `[Table: ColumnName1, ColumnName2, ... (C cols x R rows)]` markers at each table's position in the body text
- When a table has no header row, the marker shall use `[Table: (C cols x R rows)]`

### Materialization
- The materializer shall create one node per data table (layout tables excluded) with kind following codebase convention
- Table node props shall include: `row_count`, `col_count`, `column_names` (array), `has_header` (boolean)
- The materializer shall create `HAS_PART` edges from document to each table with ordinals preserving document order
- The materializer shall create spans for tables mapping to line ranges in extracted text

### Structure Template
- The structure template shall show tables positioned within the heading tree
- Table entries shall display: name or column names, dimensions
- Example: `Table: Standard Fee Matrix (6 cols, 24 rows)` indented under its containing heading

### Tests
- Test with a simple table (3x3, clear header row)
- Test with merged cells (horizontal, vertical, both)
- Test with a layout table (single column, no borders) — verify excluded
- Test with a table containing no header row
- Test with multiple tables under different headings — verify structure template positioning
- Test with a nested table — verify inner content extracted inline
- Test with a malformed table element — verify skip and continue

## Constraints

- **No UDF for table querying** — table content is in the graph and in body text; a `read_docx_table()` UDF is an extension point, not v1 scope
- **Single-level nesting only** — nested tables have their text extracted but are not materialized as separate table nodes; design chose simplicity
- **Layout filtering is heuristic** — some layout tables may slip through, some data tables may be filtered; the heuristic is conservative (prefer false positives over missed tables)

## References

- [Word Format Design](../designs/current/word-format.md) — Tables section
- XLSX loader table extraction (`src/Formats/RepoQL.Formats.Xlsx/XlsxLoader.cs`) — analogous pattern
- `DocumentFormat.OpenXml.Wordprocessing` — `Table`, `TableRow`, `TableCell`, `TableProperties`, `HorizontalMerge`, `VerticalMerge`, `TableHeader`

## Error Policy

Table extraction is independently try/caught. When a table fails to parse:
1. Log warning with table position and error details
2. Skip the table — do not create a node or marker for it
3. Continue processing remaining tables
4. Heading tree, other tables, and body text remain intact
