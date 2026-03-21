---
description: "xlsx(uri) → table data. xlsx_sheets(uri) → sheet metadata. xlsx_union(pattern) → combined multi-file data. xlsx_schema(uri) → detected column types."
tags: ["xlsx", "excel", "spreadsheet", "workbook", "tables", "financial"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# XLSX Format

Query Excel spreadsheet data with SQL macros. Automatic header detection, column type inference, multi-file synthesis.

---

## Capsule: XlsxBasic

**Invariant**
`xlsx(uri)` reads spreadsheet data using DuckDB's native Excel extension.

**Example**
```sql
SELECT * FROM xlsx('file:///data/expenses.xlsx');
SELECT * FROM xlsx('file:///data/report.xlsx', sheet := 'Summary');
SELECT * FROM xlsx('file:///data/messy.xlsx', all_varchar := TRUE);
```
//BOUNDARY: Accepts RepoQL URIs. Returns table with columns from spreadsheet.

**Depth**
- `uri`: RepoQL URI (e.g., `file:///path/to/file.xlsx`)
- `sheet`: Sheet name (default: first sheet)
- `header`: Use first row as header (default: TRUE)
- `all_varchar`: Read all columns as VARCHAR (default: FALSE, use TRUE for messy data)

---

## Capsule: XlsxSheets

**Invariant**
`xlsx_sheets(uri)` returns worksheet metadata from indexed file.

**Example**
```sql
SELECT * FROM xlsx_sheets('file:///data/workbook.xlsx');
-- Returns: sheet_name, sheet_index, row_count, column_count, has_header, has_totals, headline
```
//BOUNDARY: Uses indexed metadata, not live file read. Fast for large files.

**Depth**
- `sheet_name`: Worksheet name
- `sheet_index`: Order in workbook (0-based)
- `row_count`, `column_count`: Dimensions
- `has_header`: Header row detected
- `has_totals`: SUM/aggregate formulas present
- `headline`: X-ray summary line

---

## Capsule: XlsxSchema

**Invariant**
`xlsx_schema(uri)` shows detected column types from indexing.

**Example**
```sql
SELECT * FROM xlsx_schema('file:///data/expenses.xlsx');
-- Returns: sheet_name, column_letter, detected_type
```
//BOUNDARY: Types: Text, Numeric, Date, DateTime, Currency, Percentage, Formula, Boolean, Mixed.

**Depth**
- Column analysis performed during indexing
- `detected_type` based on cell value patterns
- Homogeneity score indicates type consistency
- Use before `xlsx_union` to verify schema compatibility

---

## Capsule: XlsxPreview

**Invariant**
`xlsx_preview(uri, rows)` returns first N rows for quick inspection.

**Example**
```sql
SELECT * FROM xlsx_preview('file:///data/large.xlsx', rows := 10);
SELECT * FROM xlsx_preview('file:///data/report.xlsx', rows := 5, sheet := 'Data');
```
//BOUNDARY: Adds `_source_file` and `_source_sheet` columns for tracking.

**Depth**
- `rows`: Number of rows to return (default: 10)
- `sheet`: Specific sheet (default: first sheet)
- Faster than full read for large files
- Header row auto-detected

---

## Capsule: XlsxUnion

**Invariant**
`xlsx_union(pattern)` combines data from multiple files matching glob pattern.

**Example**
```sql
-- All expense files
SELECT * FROM xlsx_union('**/expenses*.xlsx');

-- Specific sheet across files
SELECT * FROM xlsx_union('**/*.xlsx', sheet := 'Summary');

-- Aggregate across files
SELECT Category, SUM(Amount)
FROM xlsx_union('**/2024*.xlsx')
GROUP BY Category;
```
//BOUNDARY: Files should have compatible schemas. Columns matched by position.

**Depth**
- `pattern`: Glob pattern (e.g., `**/*expense*.xlsx`)
- `sheet`: Sheet name filter (NULL = first sheet from each)
- `header`: Use first row as header (default: TRUE)
- Adds `_source_file`, `_source_sheet` columns
- Critical for tax/financial synthesis across many spreadsheets

---

## Capsule: XlsxFiles

**Invariant**
`xlsx_files(pattern)` lists all indexed XLSX files with summary info.

**Example**
```sql
SELECT * FROM xlsx_files();
SELECT * FROM xlsx_files('**/2024*');
SELECT uri, sheet_count, total_rows FROM xlsx_files() WHERE has_formulas;
```
//BOUNDARY: Returns indexed metadata. Use before `xlsx_union` to identify targets.

**Depth**
- `uri`: File URI
- `sheet_count`: Number of worksheets
- `total_rows`: Sum of rows across all sheets
- `table_count`: Excel table objects
- `has_formulas`, `has_totals`: Formula presence
- `headline`: X-ray summary
- `byte_size`: File size

---

## Capsule: XlsxFinancial

**Invariant**
`xlsx_find_amounts(pattern)` discovers columns that look like financial data.

**Example**
```sql
-- Find all numeric/currency columns
SELECT * FROM xlsx_find_amounts();

-- Search specific files
SELECT * FROM xlsx_find_amounts('**/2024*.xlsx');

-- Custom column name pattern
SELECT * FROM xlsx_find_amounts(column_hint := '(?i)revenue|income');
```
//BOUNDARY: Filters to Numeric and Currency detected types.

**Depth**
- `pattern`: Glob filter (default: all XLSX files)
- `column_hint`: Regex for column names (default: `amount|total|sum|price|cost|value|revenue|expense`)
- Returns: `file_uri`, `sheet_name`, `column_letter`, `detected_type`, `row_count`
- Useful for discovering financial data in messy spreadsheets

---

## Capsule: XlsxSummary

**Invariant**
`xlsx_summary(pattern)` provides overview of matched files with aggregated metrics.

**Example**
```sql
SELECT * FROM xlsx_summary('**/reports/*.xlsx');
-- Returns: file_uri, sheets, total_rows, tables, has_formulas, has_totals, headline, sheet_names
```
//BOUNDARY: Quick inventory before detailed analysis.

**Depth**
- `sheet_names`: Comma-separated list of all sheets
- Aggregate view of file characteristics
- Use to scope `xlsx_union` queries

---

## Capsule: XlsxXray

**Invariant**
X-ray summaries reveal workbook structure without reading cell data.

**Example**
```sql
SELECT headline, summary, structure
FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///data/report.xlsx';
```
//BOUNDARY: Pre-computed during indexing. Zero I/O cost.

**Depth**
- **Headline**: `filename | xlsx.workbook | size | N sheets: names | rows | formulas | totals | tables | charts`
- **Structure**: Full breakdown of worksheets, columns (letter, header, type), tables, charts, named ranges
- **Summary**: Concise overview with tables, formulas, type distribution

---

## Capsule: XlsxGraph

**Invariant**
XLSX files create hierarchical nodes: document → worksheets → tables/charts.

**Example**
```sql
-- List all worksheets
SELECT ws.properties
FROM node n
JOIN edge e ON e.source_node_id = n.id
JOIN node ws ON e.destination_node_id = ws.id
WHERE n.uri = 'file:///data/report.xlsx'
  AND ws.kind = 'xlsx_worksheet'
ORDER BY e.ordinal;
```
//BOUNDARY: Node kinds: `xlsx_worksheet`, `xlsx_table`, `xlsx_chart`, `xlsx_named_range`, `xlsx_pivot_table`.

**Depth**
- Edges use `HAS_PART` type with `ordinal` for ordering
- Properties stored as JSON: name, row/column counts, detected types
- Tables include header/totals row info
- Charts include type, series count, data range

---

## Common Patterns

| Goal | Query |
|------|-------|
| Read spreadsheet | `SELECT * FROM xlsx('file:///data.xlsx')` |
| List sheets | `SELECT * FROM xlsx_sheets('file:///data.xlsx')` |
| Preview data | `SELECT * FROM xlsx_preview('file:///data.xlsx', 10)` |
| Check column types | `SELECT * FROM xlsx_schema('file:///data.xlsx')` |
| Find all XLSX files | `SELECT * FROM xlsx_files()` |
| Combine expense files | `SELECT * FROM xlsx_union('**/expense*.xlsx')` |
| Sum across files | `SELECT SUM(Amount) FROM xlsx_union('**/2024*.xlsx')` |
| Find financial columns | `SELECT * FROM xlsx_find_amounts()` |
| Files with formulas | `SELECT * FROM xlsx_files() WHERE has_formulas` |
| X-ray structure | `SELECT structure FROM artifact WHERE ... LIKE '%xlsx%'` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `xlsx('data.xlsx')` | Use full URI: `xlsx('file:///data.xlsx')` |
| `xlsx_union('*.xlsx')` | Use glob: `xlsx_union('**/*.xlsx')` |
| Mixed column types | Use `all_varchar := TRUE` for messy data |
| Wrong sheet name | Check with `xlsx_sheets(uri)` first |
| Schema mismatch in union | Verify with `xlsx_schema(uri)` before combining |
| Slow on large files | Use `xlsx_preview` or `xlsx_sheets` for metadata |

---

## Column Types

RepoQL detects these column types during indexing:

| Type | Description |
|------|-------------|
| `Text` | String values |
| `Numeric` | Numbers (integers, decimals) |
| `Date` | Date values |
| `DateTime` | Date + time values |
| `Currency` | Monetary values (detected by format) |
| `Percentage` | Percentage values |
| `Formula` | Cells containing formulas |
| `Boolean` | TRUE/FALSE values |
| `Mixed` | Multiple types in column |
