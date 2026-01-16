# DuckDB Types

> Data type system including nested and composite types

## Overview

DuckDB provides a rich type system supporting primitive types, temporal types, and arbitrarily nested composite types. Types are resolved during the binding phase and enforced throughout query execution.

## Primitive Types

### Numeric Types

| Type | Size | Range | Notes |
|------|------|-------|-------|
| `TINYINT` | 1 byte | -128 to 127 | |
| `SMALLINT` | 2 bytes | -32,768 to 32,767 | |
| `INTEGER` / `INT` | 4 bytes | -2.1B to 2.1B | |
| `BIGINT` | 8 bytes | -9.2E18 to 9.2E18 | |
| `HUGEINT` | 16 bytes | ±1.7E38 | |
| `UTINYINT` | 1 byte | 0 to 255 | Unsigned |
| `USMALLINT` | 2 bytes | 0 to 65,535 | Unsigned |
| `UINTEGER` | 4 bytes | 0 to 4.3B | Unsigned |
| `UBIGINT` | 8 bytes | 0 to 1.8E19 | Unsigned |
| `UHUGEINT` | 16 bytes | 0 to 3.4E38 | Unsigned |

### Floating Point

| Type | Size | Precision |
|------|------|-----------|
| `FLOAT` / `REAL` | 4 bytes | ~7 decimal digits |
| `DOUBLE` | 8 bytes | ~15 decimal digits |

### Fixed-Point Decimal

```sql
DECIMAL(precision, scale)
-- precision: total digits (1-38)
-- scale: digits after decimal point
```

| Example | Storage |
|---------|---------|
| `DECIMAL(5,2)` | 123.45 |
| `DECIMAL(18,0)` | Large integer |
| `DECIMAL(38,10)` | Maximum precision |

### Boolean

| Type | Values |
|------|--------|
| `BOOLEAN` / `BOOL` | `TRUE`, `FALSE`, `NULL` |

### String Types

| Type | Description |
|------|-------------|
| `VARCHAR` / `TEXT` | Variable-length string (no limit) |
| `VARCHAR(n)` | Variable-length with max length |
| `CHAR(n)` | Fixed-length, space-padded |

### Binary

| Type | Description |
|------|-------------|
| `BLOB` | Binary large object |
| `BYTEA` | Alias for BLOB |

### String Internals

DuckDB uses an optimized string representation:

```c
typedef struct {
    union {
        struct {  // Long strings (> 12 chars)
            uint32_t length;
            char prefix[4];  // First 4 chars for comparison
            char *ptr;       // Pointer to full string
        } pointer;
        struct {  // Short strings (≤ 12 chars)
            uint32_t length;
            char inlined[12];
        } inlined;
    } value;
} duckdb_string_t;
```

Benefits:
- Short strings avoid heap allocation
- Prefix enables fast comparisons without dereferencing

## Temporal Types

| Type | Description | Example |
|------|-------------|---------|
| `DATE` | Calendar date | `2025-01-15` |
| `TIME` | Time of day | `14:30:00` |
| `TIMESTAMP` | Date + time | `2025-01-15 14:30:00` |
| `TIMESTAMP WITH TIME ZONE` | With timezone | `2025-01-15 14:30:00+00` |
| `INTERVAL` | Time duration | `INTERVAL '2 days'` |

### Timestamp Precision

| Type | Precision |
|------|-----------|
| `TIMESTAMP_S` | Seconds |
| `TIMESTAMP_MS` | Milliseconds |
| `TIMESTAMP` | Microseconds (default) |
| `TIMESTAMP_NS` | Nanoseconds |

## Nested Types

DuckDB supports five nested/composite types that can be arbitrarily combined.

### LIST

Ordered, variable-length sequence of same-type values:

```sql
-- Literal
SELECT [1, 2, 3];

-- DDL
CREATE TABLE t (numbers INTEGER[]);

-- Access
SELECT numbers[1] FROM t;  -- 1-indexed

-- Functions
SELECT list_sum([1, 2, 3]);  -- 6
SELECT list_filter([1, 2, 3, 4], x -> x > 2);  -- [3, 4]
```

### ARRAY

Fixed-length sequence (all rows have same length):

```sql
-- DDL (3 elements per row)
CREATE TABLE t (coords FLOAT[3]);

-- Operations same as LIST
SELECT coords[1] FROM t;
```

| Property | LIST | ARRAY |
|----------|------|-------|
| Length | Variable per row | Fixed for all rows |
| Storage | More flexible | More efficient |

### STRUCT

Dictionary with named fields of potentially different types:

```sql
-- Literal
SELECT {'name': 'Alice', 'age': 30};

-- DDL
CREATE TABLE t (
    person STRUCT(name VARCHAR, age INTEGER)
);

-- Access by name
SELECT person.name FROM t;
SELECT person['name'] FROM t;

-- Field names are case-insensitive
```

### MAP

Dictionary with uniform key and value types:

```sql
-- Literal
SELECT MAP(['a', 'b'], [1, 2]);

-- From entries
SELECT MAP_FROM_ENTRIES([('a', 1), ('b', 2)]);

-- Access
SELECT my_map['a'];

-- Functions
SELECT map_keys(my_map);
SELECT map_values(my_map);
```

| Property | STRUCT | MAP |
|----------|--------|-----|
| Keys | Fixed at DDL time | Dynamic per row |
| Key type | Always string | Any type |
| Value types | Different per field | Same for all entries |

### UNION

Tagged union of multiple types:

```sql
-- DDL
CREATE TABLE t (
    value UNION(num INTEGER, str VARCHAR, flag BOOLEAN)
);

-- Insert different types
INSERT INTO t VALUES (1);
INSERT INTO t VALUES ('hello');
INSERT INTO t VALUES (true);

-- Access with union_tag and union_extract
SELECT union_tag(value) FROM t;  -- 'num', 'str', 'flag'
SELECT union_extract(value, 'num') FROM t;
```

## Arbitrary Nesting

Nested types can be combined to any depth:

```sql
-- STRUCT containing LISTs
SELECT {
    'birds': ['duck', 'goose', 'heron'],
    'mammals': ['dog', 'cat']
};

-- LIST of STRUCTs
SELECT [
    {'name': 'Alice', 'score': 95},
    {'name': 'Bob', 'score': 87}
];

-- STRUCT with nested MAP
SELECT {
    'metadata': MAP(['created', 'modified'], [DATE '2025-01-01', DATE '2025-01-15'])
};

-- LIST of MAPs
SELECT [MAP([1], ['a']), MAP([2], ['b'])];
```

## Type Coercion

DuckDB performs implicit type coercion when safe:

### Numeric Promotion

```
TINYINT → SMALLINT → INTEGER → BIGINT → HUGEINT
                  ↘         ↘
                  FLOAT → DOUBLE
```

### String Coercion

Most types can be implicitly converted to/from strings:

```sql
SELECT '42'::INTEGER;  -- Explicit
SELECT 42 = '42';      -- Implicit (compares as strings)
```

### COALESCE Type Resolution

Returns the most general type among arguments:

```sql
SELECT COALESCE(1, 2.5);  -- Returns DOUBLE
```

## Type Functions

### Inspection

```sql
SELECT typeof(42);           -- INTEGER
SELECT typeof([1, 2, 3]);    -- INTEGER[]
SELECT typeof({'a': 1});     -- STRUCT(a INTEGER)
```

### Casting

```sql
-- Explicit cast
SELECT CAST('42' AS INTEGER);
SELECT '42'::INTEGER;

-- TRY_CAST returns NULL on failure
SELECT TRY_CAST('not a number' AS INTEGER);  -- NULL
```

## NULL Handling

### NULL Semantics

- NULL represents unknown/missing value
- NULL comparisons return NULL (not TRUE/FALSE)
- Use `IS NULL` / `IS NOT NULL` for NULL checks

### NULL in Nested Types

```sql
-- NULL list vs empty list
SELECT NULL::INTEGER[];  -- NULL
SELECT [];               -- Empty list (not NULL)

-- NULL element in list
SELECT [1, NULL, 3];     -- List with NULL element

-- NULL struct field
SELECT {'a': NULL, 'b': 1};
```

## Best Practices

### Choosing Types

| Scenario | Recommended Type |
|----------|------------------|
| Known set of fields | STRUCT |
| Dynamic key-value pairs | MAP |
| Ordered collection | LIST |
| Fixed-size collection | ARRAY |
| One of several types | UNION |
| Exact decimals | DECIMAL |
| Large integers | HUGEINT |

### Performance Considerations

1. Use smallest sufficient integer type
2. Prefer ARRAY over LIST when length is fixed
3. STRUCT field access is faster than MAP lookup
4. Avoid deep nesting when possible
