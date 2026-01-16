# DuckDB Extensions

> Modular functionality through loadable extensions

## Overview

DuckDB's extension system enables modular functionality enhancement through downloadable modules. Extensions can add:

- New data types
- New functions
- New table functions (data sources)
- New file format support
- New network protocols

## Extension Lifecycle

```
┌─────────────┐      ┌─────────────┐      ┌─────────────┐
│  Available  │ ───▶ │  Installed  │ ───▶ │   Loaded    │
│             │      │  (on disk)  │      │ (in memory) │
└─────────────┘      └─────────────┘      └─────────────┘
     INSTALL              LOAD
```

### Installation

Downloads extension binary to local storage:

```sql
INSTALL httpfs;
INSTALL spatial;
INSTALL h3 FROM community;
```

### Loading

Dynamically links extension into running DuckDB:

```sql
LOAD httpfs;
LOAD spatial;
```

### Combined

```sql
INSTALL AND LOAD httpfs;
```

## Extension Types

### Built-in Extensions

Statically compiled into DuckDB binary. Available immediately without installation:

```sql
-- json is built-in in most distributions
SELECT * FROM read_json('data.json');
```

### Core Extensions

Maintained by DuckDB team, distributed via official repository:

| Extension | Purpose |
|-----------|---------|
| `httpfs` | HTTP/S3 file access |
| `parquet` | Parquet file support |
| `json` | JSON file support |
| `postgres` | PostgreSQL connector |
| `sqlite` | SQLite connector |
| `spatial` | Geospatial functions |
| `fts` | Full-text search |
| `icu` | Unicode collation |
| `tpch` | TPC-H benchmark data |
| `tpcds` | TPC-DS benchmark data |

### Community Extensions

Third-party extensions from community repository:

```sql
-- Install from community repository
INSTALL h3 FROM community;
INSTALL avro FROM community;
```

Community extensions are:
- Built and signed centrally
- Tested on major platforms
- Reviewed for security

## Autoloading

Many core extensions load automatically when needed:

```sql
-- httpfs autoloads for HTTPS URLs
SELECT * FROM 'https://example.com/data.csv';

-- parquet autoloads for .parquet files
SELECT * FROM 'data.parquet';

-- json autoloads for JSON functions
SELECT json_extract('{"a":1}', '$.a');
```

### Autoloading Requirements

Not all extensions support autoloading. Reasons include:
- Extensions that modify global state
- Extensions requiring explicit opt-in
- Technical limitations

### Checking Autoload Support

```sql
SELECT extension_name, installed, loaded, autoload
FROM duckdb_extensions()
WHERE autoload = true;
```

## Extension Management

### List Extensions

```sql
-- All extensions with status
SELECT * FROM duckdb_extensions();

-- Installed extensions only
SELECT * FROM duckdb_extensions() WHERE installed;
```

### Update Extensions

```sql
-- Update all installed extensions
UPDATE EXTENSIONS;

-- Update specific extension
UPDATE EXTENSIONS (httpfs);
```

### Extension Information

```sql
SELECT
    extension_name,
    installed,
    loaded,
    install_path,
    description
FROM duckdb_extensions()
WHERE extension_name = 'httpfs';
```

## Common Extensions

### httpfs - Remote File Access

```sql
INSTALL httpfs;
LOAD httpfs;

-- HTTP/HTTPS
SELECT * FROM 'https://example.com/data.parquet';

-- S3
SET s3_access_key_id = 'your-key';
SET s3_secret_access_key = 'your-secret';
SELECT * FROM 's3://bucket/path/data.parquet';

-- GCS
SELECT * FROM 'gs://bucket/path/data.parquet';
```

### parquet - Parquet Support

```sql
-- Usually autoloads
SELECT * FROM 'data.parquet';

-- With options
SELECT * FROM read_parquet('data.parquet', hive_partitioning = true);

-- Write
COPY (SELECT * FROM tbl) TO 'output.parquet' (FORMAT PARQUET);
```

### json - JSON Support

```sql
-- Read JSON files
SELECT * FROM read_json('data.json');

-- JSON functions
SELECT json_extract(data, '$.field') FROM tbl;
SELECT data->>'$.field' FROM tbl;

-- Read as table
SELECT * FROM read_json_auto('data.json');
```

### spatial - Geospatial

```sql
INSTALL spatial;
LOAD spatial;

-- Create geometry
SELECT ST_Point(0, 0);
SELECT ST_GeomFromText('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))');

-- Spatial operations
SELECT ST_Distance(geom1, geom2) FROM locations;
SELECT ST_Contains(boundary, point) FROM regions, points;
```

### postgres_scanner - PostgreSQL

```sql
INSTALL postgres;
LOAD postgres;

-- Attach PostgreSQL database
ATTACH 'postgresql://user:pass@host:5432/db' AS pg (TYPE POSTGRES);

-- Query PostgreSQL tables
SELECT * FROM pg.public.users;
```

### sqlite_scanner - SQLite

```sql
INSTALL sqlite;
LOAD sqlite;

-- Attach SQLite database
ATTACH 'mydb.sqlite' AS sqlite_db (TYPE SQLITE);

-- Query SQLite tables
SELECT * FROM sqlite_db.main.users;
```

## Extension Configuration

### Repository Settings

```sql
-- Set custom extension repository
SET extension_directory = '/path/to/extensions';

-- Allow unsigned extensions (security risk)
SET allow_unsigned_extensions = true;
```

### Platform Support

Extensions are built for:
- macOS (x64, ARM64)
- Windows (x64)
- Linux (x64, ARM64)

Check platform availability in extension documentation.

## Creating Extensions

### Extension Template

DuckDB provides an extension template repository:

```bash
git clone https://github.com/duckdb/extension-template
cd extension-template
```

### Extension Structure

```
my_extension/
├── CMakeLists.txt
├── src/
│   ├── my_extension.cpp      # Main entry point
│   └── functions/            # Function implementations
├── test/
│   └── sql/                  # SQL tests
└── vcpkg.json               # Dependencies
```

### Basic Extension Code

```cpp
#define DUCKDB_EXTENSION_MAIN

#include "duckdb.hpp"
#include "duckdb/main/extension_util.hpp"

namespace duckdb {

// Scalar function implementation
static void MyFunction(DataChunk &args, ExpressionState &state, Vector &result) {
    // Implementation
}

// Extension load
void MyExtension::Load(DuckDB &db) {
    auto &instance = *db.instance;

    // Register function
    ExtensionUtil::RegisterFunction(
        instance,
        ScalarFunction("my_function", {LogicalType::VARCHAR}, LogicalType::INTEGER, MyFunction)
    );
}

} // namespace duckdb

extern "C" {
DUCKDB_EXTENSION_API void my_extension_init(duckdb::DatabaseInstance &db) {
    duckdb::DuckDB db_wrapper(db);
    db_wrapper.LoadExtension<duckdb::MyExtension>();
}
DUCKDB_EXTENSION_API const char *my_extension_version() {
    return "0.0.1";
}
}
```

## Best Practices

### Extension Selection

1. Prefer core extensions over community when available
2. Check platform support before relying on extension
3. Pin extension versions in production

### Security

1. Only install extensions from trusted sources
2. Never enable `allow_unsigned_extensions` in production
3. Review community extension source code

### Performance

1. Load extensions only when needed
2. Autoloading adds slight overhead on first use
3. Some extensions add startup time
