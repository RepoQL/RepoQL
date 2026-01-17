# DuckDB Query Pipeline

> From SQL string to query results

## Overview

DuckDB processes SQL queries through a multi-stage pipeline. Each stage transforms the query representation, progressively moving from text to executable operations.

```
┌─────────────────────────────────────────────────────────────────┐
│                      Query Pipeline                              │
├─────────┬─────────┬──────────┬───────────┬──────────┬──────────┤
│ Parser  │ Binder  │ Logical  │ Optimizer │ Physical │ Executor │
│         │         │ Planner  │           │ Planner  │          │
└─────────┴─────────┴──────────┴───────────┴──────────┴──────────┘
```

## Stage 1: Parser

The parser converts a SQL string into an abstract syntax tree (AST).

### Input
```sql
SELECT name, age FROM users WHERE age > 21
```

### Output Tokens

| Token Type | Description | Example |
|------------|-------------|---------|
| `SQLStatement` | Top-level statement | SELECT, INSERT, CREATE |
| `QueryNode` | Query structure | SelectNode, SetOperationNode |
| `TableRef` | Table references | BaseTableRef, JoinRef |
| `ParsedExpression` | Expressions | ColumnRefExpression, ComparisonExpression |

### Key Characteristics

- **Catalog-unaware**: Parser doesn't know if tables/columns exist
- **Type-unaware**: No type information resolved yet
- **Syntax-only**: Only validates SQL grammar

### ParsedExpression Types

| Expression | Purpose |
|------------|---------|
| `ColumnRefExpression` | Column reference (`users.name`) |
| `ConstantExpression` | Literal value (`42`, `'hello'`) |
| `ComparisonExpression` | Comparison (`age > 21`) |
| `FunctionExpression` | Function call (`SUM(x)`) |
| `CastExpression` | Explicit cast (`CAST(x AS INT)`) |
| `SubqueryExpression` | Subquery (`(SELECT ...)`) |

## Stage 2: Binder

The binder resolves names and types by consulting the catalog.

### Transformations

| From (Parsed) | To (Bound) |
|---------------|------------|
| `ParsedExpression` | `Expression` (with types) |
| `TableRef` | `BoundTableRef` (resolved table) |
| `ColumnRefExpression` | `BoundColumnRefExpression` (with type, index) |

### Responsibilities

1. **Name resolution**: Map table/column names to catalog entries
2. **Type resolution**: Determine expression types
3. **Implicit casts**: Insert cast operators where needed
4. **Alias handling**: Resolve column aliases
5. **Star expansion**: Expand `SELECT *` to column list

### Error Detection

The binder throws errors for:
- Unknown tables or columns
- Type mismatches (when implicit cast isn't possible)
- Ambiguous column references
- Invalid function arguments

### Example

```sql
-- Input (parsed)
SELECT name FROM users WHERE age > '21'

-- After binding:
-- - 'users' resolved to table_id=5
-- - 'name' resolved to column_idx=0, type=VARCHAR
-- - 'age' resolved to column_idx=1, type=INTEGER
-- - '21' implicitly cast from VARCHAR to INTEGER
```

## Stage 3: Logical Planner

Creates a tree of `LogicalOperator` nodes representing the query semantically.

### Common LogicalOperators

| Operator | Purpose |
|----------|---------|
| `LogicalGet` | Table scan |
| `LogicalFilter` | WHERE clause |
| `LogicalProjection` | SELECT list |
| `LogicalAggregate` | GROUP BY / aggregates |
| `LogicalJoin` | JOIN operations |
| `LogicalOrder` | ORDER BY |
| `LogicalLimit` | LIMIT / OFFSET |
| `LogicalInsert` | INSERT statement |
| `LogicalUpdate` | UPDATE statement |
| `LogicalDelete` | DELETE statement |

### Example Tree

```sql
SELECT name FROM users WHERE age > 21 ORDER BY name
```

```
LogicalOrder (name ASC)
    │
    ▼
LogicalProjection (name)
    │
    ▼
LogicalFilter (age > 21)
    │
    ▼
LogicalGet (users)
```

## Stage 4: Optimizer

Transforms the logical plan into an optimized version. See [Optimizer](Optimizer.md) for details.

### Optimization Passes

| Pass | Description |
|------|-------------|
| Expression Rewriter | Constant folding, simplification |
| Filter Pushdown | Move filters closer to data source |
| Join Order Optimizer | Reorder joins using dynamic programming |
| Common Subexpression Elimination | Remove duplicate computations |
| IN Clause Rewriter | Convert large IN to joins |

## Stage 5: Column Binding Resolver

Converts table-based column references to index-based references for efficient DataChunk processing.

### Before

```
ColumnRef(table=users, column=name)
```

### After

```
ColumnRef(chunk_idx=0, vector_idx=1)
```

This allows operators to access data directly by index without name lookups.

## Stage 6: Physical Planner

Converts logical operators to physical operators that can be executed.

### Logical → Physical Mapping

| Logical | Physical | Notes |
|---------|----------|-------|
| `LogicalGet` | `PhysicalTableScan` | Sequential scan |
| `LogicalGet` | `PhysicalIndexScan` | When index available |
| `LogicalFilter` | `PhysicalFilter` | |
| `LogicalJoin` | `PhysicalHashJoin` | For equi-joins |
| `LogicalJoin` | `PhysicalNestedLoopJoin` | For non-equi joins |
| `LogicalJoin` | `PhysicalMergeJoin` | For sorted inputs |
| `LogicalAggregate` | `PhysicalHashAggregate` | Hash-based grouping |
| `LogicalOrder` | `PhysicalOrder` | External merge sort |

### Physical Plan Selection

The physical planner chooses implementations based on:
- Data characteristics (sorted, indexed)
- Available memory
- Cardinality estimates
- Join predicates (equi vs non-equi)

## Stage 7: Executor

Executes the physical plan using push-based vectorized execution.

### Push-Based Model

```
┌─────────────┐
│    Sink    │ ◀── DataChunk
└─────────────┘
       ▲
       │
┌─────────────┐
│  Operator   │ ◀── DataChunk
└─────────────┘
       ▲
       │
┌─────────────┐
│   Source    │ ── produces DataChunks ──▶
└─────────────┘
```

Data flows **up** through operators as DataChunks are pushed from sources to sinks.

### DataChunk

The fundamental unit of data during execution:

```cpp
struct DataChunk {
    vector<Vector> data;     // Column vectors
    idx_t count;             // Number of valid tuples (≤ 2048)
};
```

### Execution Flow

1. Source operators (scans) produce DataChunks
2. Each chunk contains up to 2048 tuples
3. Operators transform chunks and push to parent
4. Sink operators (result, insert) consume final chunks

## Pipeline Breakers

Some operators require all input before producing output:

| Operator | Reason |
|----------|--------|
| `ORDER BY` | Must see all data to sort |
| `GROUP BY` | Must see all groups |
| `Hash Join (build)` | Must build complete hash table |
| `DISTINCT` | Must track all seen values |

These operators create **pipeline boundaries**, potentially spilling to disk for large datasets.

## Debugging Queries

### EXPLAIN

View the optimized logical plan:

```sql
EXPLAIN SELECT name FROM users WHERE age > 21;
```

### EXPLAIN ANALYZE

View the physical plan with execution statistics:

```sql
EXPLAIN ANALYZE SELECT name FROM users WHERE age > 21;
```

Output includes:
- Operator tree
- Estimated vs actual cardinalities
- Execution time per operator
- Memory usage
