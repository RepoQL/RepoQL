---
description: "Conditional aggregates, hierarchical totals, distribution statistics"
tags: ["FilterClause", "GroupingSets", "ArgMax", "Percentile", "Rollup"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Aggregation Patterns

## Capsule: FilterClause

**Invariant**
Compute multiple conditional aggregates in one pass using FILTER.

**Example**
```sql
SELECT lang, COUNT(*) FILTER (WHERE error_count > 0) as errors
FROM Files GROUP BY lang
```

**Depth**
- Distinction: Replaces verbose CASE WHEN inside aggregate
- Trade-off: Same performance, much cleaner syntax
- SeeAlso: GroupingSets

---

## Capsule: GroupingSets

**Invariant**
Compute aggregates at multiple grouping levels in one query.

**Example**
```sql
SELECT COALESCE(project, 'TOTAL'), COUNT(*)
FROM Files GROUP BY ROLLUP (project, lang)
```

**Depth**
- ROLLUP: Hierarchical drill-down (N+1 levels)
- CUBE: All combinations (2^N levels)
- GROUPING SETS: Explicit control over combinations
- SeeAlso: FilterClause

---

## Capsule: ArgMax

**Invariant**
Get the value from the row with the maximum of another column.

**Example**
```sql
SELECT lang, arg_max(uri, lines) as largest_file
FROM Files GROUP BY lang
```

**Depth**
- Distinction: No subquery needed for "which X has highest Y"
- Trade-off: Only returns one column; use window for full row
- SeeAlso: QualifyTopN

---

## Capsule: Percentile

**Invariant**
Compute distribution statistics like median and quartiles.

**Example**
```sql
SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY lines) as median
FROM Files
```

**Depth**
- 0.5 = median, 0.25/0.75 = quartiles, 0.9 = P90
- percentile_cont interpolates; percentile_disc returns actual value
- SeeAlso: Summarize

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
