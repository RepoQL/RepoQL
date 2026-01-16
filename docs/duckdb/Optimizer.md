# DuckDB Optimizer

> Query optimization strategies and techniques

## Overview

After the logical planner creates the initial query tree, DuckDB's optimizer transforms it into an efficient execution plan. The optimizer applies multiple passes, each targeting specific optimization opportunities.

## Optimization Pipeline

```
┌──────────────────────┐
│   Logical Plan       │
│   (from planner)     │
└──────────┬───────────┘
           │
           ▼
┌──────────────────────┐
│ Expression Rewriter  │  ← Constant folding, simplification
└──────────┬───────────┘
           │
           ▼
┌──────────────────────┐
│   Filter Pushdown    │  ← Move filters to data sources
└──────────┬───────────┘
           │
           ▼
┌──────────────────────┐
│ Join Order Optimizer │  ← Reorder joins optimally
└──────────┬───────────┘
           │
           ▼
┌──────────────────────┐
│   CSE Elimination    │  ← Remove duplicate expressions
└──────────┬───────────┘
           │
           ▼
┌──────────────────────┐
│  IN Clause Rewriter  │  ← Convert large IN to joins
└──────────┬───────────┘
           │
           ▼
┌──────────────────────┐
│   Optimized Plan     │
└──────────────────────┘
```

## Expression Rewriter

Simplifies expressions and performs constant folding.

### Constant Folding

Evaluate constant expressions at compile time:

```sql
-- Before optimization
SELECT * FROM t WHERE x > 1 + 1;

-- After optimization
SELECT * FROM t WHERE x > 2;
```

### Simplification Rules

| Before | After |
|--------|-------|
| `x + 0` | `x` |
| `x * 1` | `x` |
| `x * 0` | `0` |
| `x AND TRUE` | `x` |
| `x OR FALSE` | `x` |
| `x AND FALSE` | `FALSE` |
| `NOT NOT x` | `x` |
| `x = x` | `TRUE` (if not nullable) |

### Boolean Simplification

```sql
-- Before
SELECT * FROM t WHERE (a = 1 AND b = 2) OR (a = 1 AND b = 3);

-- After (factoring)
SELECT * FROM t WHERE a = 1 AND (b = 2 OR b = 3);
```

## Filter Pushdown

Moves filter predicates as close to data sources as possible.

### Basic Pushdown

```sql
-- Before optimization
SELECT * FROM (SELECT * FROM orders) sub WHERE status = 'complete';

-- After optimization (filter pushed into subquery)
SELECT * FROM (SELECT * FROM orders WHERE status = 'complete') sub;
```

### Join Filter Pushdown

```sql
-- Before
SELECT * FROM orders o
JOIN customers c ON o.customer_id = c.id
WHERE c.country = 'USA';

-- After (filter pushed to customers scan)
SELECT * FROM orders o
JOIN (SELECT * FROM customers WHERE country = 'USA') c
ON o.customer_id = c.id;
```

### Partition Pruning

For partitioned data, filters eliminate entire partitions:

```sql
-- With Hive partitioning
SELECT * FROM read_parquet('orders/*/*/*.parquet', hive_partitioning = true)
WHERE year = 2024 AND month = 12;
-- Only reads files matching year=2024/month=12
```

### Row Group Pruning

Min/max metadata enables skipping row groups:

```sql
-- If row group has max(age) = 20, this query skips it
SELECT * FROM users WHERE age > 25;
```

## Join Order Optimizer

Determines optimal join sequence using dynamic programming.

### The Problem

Join order matters dramatically:

```sql
-- orders: 10M rows, customers: 100K rows, countries: 200 rows

-- Bad order: build huge intermediate result first
orders ⋈ customers ⋈ countries (filter: country = 'USA')

-- Good order: filter first, then join
(countries WHERE name = 'USA') ⋈ customers ⋈ orders
```

### DPccp Algorithm

DuckDB uses the DPccp (Dynamic Programming connected subgraph Complement Pairs) algorithm:

1. Enumerate all possible join orderings
2. Use dynamic programming to find optimal cost
3. Consider both left-deep and bushy trees

### Cardinality Estimation

The optimizer estimates result sizes using:
- Table statistics (row counts)
- Column statistics (distinct values, histograms)
- Join selectivity heuristics

```sql
-- View estimated cardinality
EXPLAIN SELECT * FROM orders JOIN customers ON ...;
```

### Disabling Join Optimizer

For debugging or forcing specific order:

```sql
-- Disable join reordering
SET disabled_optimizers = 'join_order';

-- Also disable build/probe side selection
SET disabled_optimizers = 'join_order,build_side_probe_side';

-- Re-enable
SET disabled_optimizers = '';
```

## Common Subexpression Elimination

Removes duplicate computations.

### Example

```sql
-- Before
SELECT
    price * quantity AS total,
    (price * quantity) * tax_rate AS tax,
    (price * quantity) + (price * quantity) * tax_rate AS final
FROM orders;

-- After (price * quantity computed once)
SELECT
    _cse_1 AS total,
    _cse_1 * tax_rate AS tax,
    _cse_1 + _cse_1 * tax_rate AS final
FROM (
    SELECT *, price * quantity AS _cse_1 FROM orders
);
```

### Scope

CSE applies to:
- Projection expressions
- Filter conditions
- Repeated function calls

## IN Clause Rewriter

Transforms large IN clauses into more efficient operations.

### Small IN → OR

```sql
-- Original
SELECT * FROM t WHERE x IN (1, 2, 3);

-- Rewritten
SELECT * FROM t WHERE x = 1 OR x = 2 OR x = 3;
```

### Large IN → Join

```sql
-- Original (many values)
SELECT * FROM orders WHERE customer_id IN (1, 2, 3, ..., 1000);

-- Rewritten to MARK join
SELECT * FROM orders o
WHERE EXISTS (
    SELECT 1 FROM (VALUES (1), (2), ..., (1000)) v(id)
    WHERE o.customer_id = v.id
);
```

### Threshold

The rewriting threshold depends on:
- Number of IN values
- Column statistics
- Estimated selectivity

## Additional Optimizations

### Projection Pushdown

Only read required columns:

```sql
-- Query
SELECT name FROM users WHERE age > 21;

-- Optimizer ensures only 'name' and 'age' columns are read
-- Other columns (email, address, etc.) are not loaded
```

### Limit Pushdown

Push LIMIT into subqueries when safe:

```sql
-- Before
SELECT * FROM (SELECT * FROM huge_table ORDER BY date) LIMIT 10;

-- After (top-N optimization)
-- Uses heap to track only top 10 during scan
```

### Aggregate Pushdown

Push aggregates below joins when possible:

```sql
-- Before
SELECT c.country, COUNT(*)
FROM orders o
JOIN customers c ON o.customer_id = c.id
GROUP BY c.country;

-- Optimizer may pre-aggregate on orders side
```

## Statistics

### Automatic Statistics

DuckDB automatically maintains:
- Row counts per table
- Distinct value counts
- Min/max values per column

### ANALYZE

Force statistics refresh:

```sql
-- Analyze all tables
ANALYZE;

-- Analyze specific table
ANALYZE customers;
```

### Viewing Statistics

```sql
-- Table statistics
SELECT * FROM duckdb_tables();

-- Column statistics (via EXPLAIN)
EXPLAIN SELECT * FROM t WHERE col = 'value';
```

## Debugging Optimization

### EXPLAIN

View optimized logical plan:

```sql
EXPLAIN
SELECT * FROM orders o
JOIN customers c ON o.customer_id = c.id
WHERE c.country = 'USA';
```

### EXPLAIN ANALYZE

View physical plan with execution statistics:

```sql
EXPLAIN ANALYZE
SELECT * FROM orders o
JOIN customers c ON o.customer_id = c.id
WHERE c.country = 'USA';
```

Output shows:
- Operator tree
- Estimated vs actual cardinality
- Time per operator
- Memory usage

### Optimizer Settings

```sql
-- Disable specific optimizers
SET disabled_optimizers = 'filter_pushdown';

-- List available optimizers
SELECT * FROM duckdb_optimizers();

-- Enable optimizer debug output
SET explain_output = 'all';
```

## Best Practices

### Help the Optimizer

1. **Provide statistics**: Run ANALYZE after bulk loads
2. **Use appropriate types**: Smaller types = better estimates
3. **Write clear predicates**: Simple conditions optimize better

### Query Writing

```sql
-- Good: Direct filter
SELECT * FROM orders WHERE status = 'complete';

-- Avoid: Function on column (prevents index use)
SELECT * FROM orders WHERE UPPER(status) = 'COMPLETE';

-- Good: Explicit join conditions
SELECT * FROM a JOIN b ON a.id = b.a_id;

-- Avoid: Implicit joins (harder to optimize)
SELECT * FROM a, b WHERE a.id = b.a_id;
```

### When Optimizer Fails

If optimizer chooses poor plan:
1. Check statistics are current (`ANALYZE`)
2. Simplify complex expressions
3. Disable specific optimizers to identify issue
4. Consider query restructuring
