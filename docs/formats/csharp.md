# C# Format Support

RepoQL provides first-class support for C# files through Roslyn-backed parsing, semantic analysis, and diagnostic reporting. This document describes the C# format capabilities, available data, and query patterns.

## Overview

The C# format handler (`RepoQL.Formats.DotNet`) processes `*.cs` files to produce:
- **Structural nodes**: namespaces, types, members, attributes, using directives
- **Semantic edges**: inheritance, implementation, symbol references
- **Diagnostics**: compiler errors/warnings, analyzer results, generator output
- **X-ray summaries**: instant visibility into file contents
- **Deterministic IDs**: reproducible node and span identifiers

## Semantic Media Type

C# files are identified by the semantic media type:
```
text/plain;kind=code.csharp;charset=utf-8
```

## Analysis Modes

The C# loader operates in two modes:

### Fast Mode (Syntax-only)
When no `.csproj` file can be located:
- Parses syntax tree
- Extracts structural information (namespaces, types, members)
- Generates deterministic spans
- **No semantic analysis** (no symbol resolution, no cross-file references, no diagnostics)

### Project Mode (Semantic)
When a `.csproj` file is found:
- Opens MSBuild workspace
- Runs source generators
- Executes analyzers (compiler + custom analyzers)
- Resolves symbols and cross-file references
- Emits `USES_SYMBOL` edges
- Produces full diagnostic output

## Node Kinds

The C# format emits the following `node.kind` values:

| Kind | Description | Key Properties |
|------|-------------|----------------|
| `csharp.namespace` | Namespace declaration | `qualified_name`, `name`, `parent_namespace_id` |
| `csharp.type` | Type declaration (class, interface, struct, enum, record, delegate) | `qualified_name`, `name`, `kind`, `namespace`, `accessibility`, `base_type`, `interfaces`, `is_partial`, `is_static`, `is_record`, `symbol_key` |
| `csharp.member` | Member declaration (method, property, field, event, constructor) | `name`, `kind`, `accessibility`, `is_static`, `is_async`, `return_type`, `parameters`, `symbol_key` |
| `csharp.attribute` | Attribute annotation | `name`, `target`, `arguments` |
| `csharp.using` | Using directive | `namespace`, `alias`, `is_static` |
| `csharp.generated_document` | Source generator output | `is_generated=true`, `generator`, `hint_name` |

## Edge Types

The C# format creates the following edge types:

| Edge Type | Description | Properties |
|-----------|-------------|------------|
| `HAS_PART` | Composition (document→namespace, namespace→type, type→member) | `is_composition=true`, `ordinal` |
| `DECLARES_SYMBOL` | Symbol declaration | `symbol_key` |
| `INHERITS_FROM` | Base class inheritance | `from_symbol_key`, `to_symbol_key` |
| `IMPLEMENTS` | Interface implementation | `from_symbol_key`, `to_symbol_key` |
| `ANNOTATED_WITH` | Attribute application | `attribute_name` |
| `USES_SYMBOL` | Symbol reference (method call, field access, type usage) | `from_span_id`, `to_symbol_key`, `status` |

## Helper Views

The format provides three DuckDB views for convenient querying:

### `csharp_namespaces`
```sql
SELECT * FROM csharp_namespaces
WHERE qualified_name LIKE 'MyApp.Services%';
```

Columns:
- `namespace_id`: Node ID
- `file_uri`: Source file URI
- `qualified_name`: Fully qualified namespace
- `name`: Simple name
- `parent_namespace_id`: Parent namespace (if nested)
- `span_id`: Location in source
- `properties`: Full JSON metadata

### `csharp_types`
```sql
SELECT * FROM csharp_types
WHERE kind = 'class'
  AND accessibility = 'public';
```

Columns:
- `type_id`: Node ID
- `file_uri`: Source file URI
- `qualified_name`: Fully qualified type name
- `name`: Simple name
- `kind`: `class`, `interface`, `struct`, `enum`, `record`, `delegate`
- `namespace`: Containing namespace
- `accessibility`: `public`, `internal`, `private`, `protected`, etc.
- `base_type`: Base class name (if any)
- `interfaces`: JSON array of implemented interfaces
- `is_partial`: Boolean
- `is_static`: Boolean
- `is_record`: Boolean
- `span_id`: Location in source
- `properties`: Full JSON metadata (includes `symbol_key`)

### `csharp_members`
```sql
SELECT * FROM csharp_members
WHERE kind = 'method'
  AND is_async = true;
```

Columns:
- `member_id`: Node ID
- `file_uri`: Source file URI
- `declaring_type_id`: Parent type node ID
- `declaring_type`: Qualified name of declaring type
- `name`: Member name
- `kind`: `method`, `property`, `field`, `event`, `constructor`, `indexer`
- `accessibility`: `public`, `private`, etc.
- `is_static`: Boolean
- `is_async`: Boolean
- `return_type`: Return type name (for methods/properties)
- `parameters`: JSON array of parameter info
- `span_id`: Location in source
- `properties`: Full JSON metadata (includes `symbol_key`)

## Query Examples

### Find all public interfaces
```sql
SELECT qualified_name, file_uri
FROM csharp_types
WHERE kind = 'interface'
  AND accessibility = 'public'
ORDER BY qualified_name;
```

### Find implementations of a specific interface
```sql
-- Find all types implementing IPaymentProcessor
SELECT t.qualified_name, t.file_uri
FROM csharp_types AS t
JOIN edge AS e ON e.source_node_id = t.type_id
WHERE e.type = 'IMPLEMENTS'
  AND e.properties->>'to_symbol_key' LIKE '%IPaymentProcessor%';
```

### Find async methods
```sql
SELECT
    declaring_type || '.' || name AS full_name,
    return_type,
    file_uri
FROM csharp_members
WHERE is_async = true
ORDER BY declaring_type, name;
```

### Find all usages of a symbol
```sql
-- Find all references to a specific type
WITH target_type AS (
    SELECT
        type_id,
        properties->>'symbol_key' AS symbol_key
    FROM csharp_types
    WHERE qualified_name = 'MyApp.Services.PaymentService'
)
SELECT
    doc.uri AS usage_location,
    s.start_line,
    s.start_column
FROM edge AS e
JOIN target_type AS tt ON e.properties->>'to_symbol_key' = tt.symbol_key
JOIN span AS s ON s.id = e.properties->>'from_span_id'::UUID
JOIN node AS doc ON doc.id = s.document_id
WHERE e.type = 'USES_SYMBOL';
```

### Find partial types
```sql
-- Find all partial type declarations
SELECT
    qualified_name,
    file_uri,
    COUNT(*) OVER (PARTITION BY properties->>'symbol_key') AS part_count
FROM csharp_types
WHERE is_partial = true
ORDER BY qualified_name, file_uri;
```

### Find source generator outputs
```sql
SELECT
    uri,
    properties->>'generator' AS generator_name,
    properties->>'hint_name' AS hint_name
FROM node
WHERE kind = 'csharp.generated_document';
```

### Find analyzer diagnostics
```sql
-- Find all analyzer warnings and errors
SELECT
    a.rule_id,
    a.severity,
    a.message,
    a.target_uri,
    a.data->>'project_path' AS project
FROM annotation AS a
WHERE a.kind = 'lint'
  AND a.rule_id LIKE 'csharp/%'
  AND a.severity IN ('warning', 'error')
ORDER BY a.severity DESC, a.rule_id;
```

### Find types with specific attributes
```sql
-- Find all types decorated with [Serializable]
SELECT DISTINCT
    t.qualified_name,
    t.file_uri
FROM csharp_types AS t
JOIN edge AS e ON e.source_node_id = t.type_id
JOIN node AS attr ON attr.id = e.destination_node_id
WHERE e.type = 'ANNOTATED_WITH'
  AND attr.kind = 'csharp.attribute'
  AND attr.properties->>'name' LIKE '%Serializable%';
```

### Analyze member complexity
```sql
-- Find methods with many parameters
SELECT
    declaring_type || '.' || name AS method,
    JSON_ARRAY_LENGTH(parameters) AS param_count,
    file_uri
FROM csharp_members
WHERE kind = 'method'
  AND JSON_ARRAY_LENGTH(parameters) > 5
ORDER BY param_count DESC;
```

## Symbol Keys

Symbol keys (`symbol_key` property) are stable identifiers derived from Roslyn's `SymbolKey` API:
- **Deterministic**: Same symbol produces same key across runs
- **Unique**: Different symbols have different keys
- **Semantic**: Based on symbol meaning, not syntax location
- **Merge-friendly**: Partial types/members share the same `symbol_key`

Symbol keys enable:
- Merging partial type definitions
- Tracking symbol references across files
- Matching generated code to source declarations

## Configuration

The C# format respects the following settings:

### Workspace Host
Configure via DI registration in your application:
```csharp
services.AddSingleton<CSharpWorkspaceHost>();
services.AddSingleton<IFormatLoader, CSharpLoader>();
```

### Analyzer Settings
Override analyzer severity via `AnalyzerSettings`:
```csharp
var settings = new AnalyzerSettings(new Dictionary<string, AnalyzerRuleSettings>
{
    ["csharp/CS0618"] = new() { RuleId = "csharp/CS0618", Severity = AnalysisSeverity.None }
});
```

### Concurrency
The workspace host limits concurrent project analyses:
- Default: `min(Environment.ProcessorCount / 2, 4)`
- Keeps memory usage ≤ 2 GB

## Performance Characteristics

| Operation | Cold (first run) | Warm (cached) |
|-----------|------------------|---------------|
| Parse syntax | 10-50 ms per file | N/A |
| Load project | ≤15 s | N/A |
| Run generators | 0.5-2.0 s | Cached per project |
| Execute analyzers | 0.5-2.0 s | Cached per project |
| Semantic binding | 50-200 ms per file | N/A |

Memory budget: ≤2 GB total (enforced via concurrency throttling)

## Diagnostics

Diagnostics are emitted as RepoQL annotations with `kind='lint'`:

| Field | Description |
|-------|-------------|
| `rule_id` | Format: `csharp/<DiagnosticId>` (e.g., `csharp/CS0103`, `csharp/CA1806`) |
| `severity` | `error`, `warning`, `info`, `suggestion`, `none` |
| `message` | Human-readable diagnostic message |
| `target_uri` | Source file URI |
| `target_span_id` | Location in source |
| `data.category` | Diagnostic category |
| `data.help_link` | Documentation URL |
| `data.project_path` | Owning project (project mode only) |
| `data.is_generated` | `true` for generator output |
| `data.symbol_key` | Affected symbol (if applicable) |

### Example: Query for specific diagnostic
```sql
SELECT
    message,
    target_uri,
    data->>'help_link' AS docs
FROM annotation
WHERE rule_id = 'csharp/CS8618'  -- Non-nullable field must contain a non-null value
ORDER BY target_uri;
```

## Source Generators

Generated files are treated as first-class documents:
- **Virtual URIs**: `repoql://generated/<project>/<generator>/<hint>.cs`
- **Flagged**: `is_generated=true` in node properties
- **Full analysis**: Pass through same loader/materializer/analyzer pipeline
- **Diagnostics included**: Generator diagnostics appear in annotations

### Query generated documents
```sql
SELECT
    n.uri,
    n.properties->>'generator' AS generator,
    n.properties->>'hint_name' AS output_file,
    a.digest
FROM node AS n
JOIN artifact AS a ON a.id = n.artifact_id
WHERE n.kind = 'csharp.generated_document';
```

## X-ray Summaries

The C# format provides structured X-ray output:

### Headline
Compact one-line summary: `filename | code.csharp | size | lines | namespaces:N types:T members:M`

### Summary
Key statistics and high-level structure (< 10 lines)

### Structure
Detailed outline showing:
- Namespaces and their nesting
- Types with accessibility and modifiers
- Public members with signatures
- Partial type indicators
- Generator outputs

## Limitations

- **Cross-project resolution**: Symbol resolution is project-scoped. Cross-project references create `USES_SYMBOL` edges with `status="unresolved"`.
- **Fast mode constraints**: Files without a `.csproj` get syntax-only analysis (no semantic edges, no diagnostics).
- **Memory budget**: Large solutions may need to be analyzed in batches to stay within the 2 GB limit.
- **Generator caching**: Generator outputs are cached per `(ProjectVersion, GeneratorFingerprint)`. Changing a generator invalidates the cache.

## Troubleshooting

### No diagnostics appearing
- **Check analysis mode**: Query `node.properties->>'analysis_mode'` - if `'fast'`, no project was found
- **Verify project loads**: Check logs for MSBuild load failures
- **Check analyzer config**: Ensure analyzers are referenced in `.csproj`

### Missing symbol references
- **Requires project mode**: Symbol resolution needs MSBuild workspace
- **Check `USES_SYMBOL` edges**: Query `edge` table with `type='USES_SYMBOL'`
- **Look for `status="unresolved"`**: Indicates cross-project or missing references

### Performance issues
- **Monitor concurrent projects**: Check `CSharpWorkspaceHost.ActiveSessionCount`
- **Review cache hits**: Enable verbose logging to see cache effectiveness
- **Consider batching**: Process large solutions in smaller chunks

## See Also

- [X-ray Documentation](../XRay.md) - Understanding X-ray summaries
- [Semantic Media Types](../SemanticMediaType.md) - Media type specification
- [Vocabulary](../Vocabulary.md) - Full node kind and edge type reference
- [Analyzer Settings](../../src/RepoQL.Contracts/Analysis/AnalyzerSettings.cs) - Configuration API
