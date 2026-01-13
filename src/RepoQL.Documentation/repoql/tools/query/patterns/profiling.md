---
description: "SUMMARIZE for quick statistics, EXPLAIN for query plans, profiling"
tags: ["Summarize", "ExplainPlan", "Profiling", "TableStats"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Query Profiling Patterns

## Capsule: Summarize

**Invariant**
Compute comprehensive column statistics in one pass.

**Example**
```sql
SUMMARIZE Files
SUMMARIZE (SELECT lang, lines FROM Files WHERE lang IS NOT NULL)
```

**Depth**
- Returns: min, max, avg, std, median, quartiles, null%
- approx_unique uses HyperLogLog (fast, approximate)
- Can use as subquery: SELECT * FROM (SUMMARIZE t)
- SeeAlso: Percentile

---

## Capsule: ExplainPlan

**Invariant**
Show query plan without or with actual execution metrics.

**Example**
```sql
EXPLAIN SELECT * FROM Files WHERE lang = 'code.csharp'
EXPLAIN ANALYZE SELECT * FROM Files JOIN Types ON ...
```

**Depth**
- EXPLAIN: Estimated cardinality only, no execution
- EXPLAIN ANALYZE: Runs query, shows actual metrics
- Compare estimated vs actual to spot statistics issues
- SeeAlso: Summarize

---

## Capsule: Profiling

**Invariant**
Enable detailed timing metrics for query optimization.

**Example**
```sql
SET profiling_mode = 'detailed';
SET profiling_output = '/tmp/profile.json';
```

**Depth**
- detailed mode adds optimizer and planner times
- JSON output can be visualized with graph tools
- Parallel operators show cumulative time
- SeeAlso: ExplainPlan

---

## Capsule: TableStats

**Invariant**
Update or query table statistics for better planning.

**Example**
```sql
ANALYZE Files;
SELECT * FROM duckdb_tables();
SELECT * FROM duckdb_columns() WHERE table_name = 'Files';
```

**Depth**
- ANALYZE: Recomputes statistics for join ordering
- duckdb_tables(): Table metadata
- duckdb_functions(): Available functions
- duckdb_extensions(): Loaded extensions

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
