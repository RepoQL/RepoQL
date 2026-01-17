# DuckDB Storage

> File format, row groups, compression, and buffer management

## Overview

DuckDB stores data in a single file (or in-memory) using a columnar format optimized for analytical queries. The storage system is designed for efficient sequential reads and compression.

## File Structure

```
┌────────────────────────────────────────────────┐
│                 DuckDB File                     │
├────────────────────────────────────────────────┤
│  Header (checksum + magic bytes + version)     │
├────────────────────────────────────────────────┤
│  Block 0 (256KB)                               │
├────────────────────────────────────────────────┤
│  Block 1 (256KB)                               │
├────────────────────────────────────────────────┤
│  Block 2 (256KB)                               │
├────────────────────────────────────────────────┤
│  ...                                           │
├────────────────────────────────────────────────┤
│  Metadata / Catalog                            │
└────────────────────────────────────────────────┘
```

### Header

The file begins with:
1. `uint64_t` checksum for main header
2. Magic bytes: `DUCK`
3. Storage version number

### Blocks

| Property | Value |
|----------|-------|
| Size | 256KB |
| Purpose | Fundamental I/O unit |
| Design rationale | Large enough for sequential efficiency, small enough for memory management |

## Row Groups

Data is partitioned horizontally into **row groups**, similar to Parquet.

```
┌─────────────────────────────────────────────────────────────┐
│                         Table                                │
├─────────────────────────────────────────────────────────────┤
│  Row Group 0  │  Row Group 1  │  Row Group 2  │  ...        │
│  (122,880     │  (122,880     │  (122,880     │             │
│   rows)       │   rows)       │   rows)       │             │
├───────────────┼───────────────┼───────────────┼─────────────┤
│  col_a │ col_b│  col_a │ col_b│  col_a │ col_b│             │
└───────────────┴───────────────┴───────────────┴─────────────┘
```

### Row Group Properties

| Property | Default | Notes |
|----------|---------|-------|
| Row count | ~122,880 | Multiple of vector size (2048 × 60) |
| Configurable | Yes | Via `ATTACH` or database settings |

### Configuration

```sql
-- Set row group size when attaching
ATTACH '/path/to/db.duckdb' AS mydb (ROW_GROUP_SIZE 16384);
```

### Row Group Benefits

1. **Parallelism**: Each row group can be processed independently
2. **Compression**: Better compression within homogeneous groups
3. **Pruning**: Skip entire row groups via min/max metadata

## Columnar Storage

Within each row group, data is stored column by column:

```
Row Group
┌─────────────────────────────────────┐
│  Column A  │  Column B  │  Column C │
│  ┌───────┐ │  ┌───────┐ │  ┌───────┐│
│  │ val 1 │ │  │ val 1 │ │  │ val 1 ││
│  │ val 2 │ │  │ val 2 │ │  │ val 2 ││
│  │ val 3 │ │  │ val 3 │ │  │ val 3 ││
│  │  ...  │ │  │  ...  │ │  │  ...  ││
│  └───────┘ │  └───────┘ │  └───────┘│
└─────────────────────────────────────┘
```

### Benefits of Columnar Storage

| Benefit | Description |
|---------|-------------|
| Reduced I/O | Read only needed columns |
| Better compression | Similar values compress well together |
| Vectorized processing | Operate on column segments efficiently |
| Cache efficiency | Sequential access patterns |

## Compression

DuckDB applies lightweight compression by default on persistent databases.

### Compression Algorithms

| Algorithm | Best For | Description |
|-----------|----------|-------------|
| Constant | Single-value columns | Store value once |
| RLE | Repeated values | Run-length encoding |
| Bit Packing | Small integers | Pack values into fewer bits |
| Frame of Reference | Narrow ranges | Store offset + deltas |
| Dictionary | Low cardinality | Map values to integers |
| FSST | Strings | Fast Static Symbol Table |
| ALP | Floats | Adaptive Lossless floating-Point |
| Chimp/Patas | Time series | Specialized float compression |

### Compression Selection

DuckDB automatically selects compression per column segment based on data characteristics. No manual configuration required.

### Compression Ratios

Approximate ratios (vary by data):
- 100 GB CSV → ~25 GB DuckDB
- 100 GB Parquet → ~120 GB DuckDB (Parquet often has heavier compression)

## Checkpoints and WAL

DuckDB uses Write-Ahead Logging (WAL) for durability.

### Write-Ahead Log

```
┌─────────────────┐
│   Transaction   │
│    (writes)     │
└────────┬────────┘
         │
         ▼
┌─────────────────┐      ┌─────────────────┐
│   WAL File      │ ───▶ │  Database File  │
│  (sequential)   │      │  (checkpoint)   │
└─────────────────┘      └─────────────────┘
```

### Checkpoint

Synchronizes WAL with database file:

```sql
-- Normal checkpoint (waits for transactions)
CHECKPOINT;

-- Force checkpoint (interrupts transactions)
FORCE CHECKPOINT;

-- Checkpoint specific database
CHECKPOINT mydb;
```

### Automatic Checkpointing

DuckDB periodically checkpoints to:
- Reclaim WAL space
- Ensure durability
- Improve startup time

## Buffer Manager

The buffer manager controls memory usage for database pages.

### Memory Limit

```sql
-- Set buffer manager memory limit
SET memory_limit = '4GB';

-- Check current setting
SELECT current_setting('memory_limit');
```

### Important Caveat

The memory limit applies only to the buffer manager. Actual memory usage can exceed this due to:
- Vector allocations
- Query result buffers
- Aggregate function state (list, mode, quantile, string_agg)
- Hash tables for joins/aggregates

### Out-of-Core Processing

When data exceeds memory, DuckDB spills to disk:

```sql
-- Configure temporary directory for spilling
SET temp_directory = '/path/to/temp/';
```

### Tuning Memory

```sql
-- Reduce threads to lower memory usage
SET threads = 4;

-- Disable insertion order preservation (reduces memory)
SET preserve_insertion_order = false;

-- Counter-intuitively, lower limits can help
-- (prevents OS from killing process)
SET memory_limit = '8GB';  -- On 16GB system
```

## Indexes

### Adaptive Radix Tree (ART)

DuckDB uses ART indexes for:
- Primary key constraints
- Unique constraints
- Explicit indexes

```sql
-- Automatically created for constraints
CREATE TABLE users (
    id INTEGER PRIMARY KEY,  -- ART index created
    email VARCHAR UNIQUE     -- ART index created
);

-- Explicit index creation
CREATE INDEX idx_name ON users(name);
```

### ART Characteristics

| Property | Description |
|----------|-------------|
| Use case | Point lookups, highly selective queries (< 0.1%) |
| Memory | Must fit in memory during creation |
| Maintenance | Overhead on inserts/updates/deletes |
| Not for | Joins, aggregations, sorting |

### Min-Max Indexes (Zonemaps)

DuckDB maintains min/max statistics per row group:

- Enables row group pruning
- No explicit creation needed
- Automatically maintained

```sql
-- This query can skip row groups where max(age) < 21
SELECT * FROM users WHERE age > 21;
```

## Storage Versions

| DuckDB Version | Storage Version | Notes |
|----------------|-----------------|-------|
| v1.4.x | 67 | Current |
| v1.0.x - v1.3.x | 64-66 | |
| v0.10.x | 64 | Backward compatibility baseline |

### Compatibility

- **Backward compatible**: Newer DuckDB reads older files (since v0.10)
- **Not forward compatible**: Older DuckDB cannot read newer files

### Migration

```sql
-- Export from old version
EXPORT DATABASE '/path/to/export';

-- Import into new version
IMPORT DATABASE '/path/to/export';
```

## Best Practices

### File Sizing

- Single file simplifies deployment and backup
- Consider splitting very large databases for manageability

### Row Group Sizing

- Default (122,880) works well for most cases
- Smaller groups: Better for highly selective queries
- Larger groups: Better compression, less metadata overhead

### Memory Configuration

- Set `memory_limit` to 50-75% of available RAM
- Configure `temp_directory` for out-of-core support
- Monitor actual memory usage (can exceed limit)
