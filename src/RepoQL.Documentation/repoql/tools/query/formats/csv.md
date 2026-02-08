---
description: "csv(uri) → table data. csv_schema(uri) → detected column types. csv_files() → inventory. csv_preview(uri) → first N rows. csv_data(uri) → data with source tracking."
tags: ["csv", "tsv", "psv", "delimited", "tabular", "data-analysis"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# CSV Format

Query delimited data (CSV, TSV, PSV) with SQL macros. Automatic delimiter detection, header inference, column typing, per-column token estimates.

---

## Capsule: CsvBasic

**Invariant**
`csv(uri)` reads delimited data using DuckDB's native `read_csv_auto()`.

**Example**
```sql
SELECT * FROM csv('file:///data/sales.csv');
SELECT * FROM csv('file:///data/report.tsv', delimiter := '\t');
SELECT * FROM csv('file:///data/messy.csv', all_varchar := TRUE);
```
//BOUNDARY: Accepts RepoQL URIs. Auto-detects delimiter, header, types. Returns table.

**Depth**
- `uri`: RepoQL URI (e.g., `file:///path/to/file.csv`)
- `delimiter`: Override delimiter (default: `,`)
- `header`: Use first row as header (default: TRUE)
- `all_varchar`: Read all columns as VARCHAR (default: FALSE, use TRUE for messy data)
- Uses `strict_mode=false` internally — tolerates trailing blanks and minor CSV quirks

---

## Capsule: CsvSchema

**Invariant**
`csv_schema(uri)` shows column types detected during indexing — no file I/O.

**Example**
```sql
SELECT column_name, detected_type, estimated_tokens, min_value, max_value
FROM csv_schema('file:///data/sales.csv');
```
//BOUNDARY: Uses indexed metadata. Fast. Includes per-column token estimates.

**Depth**
- `column_index`: 0-based position
- `column_name`: Header name or synthetic (`column_1`, `column_2`)
- `detected_type`: integer, float, varchar, boolean, date, timestamp
- `estimated_tokens`: Projected cost to read this column
- `min_value`, `max_value`: Range for numeric columns
- `sample_values`: JSON array of up to 5 distinct examples
- Type inference uses 70% dominance threshold — a column with 80% integers and 20% strings is `integer`

---

## Capsule: CsvFiles

**Invariant**
`csv_files()` lists all indexed CSV/TSV/PSV files with summary metadata.

**Example**
```sql
SELECT uri, row_count, column_count, delimiter FROM csv_files();
SELECT uri, row_count FROM csv_files() WHERE row_count > 100;
SELECT uri, headline FROM csv_files('**/costs*');
```
//BOUNDARY: Returns indexed metadata. Use to discover files before querying them.

**Depth**
- `uri`: File URI
- `file_path`: Storage path
- `delimiter`: Detected delimiter character
- `row_count`, `column_count`: Dimensions
- `has_header`: Header row detected
- `media_type`: `text/csv;kind=csv.table`, `text/tab-separated-values;kind=tsv.table`, `text/plain;kind=data.psv`
- `headline`: X-ray summary line
- `byte_size`: File size

---

## Capsule: CsvPreview

**Invariant**
`csv_preview(uri, rows)` returns first N rows for quick inspection.

**Example**
```sql
SELECT * FROM csv_preview('file:///data/sales.csv', rows := 5);
```
//BOUNDARY: Adds `_source_file` column. Defaults to 10 rows.

---

## Capsule: CsvData

**Invariant**
`csv_data(uri)` reads full data with a `_source_file` column for provenance.

**Example**
```sql
SELECT customer_name, SUM(value) as total
FROM csv_data('file:///data/sales.csv')
GROUP BY customer_name
ORDER BY total DESC;
```
//BOUNDARY: Like `csv()` but adds `_source_file` column. Use when tracking which file data came from.

---

## Capsule: TokenBudgetAnalysis

**Invariant**
`csv_schema` reveals where tokens are spent — target expensive columns for selective reads.

**Example**
```sql
-- Which columns cost the most tokens?
SELECT column_name, detected_type, estimated_tokens,
       ROUND(estimated_tokens * 100.0 / SUM(estimated_tokens) OVER (), 1) as pct
FROM csv_schema('file:///data/sales.csv')
ORDER BY estimated_tokens DESC;
```
//BOUNDARY: Use to decide which columns to SELECT — don't `SELECT *` on expensive files.

**Depth**
- Varchar columns with long values dominate token cost
- Metadata columns (source_file, extraction_date) often waste budget
- A 22k-token file might need only 3k tokens if you select the right columns
- Combine with `csv_files()` to find the cheapest/most expensive datasets

---

## Capsule: CrossFileDiscovery

**Invariant**
Combine `csv_files()` and `csv_schema()` to search across all CSV files by column name or type.

**Example**
```sql
-- Find all price-related columns across every CSV file
SELECT f.uri, s.column_name, s.detected_type, s.min_value, s.max_value
FROM csv_files() f,
     LATERAL (SELECT * FROM csv_schema(f.uri)) s
WHERE s.column_name ILIKE '%price%' OR s.column_name ILIKE '%cost%'
ORDER BY f.uri, s.column_index;

-- What columns exist in each file?
SELECT f.uri, f.row_count,
       LIST(s.column_name ORDER BY s.column_index) as columns
FROM csv_files() f,
     LATERAL (SELECT * FROM csv_schema(f.uri)) s
GROUP BY f.uri, f.row_count
ORDER BY f.row_count DESC;
```
//BOUNDARY: LATERAL join runs csv_schema once per file. Efficient for small file counts.

---

## Data Analysis Patterns

### Aggregation

```sql
-- Revenue by product
SELECT product_code, COUNT(*) as customers,
       ROUND(SUM(value), 2) as revenue, ROUND(AVG(price_per_unit), 2) as avg_price
FROM csv('file:///data/sales.csv')
GROUP BY product_code
ORDER BY revenue DESC;
```

### Distribution

```sql
-- Revenue bands
SELECT
    CASE WHEN value < 25 THEN '< $25' WHEN value < 50 THEN '$25-50'
         WHEN value < 100 THEN '$50-100' WHEN value < 250 THEN '$100-250'
         ELSE '$250+' END as band,
    COUNT(*) as count, ROUND(SUM(value), 0) as total
FROM csv('file:///data/sales.csv')
GROUP BY 1 ORDER BY MIN(value);
```

### Statistical Summary

```sql
-- Quick column statistics
SUMMARIZE SELECT * FROM csv('file:///data/sales.csv');

-- Specific percentiles
SELECT
    ROUND(PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY value), 2) as median,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY value), 2) as p95,
    ROUND(AVG(value), 2) as mean, ROUND(STDDEV(value), 2) as stddev
FROM csv('file:///data/sales.csv');
```

### Window Functions

```sql
-- Top 3 customers per product
SELECT product_code, customer_name, value,
       RANK() OVER (PARTITION BY product_code ORDER BY value DESC) as rank
FROM csv('file:///data/sales.csv')
QUALIFY rank <= 3
ORDER BY product_code, rank;
```

### PIVOT

```sql
-- Products as columns, customers as rows
PIVOT (
    SELECT product_code, customer_name, value
    FROM csv('file:///data/sales.csv')
) ON product_code USING SUM(value)
ORDER BY 2 DESC NULLS LAST LIMIT 10;
```

### Filtering

```sql
-- Pattern matching on text columns
SELECT customer_name, product_code, value
FROM csv('file:///data/sales.csv')
WHERE customer_name ILIKE '%hospital%' OR customer_name ILIKE '%pharmacy%'
ORDER BY value DESC;
```

### Cross-File Joins

```sql
-- Join sales with material prices
SELECT o.item_name, o.price_offered, m.price as material_price
FROM csv('file:///data/price_offers.csv') o
JOIN csv('file:///data/material_prices.csv') m
  ON LOWER(o.item_name) LIKE '%' || LOWER(SPLIT_PART(m.material, ' ', 1)) || '%';
```

### Compact Summaries

```sql
-- Aggregate with filtered string lists
SELECT product_code, COUNT(*) as customers, ROUND(SUM(value), 0) as revenue,
       STRING_AGG(DISTINCT customer_name, ', ' ORDER BY customer_name)
           FILTER (WHERE value > 200) as top_customers
FROM csv('file:///data/sales.csv')
GROUP BY product_code ORDER BY revenue DESC;
```

---

## Common Patterns

| Goal | Query |
|------|-------|
| Read CSV file | `SELECT * FROM csv('file:///data.csv')` |
| List all CSV files | `SELECT * FROM csv_files()` |
| Preview rows | `SELECT * FROM csv_preview('file:///data.csv', 5)` |
| Check column types | `SELECT * FROM csv_schema('file:///data.csv')` |
| Token cost analysis | `SELECT column_name, estimated_tokens FROM csv_schema(...)` |
| Find columns by name | `csv_files() f, LATERAL csv_schema(f.uri) WHERE column_name ILIKE '%price%'` |
| Quick statistics | `SUMMARIZE SELECT * FROM csv('file:///data.csv')` |
| Top-N per group | Window function with `QUALIFY rank <= N` |
| Reshape wide | `PIVOT (...) ON column USING SUM(value)` |
| Multi-file data | Join separate `csv()` calls |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `csv('data.csv')` | Use full URI: `csv('file:///data.csv')` |
| `SELECT *` on large files | Check `csv_schema()` first, select needed columns |
| Expecting `csv_union()` | Not yet available — join individual `csv()` calls or use `read_csv_auto('path/**/*.csv', union_by_name := true)` |
| Wrong types in WHERE | Check `detected_type` in `csv_schema()` — filters on varchar columns need quotes |
| Joining without normalization | Use `LOWER()`, `TRIM()`, `ILIKE` — real CSV data is messy |

---

## Column Types

Types detected during indexing (70% dominance threshold):

| Type | Description | Example |
|------|-------------|---------|
| `integer` | Whole numbers | `42`, `-7`, `1000` |
| `float` | Decimal numbers | `3.14`, `0.067`, `-12.5` |
| `varchar` | Text strings | `"Alice"`, `"New York"` |
| `boolean` | True/false values | `true`, `false`, `yes`, `no`, `1`, `0` |
| `date` | Date values | `2024-01-01`, `01/15/2024` |
| `timestamp` | Date + time | `2024-01-01T09:30:00`, `2024-01-01 9:30 AM` |
| `unknown` | Empty column | All values null/blank |

---

## X-Ray Summaries

CSV files produce three pre-computed summaries (zero I/O cost):

- **Headline**: `filename | csv.table | size, tokens | N rows, M cols: col1, col2, ...`
- **Structure**: Per-column breakdown with type, token estimate, ranges, samples
- **Summary**: Compact overview of the dataset

```sql
SELECT headline, summary, structure FROM Files WHERE uri = 'file:///data/sales.csv';
```

---

## Graph Structure

CSV files create a document node with column child nodes:

```sql
-- List columns as graph nodes
SELECT col.properties->>'$.name' as name, col.properties->>'$.type' as type
FROM node n
JOIN edge e ON e.source_node_id = n.id AND e.type = 'HAS_PART'
JOIN node col ON e.destination_node_id = col.id AND col.kind = 'csv_column'
WHERE n.uri = 'file:///data/sales.csv'
ORDER BY e.ordinal;
```

Node kinds: `document` (the file), `csv_column` (each column). Edges: `HAS_PART` with ordinal.
