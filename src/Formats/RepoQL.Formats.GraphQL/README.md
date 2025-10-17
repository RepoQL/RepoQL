# RepoQL.Formats.GraphQL

GraphQL format handler design for RepoQL. This document captures the intended behaviour for the loader, analyzer, and materializer that will be implemented against the Antlr4 grammar in `GraphQL.g4`.

## Format Overview

- Media type: `text/graphql;kind=graphql.doc`
- Recognised labels/extensions: `.graphql`, `.graphqls`, `.gql`, `.gqls`
- Loader parses query documents _and_ type system SDL (schema definition language) using the Antlr-generated lexer/parser.
- Materializer emits graph nodes for operations, fragments, type definitions, enum values, directives, and field selections.
- Analyzer provides lint-style diagnostics for common GraphQL correctness and hygiene issues.
- X‑ray headline/summary/structure are rendered via Liquid templates embedded in `Templates/xray`.

## Loader Responsibilities

`GraphQLLoader` will implement `IFormatLoader` and `IFormatMaterializer`.

- **Detection**
  - Immediate accept for known extensions.
  - Falls back to lightweight sniffing (looks for leading `query`, `mutation`, `subscription`, `fragment`, or SDL keywords such as `type`, `schema`, `scalar`).
  - Sets `DiscoveredArtifact.MediaType` to `text/graphql;kind=graphql.doc`.

- **Parsing**
  - Uses the Antlr4 runtime with the shipped grammar to obtain a parse tree (`GraphQLParser.DocumentContext` or `TypeSystemDefinitionOrExtension` depending on content).
  - Converts the raw Antlr tree into RepoQL-friendly POCOs housed under `RepoQL.Formats.GraphQL.Syntax`, normalising spans into `DocumentSpan`.
  - Captures syntax errors and surfaces them in loader diagnostics so callers can choose to stop indexing invalid documents.

- **Document state (`GraphQLDocumentState`)**
  - `DocumentId`, `Digest`, `Size`, `MediaType`, `StoreUri`.
  - `Operations`: list of operations with kind (query/mutation/subscription), optional name, variable definitions (name/type/default), directives, top-level selections, and span metadata.
  - `Fragments`: list with name, type condition, directives, referenced fragments/types, and spans.
  - `SchemaTypes`: grouped by kind (object/interface/input/enum/scalar/union), each carrying description text, implemented interfaces, field definitions, enum values, and spans.
  - `Directives`: per-definition data including repeatable flag, arguments, locations.
  - Indexes for fast lookups: `OperationByName`, `FragmentByName`, `TypeByName`, `EnumByName`.
  - `References`: lightweight adjacency lists describing operation → fragment usages, fragment → fragment usages, field selections → type references, variable → definition mapping.
  - Stored in `DocumentModel.Metadata["graphql.state"]`. Sliced helper maps (e.g., `graphql.fragmentIndex`) are added for analyzers needing only a subset.

- **Syntax tree**
  - The `DocumentModel.SyntaxTree` property will be set to the custom root AST (`GraphQLDocument`) so downstream analyzers can reuse it.

- **DiscoverEmbedsAsync**
  - Returns an empty result; GraphQL documents do not embed secondary formats.

## Materialization

The materializer produces RepoQL graph artefacts and X‑ray strings.

- **Artifacts**
  - Populates headline/summary/structure via Liquid templates hosted in `Templates/xray`.
  - Headline highlights document kind, operation counts, fragment counts, and the first few type definitions while omitting null/zero values so the line stays signal-rich.
  - Summary surfaces the elements most likely to require human attention (operations, fragments, schema entry points) in <20 lines to help answer “do I need to read this?”.
  - Structure outputs a detailed outline (operations with top-level fields, fragments with type conditions, types with field signatures) with no hard cap but targets <100 lines to make the raw document optional reading.

- **Nodes & edges**
  - Root `document` node (`props.media_type`, `props.operation_count`, etc.).
  - Child nodes with `HAS_PART` composition edges:
    - `graphql.operation` (props: name, kind, variable_count, directive_count).
    - `graphql.fragment` (props: name, type_condition, directive_count).
    - `graphql.type` / `graphql.interface` / `graphql.input_type` / `graphql.union` / `graphql.scalar`.
    - `graphql.field` nodes for schema fields (props: name, type, argument_count, deprecation_reason?).
    - `graphql.enum_value` nodes with deprecation info.
    - `graphql.directive` nodes for directive definitions.
  - Cross-document edges:
    - `REFERS_TO` from operations/fragments to fragments they spread or types they reference.
    - `USES_VARIABLE` edges from field selections back to their variable definition node (created under the operation) with span metadata.
    - `IMPLEMENTS` edges from object/interface types to implemented interfaces.
  - Every node backed by a `Span` derived from Antlr token intervals so UI consumers can jump to source.

- **Metadata exports**
  - Headline tags include detected schema capabilities (query/mutation/subscription root types) and presence of directives (e.g., `@deprecated` usage count).
  - Structured JSON props keep numeric counts for quick dashboarding.

## X-ray Templates

Templates mirror existing formats and live under `Templates/xray/{headline,summary,structure}.liquid`.

Model keys made available:

| Key | Description |
| --- | --- |
| `file_name` | Last segment of the document URI |
| `size_bytes` | Raw byte size |
| `media_kind` / `media_base` | Media type info |
| `operation_stats` | Breakout of queries/mutations/subscriptions (excluding zero-count entries) |
| `operations` | Array of `{ name, kind, variable_count, top_fields }` |
| `fragments` | Array of `{ name, type_condition, referenced_count }` |
| `schema_types` | Array grouped by kind with per-type field counts |
| `directive_stats` | Totals for directive definitions/usages (non-zero entries only) |
| `enum_values` | Flattened list capped for structure output |
| `has_schema_definition` | Flag indicating SDL root presence |

Template guidance:

- **Headline**: single line, drop null/empty/zero values, prioritise cues that aid repository-wide comprehension (e.g., `queries:3 fragments:5 types:4` → omit counts that are 0).
- **Summary**: limit to fewer than 20 lines; focus on the handful of elements that determine whether deeper reading is necessary (e.g., list named operations, notable fragments, schema entry points, major directive usage).
- **Structure**: provide as much detail as required (goal <100 lines) so consumers can understand the document without opening it—include operations with their selections, fragment spreads, and complete type/field inventories.

## Analyzer Rules

The analyzer (`GraphQLAnalyzer`) will expose a handful of best-practice checks. Rules follow the `graphql/*` namespace and read configuration via `.editorconfig`.

1. **`graphql/named-operation`** (Warning)  
   Enforces named operations when:
   - The operation type is `mutation` or `subscription`, or
   - The document defines multiple operations.  
   Diagnostic spans cover the `operationDefinition`. Suggested fix: insert `operation_name`.

2. **`graphql/unused-fragment`** (Warning)  
   Detects fragments that are never spread by any operation/fragment in the same workspace. Uses `AnalyzerContext.Workspace` + `FormatRegistry` to resolve sibling GraphQL documents when `#import` comments or file co-location imply multi-file modules.

3. **`graphql/undefined-fragment`** (Error)  
   Flags spreads that reference fragments not resolvable in the current document or workspace (helps catch typos / stale includes).

4. **`graphql/undefined-variable`** (Error)  
   Ensures every `$variable` used in a selection set has a matching definition in the enclosing operation. Also checks that default values respect required-ness (non-null variables must be supplied or have defaults).

5. **`graphql/missing-description`** (Suggestion)  
   Encourages descriptions on schema types, fields, and enum values (skipped for introspection types and exceptions configured via rule properties).

6. **`graphql/duplicate-definition`** (Error)  
   Surfaces multiple definitions for the same operation/fragment/type within a document.

Diagnostics reuse span data from the AST and call `AnalyzerSettings` for severity overrides. Fixes are provided where straightforward (e.g., stub description insertion).

## Registration

`RepoIndexerServiceCollectionExtensions` will register:

```csharp
services.AddSingleton<GraphQLLoader>();
services.AddSingleton<GraphQLAnalyzer>();
services.AddSingleton<IFormatRegistry>(sp =>
{
    var graphQlLoader = sp.GetRequiredService<GraphQLLoader>();
    var graphQlAnalyzer = sp.GetRequiredService<GraphQLAnalyzer>();
    // …
    new FormatDescriptor(
        SemanticMediaType.Create("text", "graphql").WithKind("graphql.doc"),
        graphQlLoader,
        graphQlAnalyzer,
        graphQlLoader,
        labels: new[] { "graphql", "gql" })
});
```

This keeps the format behind a simple label lookup (`graphql`/`gql`) so Markdown fenced code blocks or future embedders can delegate to it.

## Testing Strategy

- **Parser snapshot tests**: Feed canonical GraphQL spec examples (queries + SDL) to the loader and assert stable `GraphQLDocumentState`.
- **X-ray rendering**: Golden-file tests covering mix of operations/fragments/types to ensure the Liquid templates stay deterministic.
- **Analyzer tests**: Unit tests per rule and integration tests that invoke the analyzer via `FormatRegistry` using in-memory workspaces to mimic multi-file fragment resolution.
- **Round-trip fixture**: Add a small GraphQL schema + operation bundle under `Formats/tests` to validate indexing from discovery → load → materialize → analyze.

## Open Questions / Future Enhancements

- **Schema validation**: Hook into external schema (e.g., introspection JSON) to validate field selections; out-of-scope for first iteration.
- **Inline metrics**: Consider computing selection depths or node counts for complexity budgeting.
- **Embedded SDL**: Some teams embed SDL in string literals inside code files; long-term we might expose a helper to parse those using the same loader.

This README will guide the upcoming implementation so behaviour stays consistent across loader, materializer, templating, and analyzer layers.
