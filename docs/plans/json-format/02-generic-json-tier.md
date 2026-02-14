---
description: Plan for JSON format — pipeline integration, loader, templates, SQL macros, and DI registration
tags: [format, json, plan, loader, pipeline, sql, templates]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: JSON — Generic JSON Tier

Implements: [JSON Format Design](../../designs/json-format.md) — Pipeline Integration, JsonLoader, Registration, SQL Surface, Liquid Templates

## Scope

**Covers:**
- `JsonClassifier` — classification pipeline processor
- `JsonParser` — parsing pipeline processor (wraps JsonLoader)
- `JsonLoader` — `IFormatLoader` + `IFormatMaterializer` + `IFormatSchemaProvider`
- `JsonMediaTypes` — media type constants
- `JsonServiceCollectionExtensions.AddJsonFormat()` — DI registration
- Liquid templates: `headline.liquid`, `structure.liquid`, `summary.liquid`
- SQL macros: `json_files()`, `json_keys()`, `json_data()`, `json_preview()`
- AppSettings exclusion in `CanLoadAsync`
- Integration into `RepoIndexerServiceCollectionExtensions`
- Integration and unit tests

**Does not cover:**
- `JsonStructureParser` (prerequisite — Plan: 01-structure-parser)
- JSONC/JSON5 normalization (Plan: 03-jsonc-support)
- Secret detection analyzer (Plan: 04-secret-detection)
- Specific JSON handlers: OpenAPI, JSON Schema, package.json (future plans)

## Enables

Once this exists:
- **No JSON file is plain text** — every `.json` file gets a headline with shape, top keys, and token count; structure with key tree and pointer addresses; queryable key nodes in the graph
- **`json_files()`** — agents can inventory all JSON files with metadata
- **`json_keys()`** — agents can query key structure across all JSON files
- **`json_data()` / `json_preview()`** — agents can query JSON data files at runtime via DuckDB's `read_json_auto`
- **Plan 03 can proceed** — JSONC support extends `JsonLoader.LoadAsync` with normalization
- **Plan 04 can proceed** — secret detection fills the analyzer slot in the `FormatDescriptor`
- **Future specific handlers** can register their own classifiers before `JsonClassifier` to claim specific JSON kinds

This is the "floor" increment. After this, the generic tier works. All subsequent plans raise the ceiling.

## Prerequisites

- Plan 01 (JsonStructureParser) complete and passing tests
- `CsvClassifier` + `CsvParser` + `CsvLoader` + `CsvServiceCollectionExtensions` as the reference pattern — read these before implementing
- `ITemplateRenderer` available via DI for Liquid template rendering

## North Star

Index a repository. Run `explore(uriGlob="file:///**/*.json")`. See meaningful headlines for every JSON file — shape, top keys, token count. Run `json_files()` and see the full inventory. Run `json_keys(key_pattern := '%version%')` and find every version key across all JSON files. Never open a file to understand what it contains.

## Done Criteria

### JsonClassifier
- The `JsonClassifier` shall implement `IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>`
- When the file extension is `.json`, `.jsonc`, `.json5`, `.jsonl`, or `.ndjson`, the classifier shall return `JsonMediaTypes.Json`
- When the extension does not match, the classifier shall call `next(item)`
- The classifier shall perform extension checks case-insensitively

### JsonParser
- The `JsonParser` shall implement `IAsyncPipeline<IClassifiedArtifact, Records?>`
- When the media type kind does not start with `json`, the parser shall call `next(item)`
- When the media type kind matches, the parser shall call `JsonLoader.CanLoadAsync`
  - If `CanLoadAsync` returns false, the parser shall call `next(item)`
  - If `CanLoadAsync` returns true, the parser shall call `LoadAsync` then `Materialize`
- When `LoadAsync` or `Materialize` throws, the parser shall log the error and return `(null, PipelineResult.Error)`
- The parser shall store the `DocumentModel` in `item["document_model"]` for downstream analysis

### JsonLoader — CanLoadAsync
- `CanLoadAsync` shall return true for `.json`, `.jsonl`, `.ndjson` extensions
- `CanLoadAsync` shall return false for `.jsonc` extensions (until Plan 03 adds JSONC normalization — these fall through to PlainTextParser rather than failing with `JsonException`)
- `CanLoadAsync` shall return false for `.json5` extensions (until JSON5 normalization is implemented — these fall through to PlainTextParser)
- `CanLoadAsync` shall return false for files whose **filename** (not full path) matches `appsettings*.json` or `launchSettings.json` (case-insensitive)
- All extension and filename checks shall be case-insensitive

### JsonLoader — LoadAsync
- `LoadAsync` shall read file content via `FileContentReader.ReadAllTextWithDigestAsync()`
- `LoadAsync` shall call `JsonStructureParser.Parse()` on the text
- `LoadAsync` shall store the `JsonParseResult` in `DocumentModel.Metadata`
- When `JsonStructureParser.Parse()` throws `JsonException`, `LoadAsync` shall re-throw (the `JsonParser` handles the error)

### JsonLoader — Materialize
- `Materialize` shall retrieve `JsonParseResult` from `DocumentModel.Metadata`
- `Materialize` shall render headline, summary, and structure via Liquid templates
- `Materialize` shall create an `Artifact` with:
  - `Headline`, `Summary`, `Structure` from rendered templates
  - `StoreUri` from `document.Uri`
  - `ByteSize` from text byte length
  - `TokenCount` from text length / 4 estimate
  - `MediaType` from document media type
  - `Content` from document text
- `Materialize` shall create a `document` node with properties: `shape`, `key_count`, `max_depth`
- `Materialize` shall create child nodes for keys where `IsNodeEligible` is true
  - Each child node shall have kind `json_key`
  - Each child node shall have properties: `path`, `name`, `depth`, `value_kind`, `scalar_value`, `estimated_tokens`
- `Materialize` shall create a `Span` for each node with `StartLine` and `EndLine`
- `Materialize` shall create `HAS_PART` edges from document to child nodes with ordinal matching key order
- `Materialize` shall return a `Records` containing all artifacts, nodes, edges, and spans

### Liquid Templates
- `headline.liquid` shall produce output matching the format: `{filename} | json | {size} | {shape} | {top_keys}`
- `structure.liquid` shall produce indented key tree with type labels, token estimates, scalar values, and JSON Pointer addresses as `#/path` suffixes
- `summary.liquid` shall produce a brief description: kind, shape, key count, max depth
- Templates shall be embedded resources in the `Templates/explore/` directory

### SQL Macros
- `json_files(pattern)` shall return all JSON document nodes with headline, media_type, shape, key_count, max_depth, byte_size, token_count
  - When pattern is provided, results shall be filtered by glob match on URI
  - When pattern is null, all JSON files shall be returned
- `json_keys(file_pattern, key_pattern)` shall return key nodes with file_uri, key_uri, path, name, depth, value_kind, value, estimated_tokens, start_line, end_line
  - When file_pattern is provided, results shall be filtered by glob match on document URI
  - When key_pattern is provided, results shall be filtered by LIKE match on path
- `json_data(uri)` shall pass the URI through `resolve_path()` to `read_json_auto()`
- `json_preview(uri, rows)` shall return the first N rows from `json_data()`
- `GetSchemaScripts()` shall return the SQL macros as `FormatSqlScript` entries

### Registration
- `AddJsonFormat()` shall register `JsonStructureParser` and `JsonLoader` as singletons
- `AddJsonFormat()` shall register a `FormatDescriptor` with `JsonMediaTypes.Json`, the loader, a `NullAnalyzer(JsonMediaTypes.Json)` for the analyzer slot (Plan 04 replaces this with `JsonSecretDetector`), materializer, and labels `["json", "jsonc", "json5", "jsonl", "ndjson"]`
- `AddJsonFormat()` shall register `JsonClassifier` and `JsonParser` via `AddIndexingProcessor<T>()`
- `AddJsonFormat()` shall register `JsonLoader` as `IFormatSchemaProvider`
- In `RepoIndexerServiceCollectionExtensions`, `AddJsonFormat()` shall be called before `AddPlainTextFormat()`

### Integration Tests
- When a directory containing `.json` files is indexed, all JSON files shall have artifacts with non-empty headlines
- When a JSON file has top-level keys, `json_keys()` shall return those keys with correct paths and line numbers
- When a flat JSON object is indexed, its headline shall show shape as "object" and list top keys
- When a JSON array file is indexed, its headline shall show shape as "array" with record count estimate
- When an `appsettings.json` file is present, `JsonLoader.CanLoadAsync` shall return false for it

## Constraints

- **Follow CSV pattern** — `JsonClassifier`, `JsonParser`, `JsonServiceCollectionExtensions` should mirror `CsvClassifier`, `CsvParser`, `CsvServiceCollectionExtensions` structurally. Deviate only where JSON semantics require it
- **No JSONC handling yet** — `LoadAsync` passes text directly to `JsonStructureParser.Parse()`. JSONC normalization is Plan 03's scope. `.jsonc` files that fail strict JSON parsing will get `PipelineResult.Error` until Plan 03 is delivered
- **No JSON5 yet** — `CanLoadAsync` returns false for `.json5` extensions. These fall through to PlainTextParser (current behavior). JSON5 normalization is deferred per design trade-offs
- **No secret detection yet** — the `FormatDescriptor` uses `NullAnalyzer(JsonMediaTypes.Json)` for the analyzer slot. Plan 04 replaces this with `JsonSecretDetector`
- **AppSettings exclusion, not competition** — `CanLoadAsync` returns false for AppSettings filenames. Do not attempt to override or coexist at the pipeline level

## References

- [JSON Format Design](../../designs/json-format.md) — Pipeline Integration, JsonLoader, Registration, SQL Surface, Templates
- [JSON North Star](../../north-star/json.md) — progressive disclosure, query surface, format essence
- [JSON Indexing Flow](../../flows/future/indexing/json-indexing.md) — classification → parsing → materialization → commit
- `src/Formats/RepoQL.Formats.Csv/CsvClassifier.cs` — reference classifier implementation
- `src/Formats/RepoQL.Formats.Csv/CsvParser.cs` — reference parser implementation
- `src/Formats/RepoQL.Formats.Csv/CsvLoader.cs` — reference loader: templates, schema scripts, materialization
- `src/Formats/RepoQL.Formats.Csv/CsvServiceCollectionExtensions.cs` — reference registration
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline integration conventions
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

A malformed JSON file must not prevent other files from indexing. When `JsonStructureParser.Parse()` throws:
1. `JsonParser` catches the exception and returns `(null, PipelineResult.Error)`
2. The file continues through the pipeline — the commit phase records it with an error status
3. The file is still searchable by name/path but has no structural nodes

Do not silently swallow errors. Log at Error level with the file URI and exception message.
