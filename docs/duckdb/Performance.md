# DuckDB Performance Best Practices

> Comprehensive guide to optimizing DuckDB for analytical workloads

## Overview

DuckDB is designed for analytical (OLAP) workloads: complex queries over large datasets. This guide covers configuration, query patterns, and data organization strategies to maximize performance.

### Key Performance Principles

| Principle | Implication |
|-----------|-------------|
| **Columnar storage** | Select only needed columns, avoid `SELECT *` |
| **Vectorized execution** | Batch operations outperform row-by-row |
| **Morsel-driven parallelism** | Data volume drives thread utilization |
| **Single-writer model** | Bulk loads beat many small transactions |
| **Zonemap pruning** | Sorted data enables skipping row groups |

## Quick Reference

### Configuration Cheat Sheet

```sql
-- Memory and threads
SET memory_limit = '8GB';           -- Default: 80% of RAM
SET threads = 4;                     -- Default: CPU core count
SET temp_directory = '/fast/ssd';    -- Spill location

-- Performance tuning
SET preserve_insertion_order = false; -- 3-10x faster loads
SET checkpoint_threshold = '256MB';   -- Reduce checkpoint frequency

-- Debugging
EXPLAIN ANALYZE SELECT ...;          -- Profile query execution
PRAGMA storage_info('table_name');   -- View compression stats
```

### Performance Hierarchy

| Operation | Relative Speed |
|-----------|----------------|
| Query native DuckDB table | 1x (baseline) |
| Query Parquet file | 1.1-5x slower |
| Query CSV file | 7-10x slower |
| Row-by-row INSERT | 10x slower than batched |

---

## Query Optimization

### Use EXPLAIN ANALYZE

Always profile slow queries:

```sql
EXPLAIN ANALYZE
SELECT customer_id, SUM(amount)
FROM orders
WHERE status = 'complete'
GROUP BY customer_id;
```

Output shows:
- **Physical operator tree** with execution order
- **Estimated vs actual cardinality** (row counts)
- **Cumulative time** per operator
- **Memory usage** patterns

**Key metrics to watch:**
1. Cardinality explosions (estimated << actual)
2. Nested loop joins on large tables
3. Filters far from data sources
4. Full table scans without pruning

### Filter Pushdown

DuckDB automatically pushes filters toward data sources. Write queries that enable this:

```sql
-- Good: Direct filter on base table
SELECT * FROM orders WHERE order_date > '2024-01-01';

-- Good: Filter propagates through equality join
SELECT * FROM orders o
JOIN customers c ON o.customer_id = c.id
WHERE c.country = 'USA';
-- Optimizer derives: o.customer_id IN (customers where country='USA')
```

**Struct filter pushdown** works on nested fields:
```sql
-- Filter pushes to Parquet reader
SELECT * FROM parquet_table WHERE metadata.created_at > '2024-01-01';
```

### Join Optimization

| Algorithm | When Used | Complexity |
|-----------|-----------|------------|
| **Hash Join** | Equality conditions | O(n + m) |
| **IEJoin** | Range predicates | Better than NLJ |
| **Nested Loop** | Complex predicates | O(n × m) - avoid |

**Best practices:**
- Use equality predicates (`=`) not ranges for join conditions
- Smaller table should be build side (DuckDB chooses automatically)
- Check for nested loops in EXPLAIN output

```sql
-- Force specific join order (debugging only)
SET disabled_optimizers = 'join_order,build_side_probe_side';
```

### Subquery vs CTE vs Temp Table

| Approach | When to Use |
|----------|-------------|
| **CTE** | Readability, single query scope |
| **CTE MATERIALIZED** | Force single evaluation of expensive subquery |
| **Temp Table** | Multi-query workflows, millions of rows |

```sql
-- Force materialization (evaluated once)
WITH expensive AS MATERIALIZED (
    SELECT * FROM large_table WHERE complex_condition
)
SELECT * FROM expensive e1 JOIN expensive e2 ON ...;
```

### Anti-Patterns to Avoid

| Anti-Pattern | Problem | Solution |
|--------------|---------|----------|
| `SELECT *` | Reads all columns | Select only needed columns |
| Large `OFFSET` | Holds offset+limit rows in memory | Keyset pagination |
| Many point lookups | Not optimized for OLTP | Batch with GROUP BY |
| Functions on columns | Prevents index/zonemap use | Filter on raw values |
| Reconnecting repeatedly | Clears caches | Reuse connections |

```sql
-- Bad: Large offset
SELECT * FROM table ORDER BY id LIMIT 100 OFFSET 1000000;

-- Good: Keyset pagination
SELECT * FROM table WHERE id > :last_seen_id ORDER BY id LIMIT 100;
```

---

## Data Loading

### Method Comparison

| Method | Speed | Use Case |
|--------|-------|----------|
| COPY from Parquet | Fastest | Bulk loading |
| Appender API | Fast | Programmatic streaming |
| COPY from CSV | Good | File-based loading |
| Batched INSERT | Moderate | Small batches in transaction |
| Row-by-row INSERT | Slowest | Avoid for bulk |

### COPY vs INSERT

```sql
-- Fastest: COPY from file
COPY table FROM 'data.parquet';

-- Fast: Batched in transaction
BEGIN;
INSERT INTO table VALUES (1, 'a'), (2, 'b'), ...;  -- Many values
COMMIT;

-- Slow: Auto-commit per statement (fsync overhead)
INSERT INTO table VALUES (1, 'a');
INSERT INTO table VALUES (2, 'b');
```

**Key insight:** Auto-commit triggers `fsync` after each statement. One benchmark showed 1M row-by-row inserts writing 15GB to disk for a 15MB final database.

### File Format Performance

| Format | Load Time | Query Time | Storage |
|--------|-----------|------------|---------|
| Parquet (ZSTD) | 1x | 1x | 1x |
| Parquet (Snappy) | 0.9x | 1x | 1.2x |
| CSV | 2-3x slower | 7-10x slower | 5x larger |
| JSON | 3-4x slower | Similar to CSV | 20x larger |

**Recommendation:** Convert CSV/JSON to Parquet for repeated queries:
```sql
COPY (SELECT * FROM 'data.csv') TO 'data.parquet' (FORMAT PARQUET);
```

### Parallel Loading

DuckDB parallelizes at the **row group level** (122,880 rows default):

- Need k × 122,880 rows to utilize k threads
- Multiple files parallelize across all row groups
- One file per thread maximizes throughput

```sql
-- Parallel load from multiple files
COPY table FROM 'data/*.parquet';
```

### Hive Partitioning

For large datasets, especially on cloud storage:

```sql
-- Reading: automatic partition pruning
SELECT * FROM read_parquet('data/*/*/*.parquet', hive_partitioning = true)
WHERE year = 2024 AND month = 12;
-- Only reads data/year=2024/month=12/*.parquet

-- Writing: create partitioned output
COPY orders TO 'output' (FORMAT PARQUET, PARTITION_BY (year, month));
```

---

## Memory Management

### Configuration

```sql
SET memory_limit = '8GB';              -- Max buffer manager memory
SET temp_directory = '/path/to/ssd';   -- Spill location
SET max_temp_directory_size = '50GB';  -- Limit temp space
```

**Guidelines:**
- Default: 80% of system RAM
- Aim for 1-4 GB per thread
- Minimum: 125 MB per thread

### When Memory Is Constrained

Reduce threads before reducing memory:

```sql
-- Better: Fewer threads with adequate memory each
SET threads = 4;
SET memory_limit = '8GB';  -- 2GB per thread

-- Worse: Many threads starved for memory
SET threads = 16;
SET memory_limit = '8GB';  -- 512MB per thread
```

### Spilling to Disk

Most operations spill automatically when memory exhausted:

| Spills to Disk | Does NOT Spill |
|----------------|----------------|
| GROUP BY | `list()` aggregate |
| JOIN | `string_agg()` |
| ORDER BY | PIVOT (uses `list()`) |
| Window functions | `mode()`, `quantile()` |

```sql
-- Allow result reordering to reduce memory
SET preserve_insertion_order = false;
```

### Monitoring

```sql
-- Memory usage by component
SELECT * FROM duckdb_memory();

-- Temp file usage
SELECT * FROM duckdb_temporary_files();

-- Database size including WAL
PRAGMA database_size;
```

---

## Parallelism

### Thread Configuration

```sql
SET threads = 8;  -- Physical cores (not hyperthreads)
```

**Special cases:**
- HyperThreading: Limit to physical cores to avoid overhead
- Network I/O: Set 2-5x cores for latency hiding
- Memory-constrained: Reduce threads first

### Morsel-Driven Execution

DuckDB divides data into morsels (~122,880 rows) distributed across threads:

```
Table (1M rows)
    ├── Row Group 0 → Thread 0
    ├── Row Group 1 → Thread 1
    ├── Row Group 2 → Thread 2
    └── ...
```

**Operations that parallelize well:**
- Table scans
- Filters
- Hash aggregations
- Hash joins
- Sorting (v1.4+)

**Operations with limitations:**
- Small datasets (< threads × 122,880 rows)
- Single-row-group Parquet files
- Exact quantile/median

### Pipeline Breakers

These operators must consume all input before producing output:

| Operator | Impact |
|----------|--------|
| ORDER BY | Must see all rows |
| GROUP BY | Must process all groups |
| Hash Join (build) | Must complete hash table |
| Window (partitioned) | Must process full partition |

Pipeline breakers create synchronization points. Minimize their count in hot paths.

### Order Preservation

```sql
-- Default: preserves order (limits parallelism)
SET preserve_insertion_order = true;

-- Faster: allows reordering
SET preserve_insertion_order = false;
```

**Benchmark impact:** 3-10x faster for bulk operations when order doesn't matter.

---

## Storage Optimization

### Compression

DuckDB automatically selects compression per column segment:

| Algorithm | Best For |
|-----------|----------|
| Constant | Single repeated value, NULLs |
| RLE | Sorted/grouped repeated values |
| Dictionary | Text with duplicates |
| FSST | URLs, paths, patterned strings |
| Bit Packing | Small-range integers |
| FOR | Timestamps, sequential IDs |
| ALP/Chimp/Patas | Floating-point |

```sql
-- View compression choices
PRAGMA storage_info('table_name');

-- Force specific compression (testing)
PRAGMA force_compression = 'dictionary';
```

### Row Group Sizing

Default: 122,880 rows per row group.

| Size | Impact |
|------|--------|
| < 5,000 | 5-10x slower |
| 5,000-20,000 | 1.5-2.5x slower |
| 100,000+ | Near optimal |

```sql
-- Configure for specific database
ATTACH 'file.db' (ROW_GROUP_SIZE 100000);

-- Parquet output
COPY table TO 'out.parquet' (ROW_GROUP_SIZE 100000);
```

### Sorting for Zonemap Efficiency

DuckDB maintains min/max (zonemap) per column per row group. Sorting maximizes pruning:

```sql
-- Create sorted table
CREATE TABLE events AS
SELECT * FROM raw_events
ORDER BY category, date_trunc('month', timestamp), user_id;

-- Query benefits from zonemap pruning
SELECT * FROM events WHERE category = 'purchase' AND timestamp > '2024-01-01';
-- Skips row groups where category min/max excludes 'purchase'
```

**Sorting guidelines:**
1. Sort by columns used in WHERE clauses
2. Low-cardinality columns first
3. Round timestamps to coarser granularity
4. VARCHAR: only first 8 characters used in zonemaps

---

## Indexing

### When to Use Indexes

DuckDB's ART index helps **only** for:
- Point queries (equality `=`)
- Very high selectivity (< 0.1% of rows)
- Constraint enforcement

**Do NOT create indexes for:**
- Analytical queries (aggregations, joins)
- Range queries
- Most OLAP workloads

```sql
-- ART index only used when matching rows < MAX(2048, 0.001 × table_size)
-- Configure thresholds:
SET index_scan_percentage = 0.001;  -- 0.1%
SET index_scan_max_count = 2048;
```

### Zonemaps Are Usually Better

DuckDB automatically creates zonemaps for all columns. Combined with sorted data, this provides index-like benefits without write overhead:

```sql
-- Sorting provides 10x speedup on selective queries
-- vs manual index which:
--   - Slows writes
--   - Consumes memory
--   - Only helps < 0.1% selectivity
```

### Full-Text Search

```sql
INSTALL fts; LOAD fts;

-- Create FTS index
PRAGMA create_fts_index('docs', 'id', 'title', 'content');

-- Search
SELECT * FROM fts_main_docs.match_bm25('id', 'search terms');

-- IMPORTANT: Index does NOT auto-update
-- After table changes:
PRAGMA drop_fts_index('docs');
PRAGMA create_fts_index('docs', 'id', 'title', 'content');
```

---

## Aggregation

### Hash vs Sort Aggregation

DuckDB uses hash aggregation (O(n)) by default. Sort aggregation occurs only for:
- `ORDER BY` within aggregate functions
- Holistic aggregates requiring ordering

```sql
-- Efficient: hash aggregation
SELECT category, SUM(amount) FROM sales GROUP BY category;

-- Less efficient: sorting within aggregate
SELECT category, LIST(item ORDER BY date) FROM sales GROUP BY category;
```

### Window Function Optimization

**Efficient patterns:**
```sql
-- Streaming window (no materialization needed)
SELECT *, ROW_NUMBER() OVER () FROM table;

-- Shared data layout (same PARTITION BY/ORDER BY)
SELECT *,
    SUM(x) OVER w,
    AVG(x) OVER w
FROM table
WINDOW w AS (PARTITION BY category ORDER BY date);
```

**Inefficient patterns:**
```sql
-- Materializes entire table
SELECT * FROM (
    SELECT *, RANK() OVER (PARTITION BY id ORDER BY date DESC) as rn
    FROM large_table
) WHERE rn = 1;

-- Better: use aggregation
SELECT t.*
FROM large_table t
JOIN (SELECT id, MAX(date) as max_date FROM large_table GROUP BY id) m
ON t.id = m.id AND t.date = m.max_date;
```

### DISTINCT Optimization

Multiple COUNT DISTINCT is expensive:

```sql
-- Slow: separate hash sets per DISTINCT
SELECT COUNT(DISTINCT a), COUNT(DISTINCT b), COUNT(DISTINCT c) FROM table;

-- Faster: approximate
SELECT
    approx_count_distinct(a),
    approx_count_distinct(b),
    approx_count_distinct(c)
FROM table;
```

### Approximate Aggregates

| Function | Algorithm | Use Case |
|----------|-----------|----------|
| `approx_count_distinct(x)` | HyperLogLog | Cardinality |
| `approx_quantile(x, p)` | T-Digest | Percentiles |
| `approx_top_k(x, k)` | Space-Saving | Frequent values |

### FILTER Clause

Single-pass conditional aggregation:

```sql
-- Efficient: single pass
SELECT
    SUM(amount),
    SUM(amount) FILTER (WHERE region = 'north'),
    SUM(amount) FILTER (WHERE region = 'south')
FROM sales;
```

---

## Checkpoints and Durability

### WAL Configuration

```sql
-- Checkpoint when WAL reaches threshold (default 16MB)
SET checkpoint_threshold = '256MB';

-- Force checkpoint
CHECKPOINT;
FORCE CHECKPOINT;  -- Waits for lock (v1.4+)
```

### Best Practices

- **Bulk loads:** Increase checkpoint_threshold to reduce I/O
- **Durability critical:** Lower threshold, explicit CHECKPOINT
- **Space reclamation:** Checkpoint triggers vacuuming of deleted rows

---

## Performance Checklist

### Before Deploying

1. [ ] Convert CSV/JSON to Parquet
2. [ ] Sort data by frequently filtered columns
3. [ ] Set appropriate memory_limit and threads
4. [ ] Use transactions for bulk inserts
5. [ ] Remove unnecessary indexes

### Debugging Slow Queries

1. [ ] Run `EXPLAIN ANALYZE`
2. [ ] Check for nested loop joins
3. [ ] Verify filter pushdown (filters near scans)
4. [ ] Look for cardinality estimation errors
5. [ ] Check memory/temp file usage

### Production Configuration

```sql
-- Analytical workload (recommended starting point)
SET memory_limit = '16GB';
SET threads = 8;
SET temp_directory = '/fast/ssd/duckdb_temp';
SET preserve_insertion_order = false;
SET checkpoint_threshold = '256MB';

-- Memory-constrained (laptop)
SET memory_limit = '4GB';
SET threads = 4;
SET temp_directory = '/tmp/duckdb';

-- Network-bound (S3/HTTP)
SET threads = 20;  -- 4-5x CPU cores
```

---

## Sources

- [DuckDB Performance Guide](https://duckdb.org/docs/stable/guides/performance/overview)
- [Tuning Workloads](https://duckdb.org/docs/stable/guides/performance/how_to_tune_workloads)
- [Memory Management](https://duckdb.org/2024/07/09/memory-management)
- [Lightweight Compression](https://duckdb.org/2022/10/28/lightweight-compression)
- [Parallel Grouped Aggregation](https://duckdb.org/2022/03/07/aggregate-hashtable)
- [Windowing in DuckDB](https://duckdb.org/2021/10/13/windowing)
- [Optimizers: The Low-Key MVP](https://duckdb.org/2024/11/14/optimizers)
- [Sorting for Fast Selective Queries](https://duckdb.org/2025/05/14/sorting-for-fast-selective-queries)
- [File Formats](https://duckdb.org/docs/stable/guides/performance/file_formats)
- [Indexing Guide](https://duckdb.org/docs/stable/guides/performance/indexing)
