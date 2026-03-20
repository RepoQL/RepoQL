---
description: Non-negotiable rules for writing DuckDB TABLE macros in RepoQL. Deviation causes 5-18x performance regressions.
zones: { K: 20, C: 70, P: 5, W: 5 }
---

# Macro Rules

These rules apply to all `CREATE OR REPLACE MACRO ... AS TABLE (...)` definitions in `Schema/Macros/*.sql`.

## Rules

### Parameter Resolution

- Never use a raw macro parameter in QUALIFY, LIMIT, WHERE, or CASE expressions
- Always resolve macro parameters into a CTE first, then reference via `(SELECT col FROM cte)`
- The CTE must produce exactly one row (scalar subquery)

```sql
-- First CTE in every macro:
params AS (
    SELECT
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand,
        NULLIF(TRIM(CAST(uri_glob AS VARCHAR)), '') AS scope_glob
)
```

### Type Casting

- Never cast a CTE column at the use site: `qv.vec::FLOAT[]` re-evaluates the CTE
- Always cast in the CTE definition: `SELECT expr::FLOAT[] AS vec`
- If the same column needs different types, cast each variant in the source CTE

```sql
-- In the CTE:
query_vec AS (SELECT embed_query(q)::FLOAT[] AS vec ...)

-- All downstream references:
safe_cosine(qv.vec, de.embedding)   -- not qv.vec::FLOAT[]
array_length(qv.vec)                -- not array_length(qv.vec::FLOAT[])
```

### CTE Reference Count

- Each CTE should be referenced by exactly one downstream CTE
- If a CTE must be referenced multiple times, restructure to single-pass
- Prefer GROUP BY with conditional aggregation over FULL OUTER JOIN of filtered branches
- Carry computed values as columns through the pipeline (e.g., `query_dim`)

### UDF-Containing CTEs

- CTEs that call UDFs (especially network UDFs like `embed_query`) are the highest-priority targets for single-reference
- Never reference a UDF-containing CTE from a QUALIFY, LIMIT, or calibration expression
- Instead, carry the UDF result as a column through downstream CTEs

### Scope Filtering

- `_scope_filter()` returns 286K rows for the unfiltered case — it's not free
- When nesting inside a TABLE macro, it may be evaluated once per downstream reference
- The unfiltered fast path (no args) skips `glob_files` + URI join

## Scope

**Applies to**: all `Schema/Macros/*.sql` files

**Does not apply to**: regular SQL queries executed via `query` tool, test fixtures, one-off diagnostics

## Verification

After writing or modifying a macro:

```bash
# Time the macro with a representative query
time repoql.exe query "SELECT COUNT(*) FROM your_macro('test args')"

# Compare with manual CTE version of the same logic
# If the macro is >2x slower than manual CTEs, check for traps
```

## Exceptions

None. These rules exist because the performance impact is catastrophic and invisible without measurement. A macro that violates these rules may appear to work correctly in tests (small data) and fail at scale (production data).
