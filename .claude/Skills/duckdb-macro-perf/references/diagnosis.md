---
description: Diagnostic workflow for identifying DuckDB TABLE macro performance issues. Step-by-step process for isolating re-evaluation traps.
zones: { K: 30, P: 50, C: 10, W: 10 }
---

# Diagnosing Macro Performance

When a macro is slower than expected, follow this workflow to identify the cause.

## Step 1: Establish Baseline

Measure the macro and its manual CTE equivalent:

```bash
# Macro timing (includes gRPC overhead ~640ms)
time repoql.exe query "SELECT COUNT(*) FROM your_macro('test args')"

# Baseline: SELECT 1 to measure gRPC overhead
time repoql.exe query "SELECT 1"
```

**Subtract gRPC overhead from all timings.** If `SELECT 1` takes 640ms and your macro takes 4400ms, the actual query time is ~3760ms.

## Step 2: Reproduce as Inline CTEs

Write the same logic as manual CTEs (not inside a TABLE macro):

```sql
WITH qv AS (SELECT embed_query('test')::FLOAT[] AS vec),
scored AS (SELECT ... FROM qv, table WHERE ...)
SELECT COUNT(*) FROM scored
```

If manual CTEs are significantly faster (>2x), the macro has a re-evaluation trap.

## Step 3: Progressive Build-Up

Start with the simplest version and add complexity one CTE at a time:

```bash
# Phase 1: Just the expensive CTE
time repoql.exe query "SELECT COUNT(*) FROM (WITH qv AS (...) SELECT ...)"

# Phase 2: Add the join
time repoql.exe query "SELECT COUNT(*) FROM (WITH qv AS (...), scored AS (... JOIN ...) SELECT ...)"

# Phase 3: Add ranking
time repoql.exe query "SELECT COUNT(*) FROM (WITH ..., ranked AS (... QUALIFY ...) SELECT ...)"
```

**The step where timing jumps is where the trap lives.**

## Step 4: Check for Known Traps

### Cast at use site
Look for `cte_alias.column::TYPE` in any SELECT, WHERE, or JOIN:
```bash
grep -n '::FLOAT\[\]' Schema/Macros/your_macro.sql
```
Every `::TYPE` cast on a CTE column is a potential re-evaluation.

### Raw macro parameter
Look for macro parameters used directly in QUALIFY, LIMIT, WHERE, CASE:
```bash
grep -n 'QUALIFY.*<= [a-z_]*\b' Schema/Macros/your_macro.sql
grep -n 'LIMIT.*CAST(COALESCE(' Schema/Macros/your_macro.sql
```

### Multi-reference CTE
Count how many times each CTE is referenced:
```bash
# For each CTE name, count downstream references
grep -c 'query_vec\|_sem_scope\|limited' Schema/Macros/your_macro.sql
```
Any CTE referenced >1 times is a candidate for re-evaluation.

## Step 5: Verify with Temp Macro

Create the fix as a temp macro and test in one call:

```sql
CREATE OR REPLACE TEMP MACRO _test_fix(q) AS TABLE (
    WITH ... -- your fixed logic
);
SELECT COUNT(*) FROM _test_fix('test args');
```

**Both statements must be in the same query** — the host runs each query call in a separate read-only transaction, so temp macros don't persist between calls.

## Step 6: Deploy and Measure

```bash
# After editing the .sql file:
powershell -File deploy.ps1

# Reset host version to force macro reload
echo "" > .repoql/host.version

# Wait for host restart, then test
sleep 8
time repoql.exe query "SELECT COUNT(*) FROM your_macro('test args')"
```

## Using EXPLAIN ANALYZE

EXPLAIN ANALYZE output is designed for human interpretation. When debugging with the user, show them the output — it reveals operator timings, cardinality estimates vs actuals, and where the pipeline spends time.

```bash
# Through the host CLI:
repoql.exe query "EXPLAIN ANALYZE SELECT ... FROM your_macro('test')"
```

The output comes back as JSON wrapped in the query result format. Key fields to look for:
- `operator_timing` — time spent in each operator
- `operator_cardinality` vs `cumulative_rows_scanned` — cardinality estimation accuracy
- `cpu_time` — cumulative CPU time at each node
- `operator_name` — look for multiple `CTE_SCAN` on the same CTE index (indicates re-evaluation)

For complex macro pipelines, the EXPLAIN tree can be very deep. Focus on operators with the highest `operator_timing` values — those are your bottlenecks.

## Common Pitfalls in Diagnosis

- **Cold vs warm:** First query after host restart includes embed_query API warmup (~900ms). Always measure second run.
- **Stale macros:** After deploy, if timings don't change, the host may not have reloaded. Check `host.version` was reset.
- **DuckDB CLI comparison:** DuckDB CLI runs without UDF overhead and uses native SIMD. Don't compare CLI timings directly with host timings — the gap is expected.
