---
name: duckdbMacroPerf
description: Optimize DuckDB TABLE macros and SQL queries in RepoQL. Use when writing, reviewing, or debugging SQL macros — especially those involving UDFs, embedding calls, or scope filters. Prevents 5-18x performance regressions from CTE re-evaluation traps.
zones: { K: 45, P: 10, C: 40, W: 5 }
---

# DuckDB Macro Performance

DuckDB TABLE macros have hidden re-evaluation behaviors that cause catastrophic performance regressions. These cannot be derived from first principles — the optimizer makes decisions that contradict standard SQL expectations.

## The Mental Model

DuckDB TABLE macros expand textually. Every CTE reference is a potential re-evaluation. The optimizer will inline CTEs when it believes this is cheaper, but its cost model doesn't account for:
- UDF side effects (API calls, file I/O)
- The actual cost of C# scalar UDFs
- Cross-macro expansion depth

**The result:** A query that takes 800ms as inline CTEs can take 18s as a TABLE macro, with identical logic. The difference is invisible without measurement.

## The Three Traps

These are the specific mechanisms that cause re-evaluation. Each was discovered empirically in RepoQL and verified with controlled experiments.

### Capsule: CastAtUseSite

**Invariant**
A type cast on a CTE column reference (`qv.vec::FLOAT[]`) at the use site causes DuckDB to re-evaluate the entire CTE for each cast expression.

**Example**
```sql
-- SLOW (4.4s): cast at use site — CTE re-evaluated per reference
query_vec AS (SELECT embed_query(q) AS vec ...),
scored AS (
    SELECT safe_cosine(qv.vec::FLOAT[], de.embedding) AS score, -- re-eval 1
           array_length(qv.vec::FLOAT[]) AS dim                 -- re-eval 2
    FROM query_vec qv ...
    WHERE de.dim = array_length(qv.vec::FLOAT[])                -- re-eval 3
)

-- FAST (0.8s): cast in CTE definition — evaluated once
query_vec AS (SELECT embed_query(q)::FLOAT[] AS vec ...),
scored AS (
    SELECT safe_cosine(qv.vec, de.embedding) AS score,
           array_length(qv.vec) AS dim
    FROM query_vec qv ...
    WHERE de.dim = array_length(qv.vec)
)
```
//BOUNDARY: This applies specifically to TABLE macros. Regular CTEs may behave differently. Always measure.

**Depth**
- Measured on RepoQL's 55K embedding vectors with Voyage API embed_query UDF
- The cast expression `col::TYPE` creates a new expression node the optimizer treats as requiring fresh evaluation
- Multiple casts on the same column multiply the re-evaluation count
- The fix: cast once in the CTE definition, reference the pre-cast column everywhere else
- SeeAlso: `references/macro-rules.md`

### Capsule: RawParamInQualify

**Invariant**
A raw macro parameter in QUALIFY or LIMIT triggers full pipeline re-evaluation for each candidate row, because the optimizer cannot fold it to a constant.

**Example**
```sql
-- CATASTROPHIC (18s): raw macro parameter
CREATE OR REPLACE MACRO search(q, max_cand := 5000) AS TABLE (
WITH ...
ranked AS (
    SELECT *, ROW_NUMBER() OVER (...) AS rk FROM combined c
    QUALIFY rk <= CAST(COALESCE(max_cand, 5000) AS BIGINT)  -- re-evals everything
)
...);

-- FAST (1s): resolve through CTE first
CREATE OR REPLACE MACRO search(q, max_cand := 5000) AS TABLE (
WITH params AS (SELECT CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand),
...
ranked AS (
    SELECT *, ROW_NUMBER() OVER (...) AS rk FROM combined c
    QUALIFY rk <= (SELECT limit_cand FROM params)  -- constant subquery
)
...);
```
//BOUNDARY: This is specific to TABLE macros. In regular SQL, DuckDB folds constants normally.

**Depth**
- TABLE macros substitute parameters textually: `max_cand` becomes the literal expression `5000`
- But wrapped in `CAST(COALESCE(...))`, the optimizer apparently cannot prove it's constant
- A CTE subquery `(SELECT x FROM single_row_cte)` is recognized as a scalar constant
- Impact scales with pipeline depth — the entire CTE chain above the QUALIFY re-runs
- All existing RepoQL macros (`_search_lexical`, `_search_candidates`) already follow the CTE pattern correctly; this trap catches new macros
- SeeAlso: `references/macro-rules.md`

### Capsule: MultiRefCTE

**Invariant**
A CTE referenced by multiple downstream CTEs in a TABLE macro will be evaluated once per reference. DuckDB does not materialize CTEs in TABLE macros by default.

**Example**
```sql
-- Two scans of _scope_filter (286K rows each):
_sem_scope AS (SELECT * FROM _scope_filter()),
structure_sem AS (... JOIN _sem_scope sf ...),   -- eval 1
full_text_chunks AS (... JOIN _sem_scope sf ...), -- eval 2

-- One scan: single-pass with GROUP BY
all_scored AS (
    ... JOIN _scope_filter() sf ...   -- eval 1 (only)
    -- both embedding types in one scan
),
per_node AS (
    SELECT node_id,
        MAX(CASE WHEN embedding_type = 'structure' THEN score END) AS struct,
        MAX(CASE WHEN embedding_type = 'full' THEN score END) AS full
    FROM all_scored GROUP BY node_id
)
```
//BOUNDARY: `CTE MATERIALIZED` forces single evaluation in regular SQL but is not available inside TABLE macros.

**Depth**
- DuckDB's optimizer inlines CTEs when estimated cost is low — but UDF costs are invisible to the estimator
- The fix: restructure so each CTE is referenced exactly once
- Common pattern: replace FULL OUTER JOIN of two filtered scans with single scan + GROUP BY + conditional aggregation
- Carry computed values (like `query_dim`) through the pipeline as columns instead of referencing the source CTE again
- SeeAlso: `references/macro-rules.md`, `references/diagnosis.md`

---

## Quick Reference

| Trap | Symptom | Fix | Impact |
|------|---------|-----|--------|
| CastAtUseSite | 3-5x slower than expected | Cast in CTE definition | 4.4s → 0.8s |
| RawParamInQualify | 10-20x slower than expected | Resolve params in CTE | 18s → 1s |
| MultiRefCTE | Linear scaling with reference count | Single-pass + GROUP BY | 2-3x |

---

## When This Skill Applies

- Writing or modifying any file in `Schema/Macros/*.sql`
- Adding UDF calls to SQL macros
- Debugging unexpectedly slow queries
- Reviewing macro PRs

## References

- `references/macro-rules.md` — Hard rules for writing macros
- `references/diagnosis.md` — Diagnostic workflow for performance issues
- `references/patterns.md` — Efficient patterns for common operations

---

*Measure, don't guess. The optimizer's cost model is blind to UDFs.*
