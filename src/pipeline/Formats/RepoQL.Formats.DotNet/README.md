# RepoQL.Formats.DotNet

.NET project and solution format handlers for RepoQL.

## C# Project Files (*.csproj)

C# project format handler. Mirrors the Markdown/Mermaid patterns:

- Media type: `text/xml;kind=dotnet.csproj`
- Loader parses SDK, TargetFramework(s), OutputType, pack flags, `PackageReference`s and `ProjectReference`s.
- Materializer emits one `document` node per project with x‑ray fields (headline, summary, structure) rendered by Liquid templates.
- Composition children via `HAS_PART`:
  - `dotnet.tfm` — one per target framework (`props.tfm`)
  - `nuget.package` — one per package (`props.id`, `props.version?`)
  - `dotnet.project_reference` — one per project reference (`props.include`)
- Analyzer `csproj/unpinned-package` warns on missing or floating package versions.

## X‑ray Templates

Embedded templates (Liquid/Fluid) under `Templates/xray`:
- `headline.liquid` — concise, grep‑friendly line with size, sdk, output type, pack, tfm, counts.
- `summary.liquid` — key facts (SDK, OutputType, Pack), TFMs, top packages and project refs.
- `structure.liquid` — outline listing TFMs, packages, and references (with truncation).

Model keys available to templates:
- `file_name`, `size_bytes`, `sdk`, `output_type`, `pack`
- `tfms` (array), `tfm_text` (string)
- `packages` (array of `{ id, version? }`), `package_count`
- `project_refs` (array of `{ include }`), `project_ref_count`

## Node kinds
- `document` — the csproj itself (props include `sdk`, `tfms`, `output_type`, `pack`)
- `dotnet.tfm` — target frameworks
- `nuget.package` — packages
- `dotnet.project_reference` — project references

## Registration
Registered in DI with:
- `FormatDescriptor(text/xml;kind=dotnet.csproj, labels=["csproj"])`
- Templating wired via `AddLiquidTemplatingFromEmbedded` for this assembly.

## Tests
See `CsProjXrayTests` and `CsProjVariantsTests` in `RepoQL.Tests` for end‑to‑end coverage of x‑ray, props, and analyzer behavior.

---

## Solution Files (*.sln)

Visual Studio solution format handler:

- Media type: `text/plain;kind=dotnet.sln`
- Loader parses format version, Visual Studio version, projects, solution folders, configurations, and nested project mappings.
- Materializer emits one `document` node per solution with x‑ray fields (headline, summary, structure) rendered by Liquid templates.
- Composition children via `HAS_PART`:
  - `dotnet.solution_folder` — virtual folders in the solution (`props.name`, `props.guid`)
  - `dotnet.solution_project` — project references (`props.name`, `props.path`, `props.guid`, `props.type_guid`)

### X‑ray Templates

Embedded templates (Liquid/Fluid) under `Templates/xray`:
- `headline-sln.liquid` — concise, grep‑friendly line with size, format version, VS version, counts.
- `summary-sln.liquid` — key facts (Format, VS version, project/folder/config counts).
- `structure-sln.liquid` — outline listing folders with project counts, projects with paths, and configurations (with truncation).

Model keys available to templates:
- `file_name`, `size_bytes`, `format_version`, `vs_version`
- `project_count`, `folder_count`, `config_count`
- `projects` (array of `{ name, path, guid }`), `folders` (array of `{ name, guid }`)
- `configs` (array of strings like "Debug|Any CPU")
- `projects_display`, `folders_display`, `configs_text` (formatted strings)

### Node kinds
- `document` — the sln itself (props include `format_version`, `vs_version`, `project_count`, `folder_count`, `config_count`)
- `dotnet.solution_folder` — solution folders
- `dotnet.solution_project` — project references

### Registration
Registered in DI with:
- `FormatDescriptor(text/plain;kind=dotnet.sln, labels=["sln"])`
- Templating wired via `AddLiquidTemplatingFromEmbedded` for this assembly.

### Tests
See `SlnXrayTests` and `SlnVariantsTests` in `RepoQL.Tests` for end‑to‑end coverage of x‑ray and parsing behavior.


---

## C# Source Files (*.cs)

Individual C# files are now indexed through Roslyn so RepoQL understands namespaces, types, and members without relying on project-level metadata.

- Media type: `text/plain;kind=code.csharp`
- Loader: `CSharpLoader` builds a Roslyn syntax tree, walks namespaces/types/members/using directives, captures spans plus summary metadata (`CSharpDocumentState`), and optionally asks `CSharpWorkspaceHost` to reopen the owning `.csproj` via `MSBuildWorkspace` so symbol keys, semantic references, and diagnostics match `dotnet build`.
- Materializer: emits progressive X-ray strings directly from the captured inventory and creates graph nodes for `csharp.namespace`, `csharp.type`, and `csharp.member` with precise spans, containment edges, and resolved `USES_SYMBOL` links (falling back to a lightweight compilation when no project is available).

### X-ray

Generated inline (no Liquid templates yet):
- **headline** – file name plus aggregate counts, e.g. `PaymentService.cs | class:1, interface:0 | methods:4 async:1`
- **summary** – counts plus the top public types and async members.
- **structure** – namespace/type tree with indented members for quick navigation.

### Graph Projection (current)

- `document` node carries language + line/type/member counts.
- `csharp.namespace` nodes for declared namespaces (nested where appropriate).
- `csharp.type` nodes (classes/structs/records/interfaces/enums) with namespace, accessibility, inheritance metadata, and Roslyn `symbol_key`s.
- `csharp.member` nodes (methods, constructors, properties, indexers, events, fields) with accessibility, async/static flags, return types, parameter lists, and `symbol_key`s.
- Composition edges (`HAS_PART`) connect document → namespace → type → member hierarchies; span rows capture line ranges for every symbol.
- `USES_SYMBOL` edges capture references resolved via Roslyn (e.g., base types, identifiers, cross-file usages when a project is available) with props for `symbol_key`, `symbol_kind`, and status.

### DuckDB helper views

The loader registers views so queries stay concise:

- `csharp_namespaces` — document URI + namespace metadata.
- `csharp_types` — qualified names, accessibility, and inheritance info.
- `csharp_members` — member declarations joined with their declaring types and parameter JSON.

### Diagnostics

`CSharpAnalyzer` reuses the loader’s Roslyn diagnostics (from the MSBuild-backed analysis when available, or the fallback compilation otherwise) to emit RepoQL `lint` results using rule ids `csharp/<ID>` (e.g., `csharp/CS0246`). Results include line/column metadata and respect per-rule overrides provided via `AnalyzerSettings`.

### Source Generators

When an owning `.csproj` is available, the shared `CSharpWorkspaceHost` runs all source generators referenced by the project. Their outputs are materialized as virtual documents with `StoreUri` values like `repoql://generated/<project>/<generator>/<hint>` and document props containing `is_generated=true`, `generator`, and `hint_name`. The generated documents share the same graph projection, spans, and diagnostics pipeline as real files, but are emitted exactly once per project to avoid duplication across multiple source files.

### Registration

Registered in DI with:

- `FormatDescriptor(text/plain;kind=code.csharp, labels=["csharp","cs"])`
- Templating reuse is not required yet (strings are generated inline), but the assembly is already wired for future templates.

### Tests

`CSharpLoaderTests` cover loader/materializer output, schema scripts, `USES_SYMBOL` edges, analyzer override behavior, and the project-aware diagnostics/reference flows.

---

## Usage Examples

The C# format provides comprehensive querying capabilities through SQL. Below are practical examples demonstrating common use cases.

### 1. Find all public types implementing IDisposable

```sql
SELECT
    props->>'qualified_name' as qualified_name,
    props->>'base_type' as base_type
FROM csharp_types
WHERE props->>'accessibility' = 'public'
  AND json_array_length(props->'interfaces') > 0
  AND EXISTS (
    SELECT 1 FROM json_array_elements_text(props->'interfaces') AS iface
    WHERE iface LIKE '%IDisposable%'
  );
```

Useful for identifying resources that need proper cleanup. The `json_array_elements_text` function unpacks the interfaces array for searching.

### 2. Find all async methods

```sql
SELECT
    t.props->>'qualified_name' as type,
    m.props->>'name' as method,
    m.props->>'return_type' as return_type
FROM csharp_members m
JOIN csharp_types t ON (m.props->>'declaring_type')::text = t.node_id::text
WHERE (m.props->>'is_async')::boolean = true
  AND m.props->>'kind' = 'method';
```

Quickly identify all asynchronous operations across your codebase for performance analysis or migration planning.

### 3. Find classes with no base type or interfaces (potential value objects)

```sql
SELECT
    props->>'qualified_name' as qualified_name,
    props->>'namespace' as namespace
FROM csharp_types
WHERE props->>'kind' = 'class'
  AND props->>'base_type' IS NULL
  AND (props->'interfaces' IS NULL OR json_array_length(props->'interfaces') = 0)
ORDER BY props->>'namespace', props->>'name';
```

Identifies simple classes that might be value objects or POCOs, useful for architectural analysis.

### 4. Cross-reference analysis - who calls this method?

```sql
WITH target AS (
  SELECT node_id
  FROM csharp_members
  WHERE props->>'qualified_name' = 'MyNamespace.MyClass.MyMethod'
)
SELECT
    src_t.props->>'qualified_name' as caller_type,
    src_m.props->>'name' as caller_method,
    e.props->>'symbol_key' as symbol
FROM edge e
JOIN target t ON e.dst_id = t.node_id
JOIN csharp_members src_m ON e.src_id = src_m.node_id
JOIN csharp_types src_t ON (src_m.props->>'declaring_type')::text = src_t.node_id::text
WHERE e.type = 'USES_SYMBOL';
```

Powerful for impact analysis - find all code that depends on a specific method before refactoring.

### 5. Find partial types

```sql
SELECT
    props->>'namespace' as namespace,
    props->>'name' as name,
    COUNT(*) as file_count,
    array_agg(DISTINCT uri) as files
FROM csharp_types
WHERE (props->>'is_partial')::boolean = true
GROUP BY props->>'namespace', props->>'name'
HAVING COUNT(*) > 1
ORDER BY file_count DESC;
```

Lists partial types split across multiple files, showing how many files define each type.

### 6. API surface analysis - all public methods in a namespace

```sql
SELECT
    t.props->>'name' as type,
    m.props->>'name' as method,
    m.props->>'return_type' as return_type,
    m.props->'parameters' as parameters
FROM csharp_members m
JOIN csharp_types t ON (m.props->>'declaring_type')::text = t.node_id::text
WHERE t.props->>'namespace' LIKE 'MyApp.Public.%'
  AND t.props->>'accessibility' = 'public'
  AND m.props->>'accessibility' = 'public'
  AND m.props->>'kind' = 'method'
ORDER BY t.props->>'name', m.props->>'name';
```

Extract the complete public API surface of a namespace - perfect for generating documentation or tracking API changes.

### 7. Find types with many dependencies (high coupling)

```sql
WITH refs AS (
  SELECT
    src_id,
    COUNT(DISTINCT dst_id) as dep_count
  FROM edge
  WHERE type = 'USES_SYMBOL'
  GROUP BY src_id
)
SELECT
    t.props->>'qualified_name' as qualified_name,
    r.dep_count as dependency_count
FROM refs r
JOIN csharp_types t ON r.src_id = t.node_id
WHERE r.dep_count > 10
ORDER BY r.dep_count DESC
LIMIT 20;
```

Identifies highly coupled types that might benefit from refactoring. High dependency counts often indicate design issues.

### 8. Find all static classes (utility classes)

```sql
SELECT
    props->>'namespace' as namespace,
    props->>'name' as name,
    props->>'qualified_name' as qualified_name
FROM csharp_types
WHERE (props->>'is_static')::boolean = true
  AND props->>'kind' = 'class'
ORDER BY props->>'namespace', props->>'name';
```

Lists all static utility classes in your codebase for inventory or architectural review.

### 9. Find record types

```sql
SELECT
    props->>'qualified_name' as qualified_name,
    props->>'namespace' as namespace,
    props->>'accessibility' as accessibility
FROM csharp_types
WHERE (props->>'is_record')::boolean = true
ORDER BY props->>'namespace', props->>'name';
```

Identifies all record types, useful for tracking usage of modern C# features.

### 10. Find methods with specific parameter patterns

```sql
SELECT
    t.props->>'qualified_name' as type,
    m.props->>'name' as method,
    m.props->'parameters' as parameters
FROM csharp_members m
JOIN csharp_types t ON (m.props->>'declaring_type')::text = t.node_id::text
WHERE m.props->>'kind' = 'method'
  AND json_array_length(m.props->'parameters') > 0
  AND EXISTS (
    SELECT 1 FROM json_array_elements(m.props->'parameters') AS param
    WHERE param->>'type' LIKE '%CancellationToken%'
  );
```

Find all methods accepting CancellationToken parameters, useful for async operation audits.

---

## Node Properties Schema

All C# nodes store their metadata in the `props` JSON column. Below is the complete schema for each node kind.

### document

The root document node for each C# file.

**Properties:**
- `language` (string): Always "csharp"
- `file_name` (string): Name of the file
- `line_count` (number): Total lines in the file
- `namespace_count` (number): Number of namespaces declared
- `type_count` (number): Total number of types (classes, interfaces, structs, etc.)
- `member_count` (number): Total number of members (methods, properties, fields, etc.)
- `using_count` (number): Number of using directives
- `public_type_count` (number): Number of public types
- `method_count` (number): Number of methods
- `async_member_count` (number): Number of async methods/properties

### csharp.namespace

Represents a namespace declaration.

**Properties:**
- `name` (string): Simple namespace name (last segment, e.g., "Generic" from "System.Collections.Generic")
- `qualified_name` (string): Full namespace path (e.g., "System.Collections.Generic")
- `parent_namespace_id` (string, optional): UUID of the parent namespace if nested

**Example query:**
```sql
SELECT props->>'qualified_name' FROM csharp_namespaces;
```

### csharp.type

Represents a type declaration (class, struct, interface, record, enum).

**Properties:**
- `name` (string): Simple type name without namespace
- `qualified_name` (string): Full name including namespace (e.g., "MyApp.Services.PaymentService")
- `kind` (string): One of: "class", "struct", "interface", "record", "enum", "delegate"
- `namespace` (string): Declaring namespace (empty string if global namespace)
- `accessibility` (string): One of: "public", "internal", "private", "protected", "protected internal", "private protected"
- `is_partial` (boolean): Whether type is declared with `partial` keyword
- `is_static` (boolean): Whether type is static (classes only)
- `is_record` (boolean): Whether this is a record type
- `base_type` (string, optional): Base class name (null if no base class or if this is an interface)
- `interfaces` (array of strings): List of implemented interface names
- `symbol_key` (string, optional): Roslyn documentation comment ID for cross-referencing

**Example query:**
```sql
SELECT
    props->>'qualified_name' as name,
    props->>'kind' as kind,
    props->>'accessibility' as access
FROM csharp_types
WHERE props->>'namespace' LIKE 'MyApp.%';
```

### csharp.member

Represents a member declaration (method, property, field, event, constructor, indexer).

**Properties:**
- `name` (string): Member name
- `kind` (string): One of: "method", "property", "field", "event", "constructor", "indexer"
- `accessibility` (string): Same values as type accessibility
- `is_static` (boolean): Whether member is static
- `is_async` (boolean): Whether method/property is async
- `return_type` (string): Return type for methods/properties, type for fields (empty string for constructors)
- `declaring_type` (string): Simple name of the declaring type
- `parameters` (array of objects): Parameter list, each with:
  - `name` (string): Parameter name
  - `type` (string): Parameter type
  - `has_default` (boolean): Whether parameter has a default value
- `symbol_key` (string, optional): Roslyn symbol key for cross-referencing

**Example query:**
```sql
SELECT
    props->>'name' as member,
    props->>'kind' as kind,
    props->>'return_type' as return_type,
    json_array_length(props->'parameters') as param_count
FROM csharp_members
WHERE (props->>'is_async')::boolean = true;
```

### Accessing nested JSON properties

DuckDB provides powerful JSON operators for querying node properties:

- `props->>'key'` - Extract text value
- `props->'key'` - Extract JSON value (preserves type)
- `json_array_length(props->'array_key')` - Get array length
- `json_array_elements(props->'array_key')` - Expand array to rows
- `json_array_elements_text(props->'array_key')` - Expand array to text rows

**Example - Query methods with multiple parameters:**
```sql
SELECT
    props->>'name' as method,
    json_array_length(props->'parameters') as param_count,
    props->'parameters' as parameters
FROM csharp_members
WHERE props->>'kind' = 'method'
  AND json_array_length(props->'parameters') > 3;
```

---

## Troubleshooting

### MSBuild/SDK Not Found

**Symptom:** Files are indexed but no semantic analysis or diagnostics appear. Logs show "MSBuild workspace initialization failed" or "SDK not found".

**Solution:**
1. Ensure the .NET SDK is installed: `dotnet --version`
2. For SDK-style projects, verify the SDK version matches the project's `TargetFramework` or `TargetFrameworks`
3. On Linux/macOS, set `DOTNET_ROOT` environment variable if SDK is in a non-standard location
4. Verify MSBuild can locate the SDK: `dotnet build --verbosity diagnostic`

If MSBuild remains unavailable, the C# loader will fall back to a lightweight compilation mode with limited semantic analysis.

### Analysis Slow or Timing Out

**Symptom:** Indexing takes longer than expected or times out on large solutions.

**Solution:**
1. **Reduce concurrency:** The default allows up to 4 concurrent project analyses. Lower this if memory is constrained.
2. **Exclude generated files:** Large auto-generated files (e.g., designer files, T4 templates) can slow analysis. Consider excluding them via `.gitignore` or repository configuration.
3. **Check analyzers:** Third-party analyzers can significantly impact performance. Temporarily disable them by creating an empty `Directory.Build.props` or `.editorconfig` that sets all rules to "none".
4. **Profile slow projects:** Use `dotnet build --diagnostic` to identify slow-building projects, then optimize or exclude them.

### Out of Memory Errors

**Symptom:** Process crashes with "OutOfMemoryException" during indexing.

**Solution:**
1. **Reduce concurrent projects:** Lower the concurrent analysis limit (default: 4 concurrent projects).
2. **Increase available memory:** Ensure at least 4GB of RAM is available for indexing large solutions.
3. **Process projects in batches:** Instead of indexing the entire repository at once, process subdirectories separately.
4. **Dispose workspace:** The workspace host automatically disposes Roslyn trees after analysis, but you can force garbage collection between large projects by restarting the indexing process.

### Analyzer Failures

**Symptom:** Diagnostics are missing or incomplete. Logs show "Analyzer threw an exception" or "Analyzer timed out".

**Solution:**
1. **Update analyzers:** Ensure all analyzer packages are up to date. Outdated analyzers may not support newer Roslyn APIs.
2. **Check analyzer configuration:** Verify `.editorconfig` and `Directory.Build.props` have correct severity settings.
3. **Isolate failing analyzers:** Temporarily disable individual analyzers by setting their rules to "none" in `.editorconfig` to identify the problematic analyzer.
4. **Report analyzer bugs:** If an analyzer consistently fails or times out, report the issue to the analyzer maintainer with minimal reproduction steps.

### Symbol Resolution Issues

**Symptom:** `USES_SYMBOL` edges are missing or `symbol_key` properties are null.

**Solution:**
1. **Verify project loads:** Ensure the owning `.csproj` file is present and valid. Run `dotnet build` to confirm the project builds successfully.
2. **Check file membership:** Files must be included in a project for full semantic analysis. Orphaned `.cs` files (not in any project) fall back to lightweight mode.
3. **Multi-project files:** If a file is included in multiple projects, symbol keys are resolved using the first successfully loaded project.
4. **Generated code:** Source-generated files should have `symbol_key` values if the generator ran successfully. Check for generator errors in diagnostics.

### Performance Optimization Tips

1. **Cache indexing results:** RepoQL caches analysis results per project version. Avoid unnecessary changes to project files to maximize cache hits.
2. **Incremental indexing:** When possible, index only changed files rather than the entire repository.
3. **Disable unused features:** If you don't need diagnostics, disable analyzers entirely to speed up indexing.
4. **Use SSD storage:** Roslyn heavily relies on disk I/O. Running on SSD significantly improves performance.
5. **Warm up the JIT:** The first project analysis is always slower due to JIT compilation. Subsequent projects benefit from warmed-up code paths.

---
