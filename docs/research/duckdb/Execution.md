# DuckDB Execution

> Vectorized push-based query execution

## Overview

DuckDB uses a **push-based vectorized execution model**. Data flows through operators in fixed-size chunks called `DataChunks`, with each chunk containing up to 2048 tuples. This design maximizes CPU efficiency through cache-friendly access patterns and SIMD utilization.

## Vectorized Execution

### Why Vectorized?

| Approach | Description | Performance |
|----------|-------------|-------------|
| Tuple-at-a-time | Process one row per function call | Poor (function call overhead) |
| Vectorized | Process 2048 rows per function call | Excellent (amortized overhead) |

### Benefits

1. **Reduced interpretation overhead**: One function call processes thousands of tuples
2. **Cache efficiency**: Data fits in CPU cache during processing
3. **SIMD utilization**: Process multiple values with single instruction
4. **Branch prediction**: Tight loops improve CPU prediction

## Core Data Structures

### Vector

A `Vector` holds values for a single column:

```cpp
struct Vector {
    LogicalType type;        // Data type
    VectorType vector_type;  // Storage type (flat, constant, etc.)
    data_ptr_t data;         // Raw value storage
    ValidityMask validity;   // NULL bitmap
};
```

### Vector Types

| Type | Description | Use Case |
|------|-------------|----------|
| `FLAT_VECTOR` | Standard array of values | Most operations |
| `CONSTANT_VECTOR` | Single value repeated | Literals, constants |
| `DICTIONARY_VECTOR` | Index + dictionary | Compressed/filtered data |
| `SEQUENCE_VECTOR` | Start + increment | Generated sequences |

### DataChunk

A `DataChunk` is a collection of vectors (column batch):

```cpp
struct DataChunk {
    vector<Vector> data;     // Column vectors
    idx_t count;             // Valid tuple count (≤ STANDARD_VECTOR_SIZE)
};
```

```
DataChunk (count = 1500)
┌─────────────────────────────────────────────────┐
│  Vector 0    │  Vector 1    │  Vector 2        │
│  (INTEGER)   │  (VARCHAR)   │  (DOUBLE)        │
├──────────────┼──────────────┼──────────────────┤
│  [1,2,3,...] │  [a,b,c,...] │  [1.1,2.2,...]   │
│  (1500 vals) │  (1500 vals) │  (1500 vals)     │
└──────────────┴──────────────┴──────────────────┘
```

### STANDARD_VECTOR_SIZE

The constant `STANDARD_VECTOR_SIZE` defines maximum tuples per chunk:

| Property | Value |
|----------|-------|
| Default | 2048 |
| Rationale | Fits in L2 cache, good SIMD alignment |
| Trade-off | Larger = less overhead, worse cache locality |

## Push-Based Model

Data is **pushed** from source operators through the tree to sink operators.

### Pull vs Push

```
Pull-Based (Volcano)          Push-Based (DuckDB)
─────────────────────         ─────────────────────
      Result                        Result
        │                             ▲
        │ next()                      │ push()
        ▼                             │
      Filter                        Filter
        │                             ▲
        │ next()                      │ push()
        ▼                             │
       Scan ──▶                     Scan ──▶

(Consumer pulls data)         (Producer pushes data)
```

### Push Model Benefits

1. **Better pipelining**: Data flows without intermediate materialization
2. **Parallelism**: Easier to parallelize independent pipelines
3. **Cache efficiency**: Process data while hot in cache

## Operator Execution

### Operator Interface

```cpp
class PhysicalOperator {
    // Get next chunk of results
    virtual void GetChunk(ExecutionContext &context, DataChunk &chunk);

    // Initialize operator state
    virtual void InitializeState(ExecutionContext &context);
};
```

### Source Operators

Produce DataChunks from external sources:

| Operator | Source |
|----------|--------|
| `PhysicalTableScan` | Table storage |
| `PhysicalIndexScan` | Index + table |
| `PhysicalParquetScan` | Parquet files |
| `PhysicalCSVScan` | CSV files |

### Intermediate Operators

Transform DataChunks:

| Operator | Transformation |
|----------|----------------|
| `PhysicalFilter` | Apply predicates |
| `PhysicalProjection` | Compute expressions |
| `PhysicalHashJoin` | Join via hash table |
| `PhysicalHashAggregate` | Group and aggregate |

### Sink Operators

Consume final results:

| Operator | Destination |
|----------|-------------|
| `PhysicalResultCollector` | Query results |
| `PhysicalInsert` | Table storage |
| `PhysicalExport` | External files |

## Parallelism

### Morsel-Driven Parallelism

DuckDB divides work into **morsels** (chunks of row groups) that threads process independently:

```
┌─────────────────────────────────────────────────┐
│                    Table                         │
├─────────────┬─────────────┬─────────────────────┤
│ Row Group 0 │ Row Group 1 │ Row Group 2 │ ...   │
└──────┬──────┴──────┬──────┴──────┬──────────────┘
       │             │             │
       ▼             ▼             ▼
   Thread 0      Thread 1      Thread 2
```

### Pipeline Parallelism

Independent query pipelines can execute concurrently:

```sql
SELECT * FROM a JOIN b ON ... JOIN c ON ...
```

```
Pipeline 1: Scan(a) ──▶ Build Hash Table
Pipeline 2: Scan(b) ──▶ Build Hash Table
Pipeline 3: Scan(c) ──▶ Probe ──▶ Probe ──▶ Result
            (can start after pipelines 1,2 complete)
```

### Thread Configuration

```sql
-- Set number of threads
SET threads = 8;

-- Use all available cores (default)
SET threads = 0;
```

## Pipeline Breakers

Some operators require all input before producing output, creating pipeline boundaries:

| Operator | Reason |
|----------|--------|
| `ORDER BY` | Must see all data to sort |
| `GROUP BY` (hash) | Must see all groups |
| `Hash Join (build)` | Must build complete hash table |
| `DISTINCT` | Must track all seen values |
| Window functions | May need full partition |

### Pipeline Structure

```sql
SELECT name, SUM(amount)
FROM orders
WHERE status = 'complete'
GROUP BY name
ORDER BY SUM(amount) DESC;
```

```
Pipeline 1: Scan ──▶ Filter ──▶ Hash Aggregate (build)
                                      │
                                 [BREAKER]
                                      │
Pipeline 2: Hash Aggregate ──▶ Sort (build)
                                      │
                                 [BREAKER]
                                      │
Pipeline 3: Sort ──▶ Result
```

## Expression Execution

Expressions are compiled to operate on vectors:

### Scalar Functions

```cpp
// Vector addition: result = left + right
void AddFunction(Vector &left, Vector &right, Vector &result, idx_t count) {
    auto ldata = FlatVector::GetData<int64_t>(left);
    auto rdata = FlatVector::GetData<int64_t>(right);
    auto result_data = FlatVector::GetData<int64_t>(result);

    for (idx_t i = 0; i < count; i++) {
        result_data[i] = ldata[i] + rdata[i];
    }
}
```

### NULL Handling

Validity masks track NULL values:

```cpp
// Check if value is NULL
if (!validity.RowIsValid(i)) {
    // Handle NULL
}
```

### Selection Vectors

Filter results without copying data:

```cpp
SelectionVector sel;  // Indices of matching rows
idx_t match_count = 0;

for (idx_t i = 0; i < count; i++) {
    if (data[i] > 10) {
        sel[match_count++] = i;
    }
}
```

## UDF Integration

### Vectorized UDFs

User-defined functions operate on entire vectors:

```cpp
typedef std::function<void(DataChunk &args, ExpressionState &expr, Vector &result)>
    scalar_function_t;

// Example: custom string length function
void MyStrLen(DataChunk &args, ExpressionState &expr, Vector &result) {
    auto &input = args.data[0];
    auto result_data = FlatVector::GetData<int64_t>(result);

    for (idx_t i = 0; i < args.size(); i++) {
        auto str = input.GetValue(i).ToString();
        result_data[i] = str.length();
    }
}
```

### Performance Impact

| UDF Type | Overhead | Use Case |
|----------|----------|----------|
| Vectorized | Low | Performance-critical operations |
| Scalar | High | Simple, infrequent operations |

## Execution Monitoring

### EXPLAIN ANALYZE

```sql
EXPLAIN ANALYZE SELECT * FROM orders WHERE amount > 100;
```

Output includes:
- Physical operator tree
- Estimated vs actual cardinality
- Time per operator
- Rows processed

### Profiling

```sql
-- Enable profiling
PRAGMA enable_profiling;

-- Run query
SELECT ...;

-- View results
PRAGMA profiling_output;
```
