---
description: Design for generic and specific JSON format support via the FormatDescriptor architecture
tags: [json, design, format, loader, materializer, schema-scripts]
audience: { human: 40, agent: 60 }
purpose: { design: 90, flow: 10 }
---

# JSON Format Support — Design

## North Star

Every JSON file gets a key tree, pointer addresses, and a meaningful headline. Recognized JSON kinds get domain-aware structure on top. An agent never sees a JSON file as plain text.

*Shallow slice of [docs/north-star/json.md](../north-star/json.md)*

## Context

JSON is the most common structured data format in repositories, yet RepoQL treats most JSON files as plain text. Only `AppSettingsLoader` handles `appsettings*.json` with config-specific structure. Everything else falls through to `PlainTextLoader`.

This design adds a new `RepoQL.Formats.Json` project that provides:
- Pipeline processors (classifier + parser) that claim JSON files before the plain text fallback
- A structure parser that specific JSON handlers compose with for domain-aware loading
- SQL macros and views for querying JSON structure
- An analyzer for secret detection

**Informed by:**
- [docs/north-star/json.md](../north-star/json.md) — vision
- [docs/flows/future/indexing/json-indexing.md](../flows/future/indexing/json-indexing.md) — pipeline flow
- `CsvClassifier` + `CsvParser` + `CsvLoader` — reference implementation for the modern pipeline pattern
- `AppSettingsLoader` — existing JSON-specific loader

## Constraints

- Must follow the modern pipeline pattern: `Classifier` + `Parser` registered via `AddIndexingProcessor<T>()`, same as CSV/XLSX/PDF/DOCX
- `FormatDescriptor` registered for schema scripts (`IFormatSchemaProvider`) and label resolution
- Frozen schema: 5 tables (artifact, node, edge, span, annotation). Extend via views/macros only
- `AppSettingsLoader` stays in `RepoQL.Formats.DotNet` — it's a .NET-specific concern
- Single-writer to DuckDB through `DuckDbDataStore`
- `System.Text.Json` only — no third-party JSON parsers for the generic tier

---

## Design

### Project Structure

```
src/Formats/RepoQL.Formats.Json/
├── JsonClassifier.cs                      -- Classification pipeline processor
├── JsonParser.cs                          -- Parsing pipeline processor (wraps JsonLoader)
├── JsonLoader.cs                          -- Generic JSON loader (IFormatLoader + IFormatMaterializer)
├── JsonStructureParser.cs                 -- Core parser (key tree, shape, pointers, line numbers)
├── JsonNormalizer.cs                      -- JSONC/JSON5 comment stripping with source map
├── JsonMediaTypes.cs                      -- Media type constants
├── JsonServiceCollectionExtensions.cs     -- DI registration
├── Analysis/
│   ├── JsonSecretDetector.cs              -- IFormatAnalyzer for secret patterns
│   └── SecretPatterns.cs                  -- Pattern definitions
├── Schema/
│   └── json_macros.sql                    -- json_files(), json_keys view
└── Templates/
    └── explore/
        ├── headline.liquid
        ├── structure.liquid
        └── summary.liquid
```

One project. No sub-projects per JSON kind — specific handlers (OpenAPI, JSON Schema) are future work and will be separate projects that depend on `JsonStructureParser` from this project.

### Pipeline Integration

JSON follows the modern pipeline pattern established by CSV: separate classifier and parser processors registered via `AddIndexingProcessor<T>()`.

#### JsonClassifier

```csharp
public sealed class JsonClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (!IsJsonExtension(item.Name))
            return next(item);

        return Task.FromResult<(SemanticMediaType?, PipelineResult)>(
            (JsonMediaTypes.Json, PipelineResult.Success));
    }

    private static bool IsJsonExtension(string name)
        => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".json5", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase);
}
```

This follows the CsvClassifier pattern exactly: check extension, return media type directly if matched, delegate via `next()` if not. No content sniffing at classification time — the classifier claims by extension alone.

**AppSettings interaction:** The JSON classifier claims all `.json` files. AppSettings files get the generic `application/json` media type from classification. In the parsing pipeline, the `JsonParser` checks `CanLoadAsync` before parsing — `JsonLoader.CanLoadAsync` returns `false` for filenames matching `appsettings*.json` or `launchSettings.json`, deferring them to `AppSettingsLoader` through the `FormatRegistryParser` path or a future AppSettings pipeline processor.

#### JsonParser

```csharp
public sealed class JsonParser(JsonLoader loader, ILogger<JsonParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!IsJsonKind(item.MediaType?.Kind))
            return await next(item).ConfigureAwait(false);

        try
        {
            var discovered = new DiscoveredArtifact
            {
                File = item,
                RepoUri = item.Uri,
                MediaType = item.MediaType
            };

            if (!await _loader.CanLoadAsync(discovered, token).ConfigureAwait(false))
                return await next(item).ConfigureAwait(false);

            var documentModel = await _loader.LoadAsync(discovered, token).ConfigureAwait(false);
            item["document_model"] = documentModel;

            var records = _loader.Materialize(documentModel);
            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JSON file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
```

This follows the CsvParser pattern: check media type kind, delegate to loader for loading and materialization, catch exceptions.

### JsonStructureParser — The Shared Core

All JSON parsing flows through `JsonStructureParser`. It produces a complete, useful result on its own — key tree, shape, pointer addresses, line numbers. `JsonLoader` uses it directly. Specific handlers compose with it, adding domain extraction on top.

```csharp
public sealed class JsonStructureParser
{
    public JsonParseResult Parse(string text, JsonParseOptions? options = null);
}

public record JsonParseResult
{
    public JsonShape Shape { get; init; }              // FlatObject, NestedObject, Array, SingleValue
    public IReadOnlyList<JsonKeyInfo> Keys { get; init; }
    public int TotalKeyCount { get; init; }
    public int MaxDepth { get; init; }
    public int? ArrayLength { get; init; }             // For root arrays
}

public record JsonKeyInfo
{
    public string Path { get; init; }                  // JSON Pointer: /database/host
    public string Name { get; init; }                  // Last segment: host
    public int Depth { get; init; }
    public JsonValueKind ValueKind { get; init; }      // From System.Text.Json
    public int StartLine { get; init; }                // 1-based
    public int EndLine { get; init; }
    public int EstimatedTokens { get; init; }          // Subtree token estimate
    public string? ScalarValue { get; init; }          // For simple values, truncated
    public int? ArrayLength { get; init; }             // For array values
}

public enum JsonShape { FlatObject, NestedObject, Array, SingleValue, Empty }
```

**What it does:**
- Encodes the input string to UTF-8 bytes once at the top. Both `Utf8JsonReader` and the line-offset table operate on this same byte array — this prevents offset domain mismatch between character positions and byte positions.
- Builds a line-offset table by scanning the UTF-8 byte array for `0x0A` bytes in a single pass. Each entry records the byte offset of a line start: `[0, 45, 112, 178, ...]`. Works correctly for both `\n` and `\r\n` (scan for `0x0A` only; `\r` bytes are part of the byte offsets but don't define line boundaries).
- Parses with `Utf8JsonReader` over the UTF-8 byte array (streaming, no DOM allocation).
- Walks the token stream, producing `JsonKeyInfo` for each key with JSON Pointer path, value kind, and subtree token estimate.
- Resolves line numbers via binary search into the line-offset table — `TokenStartIndex` (byte offset into the UTF-8 byte array) → line number in O(log n).
- Detects shape: flat object (all scalar values), nested object, array of records, single value.
- For root arrays: reads the first `MaxSampleRecords` (default: 100) elements, then stops. No full parse of large data files.

**Path construction state machine:** Building JSON Pointer paths from a forward-only reader requires tracking the current location in the tree. The parser maintains a stack of `PathSegment` entries:

```csharp
private readonly record struct PathSegment(string? Name, bool IsArray, int ArrayIndex);
```

- On `PropertyName`: push the property name.
- On `StartObject`/`StartArray` after a property: the name is already on the stack.
- On `StartObject`/`StartArray` inside an array: push the current array index, then increment.
- On scalar value: emit key info with the current path, then pop if inside an object property.
- On `EndObject`/`EndArray`: pop the stack.

The asymmetry between scalars (push-emit-pop on the same token pair) and containers (push on PropertyName, pop on EndObject/EndArray) requires careful handling. Array index tracking is manual since `Utf8JsonReader` doesn't provide array indices. Expect ~120 lines of code; thorough tests for nested arrays of objects are essential.

**Subtree token estimation:** For container values (objects and arrays), the parser records `TokenStartIndex` at the opening token and reads forward until the matching close token, noting the closing `TokenStartIndex`. The byte span between them estimates subtree size: `estimatedTokens = (endByte - startByte) / 4`. This means the parser reads the entire subtree even for keys it might otherwise skip — the streaming reader provides no way to compute span length without advancing through the content. For typical config/schema files (< 100KB) this is negligible. For large data files the parser early-outs after sampling, so only sampled records are fully traversed.

**Performance characteristics:**
- UTF-8 encoding: one allocation per parse (the byte array)
- Single pass over the byte array for line offsets, single streaming pass for parsing
- No DOM in memory — streaming parse
- Line tracking is O(n + k*log n) where n = byte count, k = key count
- Large data files: early-out after sampling

**What it does NOT do:**
- Produce `Records`, `Artifact`, `Node`, `Edge`, or `Span` objects — that's the materializer's job
- Render templates — that's the loader's job
- Know about any specific JSON kind — it's format-agnostic
- Provide random access to JSON elements — specific handlers that need DOM access parse with `JsonDocument` in their own domain extraction

**Node selection heuristic:** `JsonStructureParser` returns all keys, but the materializer selects which become nodes. Two constraints work together:

| Rule | Purpose |
|------|---------|
| Depth 0-1: always a node | Top-level keys are navigable |
| Depth 2+: node only if container | Deep scalars stay in structure text, not in the graph |
| Per-file cap: `MaxNodes` (default: 200) | Prevents graph bloat for flat files (i18n with 2000 depth-1 keys, lock files with hundreds of packages) |
| Configurable via `JsonParseOptions` | Specific handlers can tune both `MaxNodeDepth` and `MaxNodes` |

When the cap is reached, remaining keys still appear in the structure text but not as graph nodes. This ensures `json_keys()` results are bounded while the full key tree remains visible in explore output.

### JsonLoader — The Generic Handler

Implements `IFormatLoader`, `IFormatMaterializer`, and `IFormatSchemaProvider`. `JsonParser` delegates to it.

```csharp
public sealed class JsonLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    private readonly JsonStructureParser _parser;
    private readonly ITemplateRenderer _renderer;

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken ct);
    public Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken ct);
    public bool Supports(SemanticMediaType mediaType);
    public Records Materialize(DocumentModel document);
    public IEnumerable<FormatSqlScript> GetSchemaScripts();
}
```

**`CanLoadAsync`**: Returns `true` for extensions the loader can currently handle, `false` for those requiring normalization not yet delivered. Initially: `true` for `.json`, `.jsonl`, `.ndjson`; `false` for `.jsonc` (until Plan 03 adds JSONC normalization) and `.json5` (until JSON5 normalization is implemented). Also returns `false` when the **filename** (not full path) matches `appsettings*.json` or `launchSettings.json` (case-insensitive) — this prevents the generic loader from competing with AppSettingsLoader.

**`LoadAsync`**:
1. Read file via `FileContentReader.ReadAllTextWithDigestAsync()`
2. For `.jsonc`: encode to UTF-8 bytes, normalize via `JsonNormalizer.StripComments(byte[])`, parse via `JsonStructureParser.Parse(ReadOnlySpan<byte>)`. Store original text in `DocumentModel.Text`.
3. For `.json`: parse via `JsonStructureParser.Parse(string)`. On `JsonException`, attempt fallback: encode to bytes, normalize, re-parse. This handles real-world `.json` files with comments.
4. For `.jsonl`/`.ndjson`: parse with `IsJsonl = true` option.
5. Store `JsonParseResult` in `DocumentModel.Metadata`.

**`Materialize`**:
1. Retrieve `JsonParseResult` from metadata
2. Build template model (same pattern as `CsvLoader.Materialize`)
3. Render headline, summary, structure via Liquid templates
4. Create `Artifact` with x-ray metadata
5. Create `document` node with shape properties
6. Create child nodes for keys passing the selection heuristic (depth + cap)
7. Create `Span` for each node, mapping to source line ranges
8. Create `HAS_PART` edges from document to child nodes
9. Return `Records`

**`GetSchemaScripts`**: Returns `json_macros.sql` (see SQL Surface section).

### Registration

```csharp
public static class JsonServiceCollectionExtensions
{
    public static IServiceCollection AddJsonFormat(this IServiceCollection services)
    {
        services.AddSingleton<JsonStructureParser>();
        services.AddSingleton<JsonLoader>();
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<JsonLoader>());
        services.AddSingleton<JsonSecretDetector>();

        // FormatDescriptor for label resolution and schema scripts
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<JsonLoader>();
            var analyzer = sp.GetRequiredService<JsonSecretDetector>();
            return new FormatDescriptor(
                JsonMediaTypes.Json,
                loader,
                analyzer,
                loader,
                new[] { "json", "jsonc", "json5", "jsonl", "ndjson" });
        });

        // Pipeline processors — modern pattern, same as CSV
        services.AddIndexingProcessor<JsonClassifier>();
        services.AddIndexingProcessor<JsonParser>();

        return services;
    }
}
```

In `RepoIndexerServiceCollectionExtensions`, `AddJsonFormat()` is called alongside other format registrations (before `AddPlainTextFormat()` which must remain last):

```csharp
services.AddCsvFormat();
services.AddJsonFormat();           // new
// ... other formats ...
services.AddPlainTextFormat();      // catch-all, always last
```

The `JsonClassifier` claims `.json` files during classification. The `JsonParser` runs during parsing, delegating to `JsonLoader`. AppSettings files are excluded by `JsonLoader.CanLoadAsync` returning `false` for `appsettings*.json`, so they fall through to `PlainTextParser` (or a future AppSettings pipeline processor). The FormatDescriptor registered here is used only for schema script discovery and label resolution — not for pipeline routing.

### Specific Handler Pattern

Future specific handlers (OpenAPI, JSON Schema, package.json) follow this pattern:

```csharp
// Classification: more specific classifier registered alongside generic
public sealed class OpenApiClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public async Task<(SemanticMediaType?, PipelineResult)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (!item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return await next(item);

        // Content sniff: check for "openapi" or "swagger" top-level key
        var preview = await ReadPreview(item, maxBytes: 4096, token);
        if (!HasOpenApiMarker(preview))
            return await next(item);

        return (JsonMediaTypes.OpenApi, PipelineResult.Success);
    }
}

// Parsing: specific parser wraps specific loader
public sealed class OpenApiParser(OpenApiLoader loader)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records?, PipelineResult)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (item.MediaType?.Kind != "api.openapi")
            return await next(item);

        // Loader uses JsonStructureParser for key tree, adds domain extraction
        var document = await _loader.LoadAsync(item, token);
        var records = _loader.Materialize(document);
        return (records, PipelineResult.Success);
    }
}
```

The specific classifier is registered BEFORE the generic `JsonClassifier`. Both run in the middleware chain. Because the specific classifier checks content before claiming (and returns early on match), it takes precedence — the generic classifier only runs for `.json` files that the specific classifier didn't claim. If the specific classifier doesn't match (no OpenAPI marker), it calls `next()` and the generic classifier claims the file.

The specific loader composes with `JsonStructureParser`: it calls `_parser.Parse()` for the generic key tree, then adds domain extraction. Materialization produces base key nodes (so `json_keys` works) plus domain nodes (so `api_endpoints` works). Composition, not inheritance.

**Fallback on domain failure:** If domain extraction throws, the specific handler catches the exception and falls back to materializing only the `JsonStructureParser` result. The file degrades to generic output instead of failing entirely.

### Line Number Tracking

`Utf8JsonReader` provides `TokenStartIndex` (byte offset from start of input) for every token. `JsonStructureParser` converts byte offsets to line numbers using a precomputed line-offset table.

**Critical: byte domain consistency.** `Parse(string text)` encodes the string to a `byte[]` via `Encoding.UTF8.GetBytes(text)` (which strips any BOM). This byte array is used for both:
1. The line-offset table (scanning for `0x0A` bytes)
2. The `Utf8JsonReader` input (via `new Utf8JsonReader(bytes)`)

Both operate in the same byte-offset domain, so `TokenStartIndex` values directly index into the line-offset table. For pure ASCII JSON (the vast majority), byte offsets equal character offsets. For multi-byte UTF-8 (common in i18n JSON files), the byte-domain approach is correct where a character-domain approach would produce wrong line numbers.

**Approach:**
1. Encode string to UTF-8 bytes.
2. Scan bytes for `0x0A`, recording byte offset of each line start: `[0, 45, 112, 178, ...]`.
3. During streaming parse, `TokenStartIndex` gives the byte offset for each token.
4. Binary search the line-offset array to find which line contains that byte offset. O(log n) per lookup.

**Cost:** One UTF-8 encode (allocation), one linear scan for newlines (O(n)), then O(log n) per key for line resolution. Total: O(n + k*log n). For a 10,000-line file with 500 keys, this is ~5,000 comparisons — negligible.

### Large Files

JSON data files (arrays of records, JSONL) may be megabytes or larger. The streaming parser handles this naturally.

**Detection:** `Utf8JsonReader` sees the first token. If it's `[` (start array), the parser reads the first `MaxSampleRecords` (default: 100) elements to detect shape (uniform objects vs mixed). JSONL files are detected by extension (`.jsonl`, `.ndjson`) and parsed line-by-line with the same sampling limit.

**Strategy:**
- Streaming parse with early-out — stop after N samples, never read the full file
- Estimate total record count from file size and average sample record size
- Produce a `document` node with shape metadata (field names, types, record count estimate)
- No child nodes per record — the graph stores shape, not data
- Query-time access via `json_data()` macro uses DuckDB's `read_json_auto()` for the full dataset

**Headline example:** `events.jsonl | data | 12 MB | ~150k tok | ~23,800 records | id, type, timestamp, payload`

**Memory:** Streaming parse means a 50MB data file uses the same memory as a 5KB config file. The UTF-8 byte array is allocated for the sampled portion only — for large files where early-out kicks in, the full file is read as a string by `FileContentReader` (unavoidable for digest computation) but the parser encodes only up to the sampling boundary.

### JSONC / JSON5 Support

Comments and relaxed syntax are handled by `JsonNormalizer` before `JsonStructureParser`:

```csharp
public static class JsonNormalizer
{
    public static void StripComments(byte[] utf8Bytes);              // In-place on UTF-8 bytes
    public static byte[] StripComments(string text);                 // Convenience: encode + normalize
}
```

**Byte-level space replacement, not removal.** The normalizer replaces comment bytes with `0x20` in-place, preserving `0x0A` (LF) bytes within block comments. This produces output with **identical byte count and identical newline positions** as the input. Consequences:

- `Utf8JsonReader.TokenStartIndex` values are valid in both original and normalized byte arrays — no offset translation needed
- The parser's line-offset table produces correct line numbers without modification — newlines haven't moved
- `JsonStructureParser.Parse(ReadOnlySpan<byte>)` (the byte overload from Plan 01) receives normalized bytes directly — no intermediate string
- `DocumentModel.Text` stores the original text; the normalized bytes are an internal detail of `LoadAsync`

Operating on bytes (not strings) is essential: a multi-byte UTF-8 character (e.g., `é` = 2 bytes) inside a comment must become 2 `0x20` bytes, not 1. String-level replacement would change the UTF-8 byte count and break `TokenStartIndex` alignment.

The normalizer must be a proper string-aware scanner, not a regex. It must distinguish comment syntax inside JSON string values from actual comments by tracking `0x22` (`"`) bytes:
- `"value // not a comment"` — the `//` is inside a string, do not replace
- `"value */ still a string"` — the `*/` is inside a string, not a block comment end
- Unterminated block comments at end of file — replace from `/*` to EOF with `0x20`

String detection operates on ASCII byte values (`0x22`, `0x5C`) which are unambiguous in UTF-8 — no multi-byte character contains these values. Implementation is a simple byte-scanner (~150 lines).

**Trade-off:** JSON5 normalization is non-trivial — trailing commas, unquoted keys, hex numbers, single-quote strings, and multiline strings all change byte length, not just content. Space replacement won't work for JSON5; it would require removal + source map (normalized offset → original offset → line number). JSONC (comment stripping only) ships first with the simpler space-replacement approach. JSON5 support can be added later with a source map without changing any existing interfaces.

### SQL Surface

`json_macros.sql` installed via `GetSchemaScripts()`:

```sql
-- Inventory: list all indexed JSON files with metadata
CREATE OR REPLACE MACRO json_files(pattern := NULL) AS TABLE (
    SELECT
        n.uri,
        a.headline,
        a.media_type,
        json_extract_string(n.properties, '$.shape') AS shape,
        json_extract(n.properties, '$.key_count')::INTEGER AS key_count,
        json_extract(n.properties, '$.max_depth')::INTEGER AS max_depth,
        a.byte_size,
        a.token_count
    FROM node n
    JOIN artifact a ON a.id = n.artifact_id
    WHERE n.kind = 'document'
      AND a.media_type LIKE 'application/json%'
      AND (pattern IS NULL OR matches_glob(n.uri, pattern))
    ORDER BY n.uri
);

-- Key structure: query keys across all JSON files
CREATE OR REPLACE MACRO json_keys(file_pattern := NULL, key_pattern := NULL) AS TABLE (
    SELECT
        doc.uri AS file_uri,
        key_node.uri AS key_uri,
        json_extract_string(key_node.properties, '$.path') AS path,
        json_extract_string(key_node.properties, '$.name') AS name,
        json_extract(key_node.properties, '$.depth')::INTEGER AS depth,
        json_extract_string(key_node.properties, '$.value_kind') AS value_kind,
        json_extract_string(key_node.properties, '$.scalar_value') AS value,
        json_extract(key_node.properties, '$.estimated_tokens')::INTEGER AS estimated_tokens,
        s.start_line,
        s.end_line
    FROM node doc
    JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
    JOIN node key_node ON key_node.id = e.destination_node_id AND key_node.kind = 'json_key'
    LEFT JOIN span s ON s.id = key_node.span_id
    WHERE doc.kind = 'document'
      AND (file_pattern IS NULL OR matches_glob(doc.uri, file_pattern))
      AND (key_pattern IS NULL OR json_extract_string(key_node.properties, '$.path') LIKE key_pattern)
    ORDER BY doc.uri, e.ordinal
);

-- Query-time data access for JSON data files
CREATE OR REPLACE MACRO json_data(uri) AS TABLE (
    SELECT * FROM read_json_auto(resolve_path(uri), maximum_object_size := 67108864)
);

-- Preview first N items from a JSON data file
CREATE OR REPLACE MACRO json_preview(uri, rows := 10) AS TABLE (
    SELECT * FROM read_json_auto(resolve_path(uri), maximum_object_size := 67108864) LIMIT rows
);
```

### Secret Detection

`JsonSecretDetector` implements `IFormatAnalyzer`. It examines `DocumentModel.Text` for values matching secret patterns.

**Patterns** (same approach as `AppSettingsLoader.ScanForSecrets`):
- Key names containing: `secret`, `password`, `token`, `apikey`, `api_key`, `connectionstring`
- Values matching: base64-encoded strings > 20 chars, strings starting with known prefixes (`sk-`, `ghp_`, `Bearer `)

**Output:** `Annotation` with `kind = "lint"`, `severity = "warning"`, `rule_id = "json.potential-secret"`, pointing to the line number of the value.

Runs on all JSON files regardless of kind. The `AppSettingsLoader` already does its own secret scanning for appsettings files — there's minor overlap, but it's harmless (same annotations, idempotent).

### Liquid Templates

Following the CSV template pattern:

**headline.liquid:**
```liquid
{{ file_name }} | {{ kind_label }} | {{ size_bytes | filesize }}{% if token_count > 0 %}, {{ token_count | tokens }}{% endif %} | {{ shape_label }}{% if top_keys.size > 0 %} | {% for key in top_keys limit:8 %}{{ key }}{% unless forloop.last %}, {% endunless %}{% endfor %}{% if top_keys.size > 8 %}, ...{% endif %}{% endif %}
```

**structure.liquid:**
```liquid
{% for key in keys -%}
{{ key.indent }}{{ key.name }}{% if key.type_label %} ({{ key.type_label }}{% if key.estimated_tokens > 0 %}, {{ key.estimated_tokens | tokens }}{% endif %}){% endif %}{% if key.scalar_value %}: {{ key.scalar_value }}{% endif %}    #{{ key.path }}
{% endfor -%}
```

---

## Cross-Cutting Concerns

### AppSettingsLoader Coexistence

`AppSettingsLoader` stays in `RepoQL.Formats.DotNet`. It doesn't use `JsonStructureParser` — it predates this design and has its own parsing. Coexistence works through `JsonLoader.CanLoadAsync` returning `false` for `appsettings*.json` and `launchSettings.json`, so those files fall through `JsonParser` and reach AppSettingsLoader's handling path.

Over time, `AppSettingsLoader` could be refactored to use `JsonStructureParser` for its key tree and migrated to the modern pipeline pattern (AppSettingsClassifier + AppSettingsParser). This is optional and doesn't block initial delivery.

### JSON Pointer Fragment Resolution

The read tool needs to resolve `file:///config.json#/database/host` into a specific subtree. `RepoUri.FromJsonPointer()` already exists.

At read time, the read tool:
1. Finds the artifact by URI (without fragment)
2. If a JSON Pointer fragment is present, parses the artifact's text content
3. Navigates to the pointer path
4. Returns the serialized subtree

This is a read-tool concern, not a format-loader concern. The loader's job is to produce spans with JSON Pointer paths in node URIs, so the read tool can locate them.

### Embedded JSON

`IFormatLoader.DiscoverEmbedsAsync()` exists but is not used by this design. Embedded JSON (in Markdown code blocks, string literals) is a broader composition concern. The JSON loader handles standalone files only. Embedded JSON support can be added later by having the Markdown loader's embed discovery invoke `JsonStructureParser` on detected JSON code blocks.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| `System.Text.Json` | Third-party parsers (Newtonsoft, etc.) | Zero dependencies, ships with .NET, `Utf8JsonReader` provides streaming parse with byte positions |
| `Utf8JsonReader` (streaming) | `JsonDocument` (DOM) | No memory allocation for the parse tree; byte positions for line tracking; early-out for large data files. Specific handlers that need DOM access use `JsonDocument` only for their domain extraction |
| UTF-8 byte array for both reader and line-offset | Building line-offset from string characters | `TokenStartIndex` reports byte offsets; character offsets diverge for multi-byte UTF-8. Same domain eliminates an entire class of bugs |
| Composition with `JsonStructureParser` | Inheritance from base loader class | Loaders have different registration patterns; utility composition is more flexible |
| Depth heuristic + per-file cap | Depth-only or query-time-only keys | Depth alone doesn't prevent bloat for flat files (i18n, lock files). The cap ensures bounded graph size while the full key tree stays in structure text |
| JSONC first, JSON5 later | Full JSON5 from day one | JSONC normalizer uses space replacement (~150 lines). JSON5 changes byte length (adding quotes, etc.) and requires removal + source map — substantial and diminishing returns |
| Byte-level space replacement for JSONC | Removal + source map | Replacing comment bytes with `0x20` in the UTF-8 byte array preserves byte count and `0x0A` positions. No source map, no offset translation. Uses Plan 01's byte-span overload directly. Source map approach is the fallback for future JSON5 |
| String-aware JSONC normalizer | Regex-based comment stripping | Regex can't distinguish `//` inside a JSON string from an actual comment. Must track string boundaries to avoid corrupting values |
| Separate `RepoQL.Formats.Json` project | Extending `RepoQL.Formats.DotNet` | JSON is not .NET-specific. The project should be usable for any repository |
| Modern pipeline pattern (Classifier + Parser) | FormatDescriptor-only (legacy path) | Follows the established pattern from CSV/XLSX/PDF/DOCX. Pipeline processors participate directly in the middleware chain with explicit ordering control |

## Alternatives Considered

**Query-time-only key tree (no key nodes):** `json_keys` could be a table macro that parses artifact content at query time using DuckDB's `json_extract`. This eliminates graph bloat entirely but sacrifices: (a) semantic search over keys, (b) span-based line number resolution, (c) edge relationships from keys. The hybrid approach (selected keys as nodes, full tree in structure text) is a better balance.

**Base class inheritance:** A `JsonLoaderBase : IFormatLoader, IFormatMaterializer` that specific loaders override. Rejected because the registration pattern and claim logic differ per handler. Composition with `JsonStructureParser` is simpler — specific handlers call it, they don't extend it.

**Single loader with kind dispatch:** One `JsonLoader` that handles all JSON kinds internally, dispatching to kind-specific extractors. Rejected because it violates the `FormatDescriptor` pattern (one descriptor per kind), makes testing harder, and centralizes what should be distributed.

**Generic classifier yields via `next()` for specific overrides:** The generic classifier would call `next()` first, and only return its own result if no downstream handler claims the file. This achieves "most specific wins" through middleware wrapping. Rejected because it requires all specific classifiers to be registered AFTER the generic one, which is fragile and harder to reason about. Instead, specific classifiers are registered BEFORE the generic one and claim files by returning early — the generic classifier only runs for files no specific classifier matched.

## Risks

| Risk | Mitigation |
|------|------------|
| Line number tracking inaccuracy | Both `Utf8JsonReader` and line-offset table operate on the same UTF-8 byte array, eliminating offset domain mismatch. JSONC normalizer operates at byte level, preserving byte count and newline positions — test with multi-byte characters in comments |
| Graph bloat for key-heavy files | Depth heuristic + per-file node cap (default: 200). `json_files()` includes `key_count` so agents see complexity before querying keys |
| Subtree token estimation reads full subtree | Forward-only reader must advance to EndObject/EndArray to compute byte span. Acceptable for config/schema files; large data files early-out after sampling |
| `read_json_auto` failures for edge-case JSON | `json_data()` macro sets `maximum_object_size` high. For truly pathological files, agents fall back to reading artifact content |
| Secret detection false positives | Conservative patterns. `severity = "warning"` not `"error"`. Agents treat as hints, not blockers |
| JSONC comment stripping — strings containing comment syntax | Normalizer is a byte-level scanner (not regex) that tracks `0x22` bytes for string boundaries. In-place `0x20` replacement preserves byte count and `0x0A` positions. Must handle escaped quotes within strings |
| AppSettings files claimed by generic classifier | `JsonLoader.CanLoadAsync` returns `false` for `appsettings*.json` / `launchSettings.json`. These fall through to existing handling. Test this exclusion explicitly |

## Extension Points

- `JsonStructureParser` — Available to any future JSON-related loader (OpenAPI, JSON Schema, package.json)
- `GetSchemaScripts()` — Specific handlers can add their own macros/views alongside the generic ones
- `SecretPatterns` — Pattern list is a static collection, easily extended
- `JsonParseOptions.MaxNodeDepth` and `MaxNodes` — Tunable per handler for different density needs
- Pipeline processors — New specific handlers register their own Classifier + Parser via `AddIndexingProcessor<T>()`
