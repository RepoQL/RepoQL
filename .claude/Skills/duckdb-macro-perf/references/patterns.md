---
description: Efficient SQL patterns for common DuckDB TABLE macro operations in RepoQL. Tested alternatives to patterns that trigger re-evaluation traps.
zones: { K: 60, C: 25, P: 5, W: 10 }
---

# Efficient Macro Patterns

Tested patterns that avoid the re-evaluation traps documented in SKILL.md.

## Single-Pass Embedding Scan

When you need both structure and full-text embedding scores per document, scan once and split with GROUP BY:

```sql
-- Instead of two separate scans + FULL OUTER JOIN:
all_scored AS (
    SELECT
        de.doc_id, de.node_id, de.embedding_type,
        de.chunk_index, de.start_byte, de.end_byte,
        safe_cosine(qv.vec, de.embedding) AS score,
        array_length(qv.vec) AS query_dim   -- carry dim through pipeline
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN _scope_filter(...) sf ON sf.node_id = de.node_id
    WHERE qv.vec IS NOT NULL
      AND de.scope = 'document'
      AND de.dim = array_length(qv.vec)
),

per_node AS (
    SELECT
        node_id, doc_id,
        MAX(CASE WHEN embedding_type = 'structure' THEN score END) AS struct_sem,
        MAX(CASE WHEN embedding_type = 'full' THEN score END) AS full_sem,
        ARG_MAX(
            CASE WHEN embedding_type = 'full' THEN chunk_index END,
            CASE WHEN embedding_type = 'full' THEN score ELSE -1 END
        ) AS best_chunk_index,
        MAX(query_dim) AS query_dim
    FROM all_scored
    GROUP BY node_id, doc_id
)
```

**Why:** Two branches referencing `query_vec` and `_scope_filter` causes double evaluation of both (including the embed_query API call and 286K-row scope expansion).

## Inline Calibration with Window Functions

Instead of a separate `sem_stats` CTE that re-reads `limited`:

```sql
-- Instead of:
-- sem_stats AS (SELECT MAX(sem_score), quantile_cont(...) FROM limited),
-- calibrated AS (SELECT l.*, ... FROM limited l, sem_stats)

-- Use window functions in one pass:
normalized AS (
    SELECT
        node_id, doc_id, sem_score,
        sem_calibrate(sem_score, query_dim)
            * sem_query_confidence(
                MAX(sem_score) OVER (),
                quantile_cont(sem_score, 0.90) OVER (),
                COUNT(*) OVER (),
                query_dim
            ) AS sem_norm,
        sem_rank,
        rrf_score(sem_rank) AS rrf_sem
    FROM ranked
)
```

**Why:** `limited` referenced by both `sem_stats` and `calibrated` causes the entire ranking pipeline to run twice.

## Parameter Resolution Template

Standard first CTE for every TABLE macro:

```sql
CREATE OR REPLACE MACRO your_macro(
    q,
    scope := NULL,
    k := 200,
    threshold := 0.35
) AS TABLE (
WITH
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS query,
        NULLIF(TRIM(CAST(scope AS VARCHAR)), '') AS scope_filter,
        CAST(COALESCE(k, 200) AS BIGINT) AS result_limit,
        CAST(COALESCE(threshold, 0.35) AS DOUBLE) AS min_threshold
),
...
-- All downstream references use (SELECT col FROM params)
-- Never use raw macro parameters directly
```

## Scope Filter Usage

When no scope filtering is needed (common case in `search()`), `_scope_filter()` still expands through `glob_files` + node + span. For the unfiltered path, the fast path CTE avoids this:

```sql
-- _scope_filter has an internal fast path when all args are NULL.
-- It skips glob_files and scans node directly.
-- This works transparently — just call it normally.
_scope_filter(uri_glob := uri_glob, uri_like := uri_like)
```

When using _scope_filter in semantic search, join it once in the scan CTE — never in multiple branches.

## Benchmarking Template

Standard benchmark for a macro change:

```bash
# Deploy
powershell -File deploy.ps1
echo "" > .repoql/host.version
sleep 8

# Warmup (first query warms embed_query cache)
repoql.exe query "SELECT 1"
sleep 2
time repoql.exe query "SELECT COUNT(*) FROM your_macro('test query')"

# Real measurement (warm)
time repoql.exe query "SELECT COUNT(*) FROM your_macro('test query')"
```

## DuckDB Performance Facts

Quick reference for query optimization decisions:

| Fact | Value | Source |
|------|-------|--------|
| Linear cosine scan, 55K vectors @ 1024d | 30ms (DuckDB CLI), ~150ms (host) | Measured |
| HNSW index creation, 55K @ 1024d | 5.6s | Measured |
| HNSW query, 55K @ 1024d | 56ms | Measured — no faster than linear at this scale |
| gRPC CLI overhead | ~640ms | Measured |
| embed_query cold (Voyage API) | ~900ms | Measured |
| embed_query warm | ~35ms | Measured |
| _scope_filter (no args, 286K rows) | ~80ms | Measured |
| grep_matches on 11K documents | ~370ms-1.7s | Measured, depends on query |
| match_score on 11K documents | ~200ms | Measured |

**Key insight:** At 55K vectors, HNSW provides no benefit over linear scan. The bottleneck is never the cosine computation — it's CTE re-evaluation, UDF overhead, and macro expansion.
