# RepoQL Views Reference

Primary views cover 90% of queries. Use these before touching base tables.

## Files

Documents with diagnostics and x-ray summaries.

| Column | Type | Description |
|--------|------|-------------|
| uri | VARCHAR | File URI (file:///path or github://...) |
| lang | VARCHAR | Language (code.csharp, code.python, etc.) |
| lines | INTEGER | Line count |
| error_count | INTEGER | Number of errors |
| warning_count | INTEGER | Number of warnings |
| headline | VARCHAR | One-line summary |
| summary | VARCHAR | Multi-line summary |
| structure | VARCHAR | Structural outline (signatures) |
| artifact_id | INTEGER | FK to artifact table |

### Examples

```sql
-- Files with errors
SELECT uri, error_count FROM Files WHERE error_count > 0;

-- Language distribution
SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang;

-- Largest files
SELECT uri, lines FROM Files ORDER BY lines DESC LIMIT 10;

-- Search within files
SELECT uri, headline FROM Files WHERE headline LIKE '%auth%';
```

## Functions

Methods, constructors, and callables across languages.

| Column | Type | Description |
|--------|------|-------------|
| name | VARCHAR | Function name |
| qualified_name | VARCHAR | Full qualified name |
| declaring_type | VARCHAR | Parent class/type |
| signature | VARCHAR | Full signature |
| return_type | VARCHAR | Return type |
| is_async | BOOLEAN | Async method |
| is_static | BOOLEAN | Static method |
| start_line | INTEGER | Starting line number |
| file_uri | VARCHAR | File URI |

### Examples

```sql
-- Methods in a class
SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';

-- Async methods
SELECT file_uri, name FROM Functions WHERE is_async;

-- Find by pattern
SELECT name, file_uri FROM Functions WHERE name LIKE '%Validate%';
```

## Types

Classes, interfaces, structs, enums across languages.

| Column | Type | Description |
|--------|------|-------------|
| name | VARCHAR | Type name |
| qualified_name | VARCHAR | Full qualified name |
| type_kind | VARCHAR | class, interface, struct, enum |
| namespace | VARCHAR | Namespace/package |
| extends | VARCHAR | Base type |
| implements | VARCHAR | Implemented interfaces |
| start_line | INTEGER | Starting line number |
| file_uri | VARCHAR | File URI |

### Examples

```sql
-- Find implementations
SELECT name, file_uri FROM Types WHERE extends = 'BaseService';

-- All interfaces
SELECT name FROM Types WHERE type_kind = 'interface';

-- Types implementing interface
SELECT name FROM Types WHERE implements LIKE '%IDisposable%';
```

## Annotations

Errors, warnings, lint diagnostics.

| Column | Type | Description |
|--------|------|-------------|
| resolved_target_uri | VARCHAR | File URI with location |
| severity | VARCHAR | error, warning, info, hint |
| rule_id | VARCHAR | Diagnostic rule identifier |
| message | VARCHAR | Diagnostic message |
| start_line | INTEGER | Line number |

### Examples

```sql
-- All errors
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';

-- Most common issues
SELECT rule_id, COUNT(*) FROM Annotations GROUP BY rule_id ORDER BY 2 DESC;

-- Errors in specific file
SELECT message FROM Annotations WHERE resolved_target_uri LIKE 'file:///src/Auth%' AND severity = 'error';
```

## Format-Specific Views

Views prefixed by format name:

- `markdown_headings` - Heading structure
- `csharp_types` - C# specific type info
- `csharp_functions` - C# specific method info

Query `help:///repoql/tools/query/formats/*` for available views per format.
