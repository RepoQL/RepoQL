---
description: "PIVOT/UNPIVOT reshaping, regex extraction, inline CSV parsing"
tags: ["PivotWide", "UnpivotLong", "RegexpExtract", "ParseInline"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Data Reshaping Patterns

## Capsule: PivotWide

**Invariant**
Transform distinct row values into separate columns.

**Example**
```sql
PIVOT (SELECT project, lang, 1 as n FROM Files)
ON lang USING sum(n) GROUP BY project
```

**Depth**
- ON: Column whose values become new columns
- USING: Aggregation for each cell
- Quote generated column names containing special characters
- SeeAlso: UnpivotLong

---

## Capsule: UnpivotLong

**Invariant**
Transform multiple columns into key-value rows.

**Example**
```sql
UNPIVOT stats ON col1, col2, col3
INTO NAME metric VALUE amount
```

**Depth**
- ON: Columns to unpivot
- INTO NAME: New column for original column names
- INTO VALUE: New column for values
- SeeAlso: PivotWide

---

## Capsule: RegexpExtract

**Invariant**
Pull captured groups from strings using regex patterns.

**Example**
```sql
SELECT regexp_extract(uri, '/src/([^/]+)/', 1) as project
FROM Files
```

**Depth**
- Second arg: Regex with capture groups
- Third arg: Which group to extract (1-based)
- Returns NULL if no match
- regexp_extract_all returns list of all matches

---

## Capsule: ParseInline

**Invariant**
Read inline CSV text as a table with auto-detected columns.

**Example**
```sql
SELECT * FROM parse('name,value
Alice,100
Bob,200')
```

**Depth**
- Auto-detects column names from header
- Auto-detects column types
- No external file needed for small lookups
- SeeAlso: RegexpExtract

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
