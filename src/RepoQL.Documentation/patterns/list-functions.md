---
description: "List transforms with lambdas, comprehension syntax, aggregation to arrays"
tags: ["ListTransform", "ListComprehension", "StringAgg", "Unnest"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# List Function Patterns

## Capsule: ListTransform

**Invariant**
Apply a function to each list element using lambda syntax.

**Example**
```sql
SELECT list_transform([1,2,3], x -> x * 2) -- [2,4,6]
SELECT list_filter([1,2,3,4], x -> x > 2)  -- [3,4]
```

**Depth**
- list_transform: Map function over elements
- list_filter: Keep elements matching predicate
- Second parameter (x, i) provides 1-based index
- SeeAlso: ListComprehension

---

## Capsule: ListComprehension

**Invariant**
Combine transform and filter in bracket notation.

**Example**
```sql
SELECT [x * 2 FOR x IN [1,2,3,4] IF x > 1] -- [4,6,8]
```

**Depth**
- Syntax: [expression FOR var IN list IF predicate]
- IF clause is optional
- Equivalent to list_transform(list_filter(...))
- SeeAlso: ListTransform

---

## Capsule: StringAgg

**Invariant**
Collect grouped values into a delimited string.

**Example**
```sql
SELECT project, string_agg(uri, '; ' ORDER BY lines DESC)
FROM Files GROUP BY project
```

**Depth**
- ORDER BY inside controls result order
- DISTINCT removes duplicates first
- Alternative: list_agg() returns actual list
- SeeAlso: QualifyTopN

---

## Capsule: Unnest

**Invariant**
Expand a list into multiple rows.

**Example**
```sql
SELECT unnest([1,2,3]) as n              -- 3 rows
SELECT (unnest([{a:1},{a:2}])).*         -- struct expansion
```

**Depth**
- Use in FROM or SELECT clause
- Struct unnest with .* expands to columns
- recursive := true flattens nested lists
- SeeAlso: StringAgg

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
