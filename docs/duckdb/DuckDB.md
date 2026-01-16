# DuckDB Internals

> Reference documentation for DuckDB's internal architecture

## Overview

**DuckDB** is an in-process SQL OLAP database management system designed for analytical workloads. Unlike traditional client-server databases, DuckDB runs embedded within the host application, similar to SQLite but optimized for analytics rather than transactions.

## Architecture Pillars

DuckDB's performance comes from three synergistic design choices:

```
┌─────────────────────────────────────────────────────────┐
│                  DuckDB Architecture                     │
├─────────────────┬─────────────────┬─────────────────────┤
│   Columnar      │   Vectorized    │   Morsel-Driven     │
│   Storage       │   Execution     │   Parallelism       │
├─────────────────┼─────────────────┼─────────────────────┤
│ Data stored by  │ Process 2048    │ Parallel execution  │
│ column, not row │ tuples at once  │ over row groups     │
└─────────────────┴─────────────────┴─────────────────────┘
```

| Pillar | Benefit |
|--------|---------|
| **Columnar Storage** | Minimizes I/O for analytical queries (read only needed columns) |
| **Vectorized Execution** | Maximizes CPU cache efficiency and enables SIMD |
| **Morsel-Driven Parallelism** | Scales across cores with minimal coordination overhead |

## Core Concepts

### In-Process Execution

DuckDB runs in the same process as the application:

```
┌────────────────────────────────────┐
│         Host Application           │
│  ┌──────────────────────────────┐  │
│  │          DuckDB              │  │
│  │  ┌────────┐  ┌────────────┐  │  │
│  │  │ Parser │  │  Executor  │  │  │
│  │  ├────────┤  ├────────────┤  │  │
│  │  │ Binder │  │  Storage   │  │  │
│  │  └────────┘  └────────────┘  │  │
│  └──────────────────────────────┘  │
└────────────────────────────────────┘
```

**Benefits**: No IPC overhead, direct memory access, simple deployment (single file or in-memory).

### Single-File Database

A DuckDB database resides in a single file (`.duckdb` extension) or entirely in memory:

- **Block size**: 256KB (optimized for SSD/HDD sequential reads)
- **Row groups**: ~122,880 rows per group (multiple of vector size)
- **Compression**: Lightweight compression enabled by default

## Documentation Structure

### Query Processing

| Document | Description |
|----------|-------------|
| [Query Pipeline](QueryPipeline.md) | Parser → Binder → Planner → Optimizer → Executor |
| [Optimizer](Optimizer.md) | Optimization strategies and techniques |

### Execution

| Document | Description |
|----------|-------------|
| [Execution](Execution.md) | Vectorized push-based execution model |
| [Types](Types.md) | Data type system including nested types |

### Storage & Memory

| Document | Description |
|----------|-------------|
| [Storage](Storage.md) | File format, row groups, compression |
| [Concurrency](Concurrency.md) | MVCC, transactions, single-writer model |

### Extensibility

| Document | Description |
|----------|-------------|
| [Extensions](Extensions.md) | Extension system and development |
| [UDFs](UDFs.md) | User-defined functions and macros (C#, Rust, SQL) |

### Performance

| Document | Description |
|----------|-------------|
| [Performance](Performance.md) | Best practices for query, loading, memory, and parallelism tuning |

## Quick Reference

### Query Pipeline Stages

```
SQL String
    │
    ▼
┌─────────┐   SQLStatement, QueryNode,
│ Parser  │   TableRef, ParsedExpression
└────┬────┘
     │
     ▼
┌─────────┐   BoundStatement, Expression
│ Binder  │   (types resolved via catalog)
└────┬────┘
     │
     ▼
┌─────────┐   LogicalOperator tree
│ Planner │
└────┬────┘
     │
     ▼
┌───────────┐  Optimized LogicalOperator tree
│ Optimizer │
└─────┬─────┘
      │
      ▼
┌───────────┐  PhysicalOperator tree
│ Physical  │
│ Planner   │
└─────┬─────┘
      │
      ▼
┌───────────┐  DataChunks (results)
│ Executor  │
└───────────┘
```

### Key Constants

| Constant | Value | Purpose |
|----------|-------|---------|
| `STANDARD_VECTOR_SIZE` | 2048 | Tuples per vector |
| Block size | 256KB | Fundamental I/O unit |
| Row group size | ~122,880 | Rows per horizontal partition |

### Storage Versions

| DuckDB Version | Storage Version |
|----------------|-----------------|
| v1.4.x | 67 |
| v1.0.x | 64 |
| v0.10.x | 64 |

Backward compatibility maintained since v0.10.

## Design Philosophy

### OLAP vs OLTP

DuckDB is optimized for **analytical** workloads:

| Characteristic | OLTP (SQLite) | OLAP (DuckDB) |
|----------------|---------------|---------------|
| Query pattern | Many small transactions | Few large scans |
| Data access | Row-oriented | Column-oriented |
| Optimization | Write latency | Read throughput |
| Concurrency | Many writers | Single writer, many readers |

### Not Just In-Memory

Despite being embedded, DuckDB handles datasets larger than RAM:

- **Buffer manager** manages memory with configurable limits
- **Out-of-core** processing spills to disk when needed
- **Compression** reduces memory footprint

## External Resources

- [Official Documentation](https://duckdb.org/docs/)
- [DuckDB Internals Overview](https://duckdb.org/docs/stable/internals/overview)
- [GitHub Repository](https://github.com/duckdb/duckdb)
- [Research Papers](https://duckdb.org/docs/stable/internals/overview#papers)
