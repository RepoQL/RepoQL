---
description: "Top-N per group, running totals, row comparison, column exclusion"
tags: ["QualifyTopN", "CumulativeCalc", "LagLead", "ExcludeReplace"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Window Function Patterns

## Capsule: QualifyTopN

**Invariant**
Filter rows by window function results without nesting queries.

**Example**
```sql
SELECT uri, lang, lines FROM Files
QUALIFY row_number() OVER (PARTITION BY lang ORDER BY lines DESC) <= 3
```

**Depth**
- Distinction: QUALIFY is to windows what HAVING is to aggregates
- Works with rank(), dense_rank(), ntile()
- SeeAlso: ArgMax

---

## Capsule: CumulativeCalc

**Invariant**
Compute running totals with ORDER BY inside the window frame.

**Example**
```sql
SELECT lang, SUM(lines) OVER (ORDER BY lines DESC) as cumulative
FROM Files
```

**Depth**
- OVER () = entire result set
- OVER (ORDER BY x) = running total up to current row
- OVER (PARTITION BY a ORDER BY b) = running within partition
- SeeAlso: QualifyTopN

---

## Capsule: LagLead

**Invariant**
Access previous or next row values for comparison.

**Example**
```sql
SELECT uri, lines, LAG(lines) OVER (ORDER BY mtime) as prev
FROM Files
```

**Depth**
- LAG(col, n, default): n rows back with optional default
- LEAD(col, n, default): n rows forward
- NotThis: Returns NULL at boundaries unless default specified
- SeeAlso: AsofJoin

---

## Capsule: ExcludeReplace

**Invariant**
Remove or transform columns from SELECT * without listing all.

**Example**
```sql
SELECT * EXCLUDE (summary, structure) FROM Files
SELECT * REPLACE (upper(lang) as lang) FROM Files
```

**Depth**
- EXCLUDE: Remove columns by name
- REPLACE: Transform columns in place
- Can combine both in one statement

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
