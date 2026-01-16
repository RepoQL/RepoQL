# DuckDB UDFs and Macros

> Deep dive into user-defined functions and macros: architecture, C#, Rust, and SQL

## Overview

DuckDB provides three extension mechanisms for custom logic:

| Mechanism | Definition | Execution | Use Case |
|-----------|------------|-----------|----------|
| **UDF** | Native code (C/C++/Rust/C#) | Vectorized in engine | Performance-critical |
| **Macro** | SQL expression | Inlined at parse time | Convenience/defaults |
| **Extension** | Loadable module | Dynamic linking | Major features |

## Architecture

### The C API Foundation

All UDFs ultimately register through DuckDB's C API. The core abstraction is the **scalar function**:

```
┌─────────────────────────────────────────────────────────────┐
│                    DuckDB Engine                             │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐   │
│  │   Function   │    │   Function   │    │   Function   │   │
│  │   Catalog    │───▶│   Binder     │───▶│   Executor   │   │
│  └──────────────┘    └──────────────┘    └──────────────┘   │
│         ▲                                       │            │
│         │                                       ▼            │
│  ┌──────────────┐                       ┌──────────────┐    │
│  │  Register    │                       │  Vectorized  │    │
│  │  Function    │                       │  Callback    │    │
│  └──────────────┘                       └──────────────┘    │
│         ▲                                       │            │
└─────────│───────────────────────────────────────│────────────┘
          │                                       │
          │                                       ▼
    ┌─────────────┐                       ┌──────────────┐
    │ C API       │                       │  DataChunk   │
    │ Registration│                       │  (2048 rows) │
    └─────────────┘                       └──────────────┘
```

### Vectorized Execution Contract

UDFs receive and produce **vectors**, not individual values:

```cpp
// C API signature (simplified)
typedef void (*scalar_function_t)(
    DataChunk &args,      // Input vectors (up to 2048 rows each)
    ExpressionState &state,
    Vector &result        // Output vector
);
```

Key properties:
- **Batch processing**: One function call handles up to `STANDARD_VECTOR_SIZE` (2048) rows
- **Columnar data**: Each argument is a `Vector` of values
- **NULL handling**: Validity masks track NULLs per-value
- **Type safety**: Types resolved at bind time, enforced at runtime

### Function Categories

| Category | Input | Output | Example |
|----------|-------|--------|---------|
| **Scalar** | N values → N values | Single column | `upper('hello')` → `'HELLO'` |
| **Aggregate** | N values → 1 value | Single value | `sum([1,2,3])` → `6` |
| **Table** | Parameters → rows | Multiple columns | `read_csv('file.csv')` |

## C# Implementation (DuckDB.NET)

### Registration API

DuckDB.NET wraps the C API with generic methods:

```csharp
// 1-parameter function
connection.RegisterScalarFunction<TArg1, TResult>(
    "function_name",
    (readers, writer, rowCount) => {
        for (ulong i = 0; i < rowCount; i++) {
            var arg1 = readers[0].GetValue<TArg1>(i);
            var result = ComputeResult(arg1);
            writer.WriteValue(result, i);
        }
    },
    isPureFunction: true  // Enables optimization
);

// 2-parameter function
connection.RegisterScalarFunction<TArg1, TArg2, TResult>(
    "function_name",
    (readers, writer, rowCount) => { ... }
);

// Up to 4 direct parameters supported
```

### Readers and Writers

The vectorized interface uses typed accessors:

```csharp
// Reading input vectors
string value = readers[0].GetValue<string>(i);      // VARCHAR
long number = readers[0].GetValue<long>(i);         // BIGINT
bool flag = readers[0].GetValue<bool>(i);           // BOOLEAN

// Checking for NULL
if (readers[0].IsNull(i)) {
    writer.WriteNull(i);
    continue;
}

// Writing output vector
writer.WriteValue("result", i);    // VARCHAR
writer.WriteValue(42L, i);         // BIGINT
writer.WriteNull(i);               // NULL
```

### Type Mapping

| DuckDB Type | C# Type | Notes |
|-------------|---------|-------|
| `VARCHAR` | `string` | Primary interchange type |
| `BIGINT` | `long` | 64-bit signed |
| `INTEGER` | `int` | 32-bit signed |
| `DOUBLE` | `double` | 64-bit float |
| `BOOLEAN` | `bool` | |
| `BLOB` | `byte[]` | Binary data |

### Pure Functions

Marking a function as pure enables optimizations:

```csharp
connection.RegisterScalarFunction<string, string>(
    "my_func",
    (readers, writer, n) => { ... },
    isPureFunction: true  // ← Key flag
);
```

Pure function guarantees:
- Same inputs always produce same output
- No side effects
- Enables constant folding: `my_func('literal')` evaluated once at plan time

### RepoQL's UDF Framework

RepoQL builds a higher-level framework on DuckDB.NET:

#### Attribute-Based Discovery

```csharp
[UdfClass]
public class MyUdfs
{
    [ScalarUdf("_internal_name", MacroName = "public_name", IsPure = true,
        Description = "Human-readable description")]
    public string MyFunction(string arg1, [UdfDefault("default")] string arg2)
    {
        return $"{arg1}-{arg2}";
    }
}
```

#### Registration Flow

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│  Assembly Scan  │────▶│  Reflection     │────▶│  DuckDB.NET     │
│  [UdfClass]     │     │  Method Info    │     │  Registration   │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                               │
                               ▼
                        ┌─────────────────┐
                        │  SQL Macro      │
                        │  Generation     │
                        └─────────────────┘
```

The `UdfRegistry` class:
1. Scans assemblies for `[UdfClass]` types
2. Finds methods with `[ScalarUdf]` or `[StructuredUdf]`
3. Generates appropriate `RegisterScalarFunction<>` calls
4. Creates SQL macros for default parameters

#### All-VARCHAR Strategy

RepoQL uses strings for all parameters at the DuckDB level:

```csharp
// DuckDB sees: VARCHAR, VARCHAR, VARCHAR → VARCHAR
conn.RegisterScalarFunction<string, string, string>(
    "_internal_func",
    (readers, writer, n) => {
        for (ulong i = 0; i < n; i++) {
            // Read as strings
            string arg1 = readers[0].GetValue<string>(i);
            string arg2 = readers[1].GetValue<string>(i);

            // Convert to C# types as needed
            int parsed = int.Parse(arg2);

            // Compute and write result
            writer.WriteValue(result, i);
        }
    }
);
```

Benefits:
- Simpler registration (one type signature)
- JSON support (complex objects as strings)
- Flexible parsing in C#

#### Structured (Table-Valued) UDFs

For functions returning multiple rows, RepoQL uses JSON serialization:

```csharp
[StructuredUdf("_search_internal", MacroName = "search")]
public IEnumerable<SearchResult> Search(string query, [UdfDefault("10")] string k)
{
    yield return new SearchResult { Uri = "file:///a.cs", Score = 0.95 };
    yield return new SearchResult { Uri = "file:///b.cs", Score = 0.87 };
}
```

The framework:
1. Calls the C# method
2. Serializes `IEnumerable<T>` to JSON array
3. Returns JSON string to DuckDB
4. SQL macro expands via `json_each()`

```sql
-- Generated macro
CREATE MACRO search(query, k := 10) AS TABLE (
    SELECT * FROM json_each(_search_internal(query, k::VARCHAR))
);
```

## Rust Implementation (duckdb-rs)

### Feature Flags

The `duckdb` crate provides UDF support via feature flags:

```toml
[dependencies]
duckdb = { version = "1.0", features = ["vscalar", "vtab"] }
```

| Feature | Purpose |
|---------|---------|
| `vscalar` | Scalar function support |
| `vtab` | Table function (virtual table) support |
| `loadable-extension` | Build as `.duckdb_extension` |

### Scalar Functions

```rust
use duckdb::{Connection, Result};
use duckdb::functions::FunctionSpec;

fn main() -> Result<()> {
    let conn = Connection::open_in_memory()?;

    // Register scalar function
    conn.create_scalar_function(
        "my_add",
        2,  // argument count
        true,  // deterministic (pure)
        |ctx| {
            let a: i64 = ctx.get(0)?;
            let b: i64 = ctx.get(1)?;
            Ok(a + b)
        },
    )?;

    // Use it
    let result: i64 = conn.query_row(
        "SELECT my_add(10, 32)",
        [],
        |row| row.get(0)
    )?;

    Ok(())
}
```

### Vectorized Functions

For performance-critical code, use the vectorized API:

```rust
use duckdb::vtab::{VScalar, VScalarInit};

struct MyScalar;

impl VScalarInit for MyScalar {
    type State = ();

    fn init(_: &InitInfo) -> Self::State {
        ()
    }
}

impl VScalar for MyScalar {
    type State = ();

    fn invoke(
        _state: &Self::State,
        input: &mut DataChunk,
        output: &mut Vector,
    ) -> Result<()> {
        let arg0 = input.get_vector(0);

        for i in 0..input.len() {
            if arg0.is_null(i) {
                output.set_null(i, true);
            } else {
                let val: i64 = arg0.get(i);
                output.set(i, val * 2);
            }
        }

        Ok(())
    }
}
```

### Table Functions (Virtual Tables)

```rust
use duckdb::vtab::{VTab, VTabInit, VTabBind};

struct MyTable;
struct MyTableState {
    current_row: usize,
    max_rows: usize,
}

impl VTabInit for MyTable {
    type BindData = usize;  // max rows
    type State = MyTableState;

    fn bind(bind: &BindInfo) -> Result<Self::BindData> {
        // Read parameters, return bind data
        let max = bind.get_parameter(0).get_i64()? as usize;

        // Declare output columns
        bind.add_result_column("id", LogicalType::BigInt);
        bind.add_result_column("value", LogicalType::Varchar);

        Ok(max)
    }

    fn init(init: &InitInfo) -> Result<Self::State> {
        Ok(MyTableState {
            current_row: 0,
            max_rows: *init.get_bind_data(),
        })
    }
}

impl VTab for MyTable {
    fn next(state: &mut Self::State, output: &mut DataChunk) -> Result<()> {
        let mut count = 0;

        while state.current_row < state.max_rows && count < STANDARD_VECTOR_SIZE {
            output.set_value(0, count, state.current_row as i64);
            output.set_value(1, count, format!("row_{}", state.current_row));
            state.current_row += 1;
            count += 1;
        }

        output.set_len(count);
        Ok(())
    }
}
```

### Loadable Extensions

Build standalone `.duckdb_extension` files:

```rust
// lib.rs
use duckdb::ffi;
use duckdb_loadable_macros::duckdb_entrypoint;

#[duckdb_entrypoint]
pub unsafe fn my_extension_init(conn: &Connection) -> Result<()> {
    conn.create_scalar_function("my_func", ...)?;
    Ok(())
}
```

```toml
# Cargo.toml
[lib]
crate-type = ["cdylib"]

[dependencies]
duckdb = { features = ["loadable-extension"] }
duckdb-loadable-macros = "0.1"
```

Build and load:
```bash
cargo build --release
# Produces: target/release/libmy_extension.so

# In DuckDB:
LOAD 'path/to/libmy_extension.so';
SELECT my_func('test');
```

## SQL Macros

### Scalar Macros

Simple expression substitution:

```sql
-- Definition
CREATE MACRO add_one(x) AS x + 1;

-- Usage (expanded at parse time)
SELECT add_one(col) FROM t;
-- Becomes: SELECT col + 1 FROM t;
```

### Table Macros

Return tabular results:

```sql
-- Definition
CREATE MACRO recent_files(n) AS TABLE (
    SELECT uri, modified_at
    FROM Files
    ORDER BY modified_at DESC
    LIMIT n
);

-- Usage
SELECT * FROM recent_files(10);
```

### Default Parameters

```sql
CREATE MACRO search(query, k := 10, scope := NULL) AS TABLE (
    SELECT * FROM _search_internal(query, k, scope)
);

-- All equivalent:
SELECT * FROM search('auth');
SELECT * FROM search('auth', 10);
SELECT * FROM search('auth', k := 5);
SELECT * FROM search(query := 'auth', scope := 'file:///src/%');
```

### Macros vs UDFs

| Aspect | Macro | UDF |
|--------|-------|-----|
| Evaluation | Parse time (inlined) | Runtime |
| Performance | Zero overhead | Function call overhead |
| Capabilities | SQL expressions only | Any computation |
| Side effects | Cannot have | Can have (if not pure) |
| Debugging | Visible in EXPLAIN | Opaque box |

### RepoQL Pattern: Macro + Internal UDF

RepoQL combines both for best of both worlds:

```sql
-- Internal UDF (C# implementation)
-- Registered as: _snippet_internal(uri VARCHAR, context INTEGER) → VARCHAR

-- Public macro (SQL convenience layer)
CREATE MACRO snippet(u, context_lines := 3) AS TABLE (
    SELECT * FROM json_each(_snippet_internal(u, context_lines::VARCHAR))
);
```

Benefits:
- **Macro**: Named parameters, defaults, type coercion
- **UDF**: Complex logic in C#, access to services

## Performance Considerations

### Vectorization Impact

| Approach | Rows/sec | Notes |
|----------|----------|-------|
| Row-at-a-time UDF | ~100K | Function call per row |
| Vectorized UDF | ~10M+ | Amortized overhead |
| SQL Macro | ~50M+ | No overhead (inlined) |

### Guidelines

1. **Prefer macros** for simple transformations
2. **Use pure functions** when possible (enables constant folding)
3. **Batch I/O** in UDFs (don't make network calls per-row)
4. **Avoid allocations** in hot loops (reuse buffers)
5. **Handle NULLs explicitly** (check validity mask first)

### Anti-Patterns

```csharp
// BAD: Per-row allocation
for (ulong i = 0; i < n; i++) {
    var list = new List<string>();  // Allocates each iteration
    // ...
}

// GOOD: Reuse outside loop
var list = new List<string>();
for (ulong i = 0; i < n; i++) {
    list.Clear();
    // ...
}
```

```csharp
// BAD: Per-row I/O
for (ulong i = 0; i < n; i++) {
    var data = FetchFromNetwork(readers[0].GetValue<string>(i));
    // ...
}

// GOOD: Batch fetch
var ids = new List<string>();
for (ulong i = 0; i < n; i++) {
    ids.Add(readers[0].GetValue<string>(i));
}
var data = BatchFetchFromNetwork(ids);  // Single call
for (ulong i = 0; i < n; i++) {
    writer.WriteValue(data[(int)i], i);
}
```

## Summary

| Language | Registration | Vectorized | Table Functions |
|----------|--------------|------------|-----------------|
| C/C++ | Native API | Full control | `TableFunction` |
| C# | `RegisterScalarFunction<>` | Via readers/writers | JSON + macro |
| Rust | `create_scalar_function` | `VScalar` trait | `VTab` trait |
| SQL | `CREATE MACRO` | N/A (inlined) | `AS TABLE` |

The common thread: all paths lead to vectorized execution on `DataChunk`s of 2048 rows, maximizing CPU efficiency through batch processing.
