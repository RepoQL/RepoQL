# Plan: Wire Format Analyzers into Production

Implements: [Design §1 — Wire Format Analyzers](../../designs/current/unresolved-ref-detection.md#1-wire-format-analyzers-into-production)

## Scope

**Covers:**
- Move `FormatRegistryAnalyzer` from test code to production
- Register it as `IAsyncPipeline<IParsedArtifact, Annotation[]>` in DI
- Rename `markdown/broken-link` rule ID to `markdown/unresolved-ref`
- Update existing tests to use new rule ID

**Does not cover:**
- Cross-document REFERS_TO edges (Plan: 02)
- Cross-document reference resolver (Plan: 03)
- New analyzers for other formats

## Enables

Once format analyzers run in production:
- `MarkdownAnalyzer` detects unresolved local anchors on every indexed markdown file
- `CSharpAnalyzer`, `CsProjAnalyzer`, `JsonSecretDetector`, `GraphQLAnalyzer`, `MermaidAnalyzer` — all start producing annotations in production
- Annotations are queryable: `SELECT * FROM Annotations WHERE rule_id = 'markdown/unresolved-ref'`
- `.editorconfig` severity overrides take effect
- **Plan 03** can build on local anchor detection being live

This is the highest-value increment. One registration line unblocks every format analyzer that has been sitting unused in production.

## Prerequisites

- None — this is the first increment

## North Star

Every `IFormatAnalyzer` registered in a `FormatDescriptor` produces annotations in production, not just in tests.

## Done Criteria

### FormatRegistryAnalyzer

- The `FormatRegistryAnalyzer` shall be in `RepoQL.Core` (or `RepoQL.Indexing`), not `RepoQL.Testing`
- The `FormatRegistryAnalyzer` shall implement `IAsyncPipeline<IParsedArtifact, Annotation[]>`
- When an item has a `MediaType` that resolves to a `FormatDescriptor` with a non-null `Analyzer`, the `FormatRegistryAnalyzer` shall call `Analyzer.AnalyzeAsync` with the stashed `DocumentModel`
- When the `DocumentModel` stash is missing, the `FormatRegistryAnalyzer` shall return an empty array
- When the `Analyzer.AnalyzeAsync` throws, the `FormatRegistryAnalyzer` shall log the exception and return an empty array
- The `FormatRegistryAnalyzer` shall map each `AnalysisResult` to an `Annotation` with these field mappings:
  - `AnalysisResult.SemanticKey` → `Annotation.SemanticKey`
  - `AnalysisResult.Kind` → `Annotation.Kind`
  - `AnalysisResult.Severity` → `Annotation.Severity` (enum to string: `Warning` → `"warning"`, `Error` → `"error"`, `Info` → `"info"`, `Hint` → `"hint"`)
  - `AnalysisResult.RuleId` → `Annotation.RuleId`
  - `AnalysisResult.Source` → `Annotation.Source`
  - `AnalysisResult.Message` → `Annotation.Message`
  - `AnalysisResult.Target.NodeId` → `Annotation.TargetNodeId`
  - `AnalysisResult.Target.SpanId` → `Annotation.TargetSpanId`
  - `AnalysisResult.Target.TargetUri` → `Annotation.TargetUri`
  - `AnalysisResult.Data` → `Annotation.Data`
- When the `AnalysisResult.Severity` is `None`, the result shall be skipped (not mapped to an annotation)

### DI Registration

- The `FormatRegistryAnalyzer` shall be registered as `IAsyncPipeline<IParsedArtifact, Annotation[]>` in `RepoIndexerServiceCollectionExtensions`
- The `SingleFileAnalysisPipeline` shall receive the `FormatRegistryAnalyzer` via `GetServices<IAsyncPipeline<IParsedArtifact, Annotation[]>>()`

### Rule ID Rename

- The `MarkdownAnalyzer` shall use `markdown/unresolved-ref` as its `RuleId` constant
- The `MarkdownAnalyzer` shall use `markdown/unresolved-ref` in `SemanticKey` generation
- When the `markdown/unresolved-ref` rule severity is `None` in `.editorconfig`, the `MarkdownAnalyzer` shall emit no annotations

### Tests

- When a markdown file has a broken local anchor, indexing shall produce an annotation with `rule_id = 'markdown/unresolved-ref'` and `kind = 'lint'`
- When a markdown file has all valid local anchors, indexing shall produce no `unresolved-ref` annotations
- When `FormatRegistryAnalyzer` receives an item with no `DocumentModel` stash, it shall return an empty array without throwing
- When the format analyzer throws, `FormatRegistryAnalyzer` shall log and return an empty array

## Constraints

- **No new DI patterns** — follow existing `AddIndexingProcessor` / `GetServices` pattern
- **Hot-path annotation flow** — these annotations flow through `item.AnnotationsList` → `IndexingCommitter`, not through `AnnotationResultWriter`. The `IndexingCommitter` merges them with parser annotations at commit time
- **Annotation source strings** — each `IFormatAnalyzer` implements `IAnnotationSourceProvider` to declare its source string (e.g., `"RepoQL.Markdown"`). The `FormatRegistryAnalyzer` must propagate this source onto each mapped `Annotation.Source` for correct scoped replacement on reindex
- **Test framework** — TUnit, AwesomeAssertions, FakeItEasy

## References

- [Design](../../designs/current/unresolved-ref-detection.md) — §1 Wire Format Analyzers
- [Single-file analysis flow](../../flows/current/indexing/single-file-analysis.md) — pipeline mechanics
- `src/RepoQL.Testing/IndexedRepoBuilder.cs` — existing `FormatRegistryAnalyzer` implementation (lines 562-650)
- `src/Formats/RepoQL.Formats.Markdown/MarkdownAnalyzer.cs` — analyzer being unblocked
- `src/Indexing/RepoQL.Indexing/Indexing/ServiceCollectionExtensions/RepoIndexerServiceCollectionExtensions.cs` — DI registration site

## Error Policy

Analyzer exceptions must not prevent indexing. Log the exception, return empty annotations, continue. One bad analyzer never blocks another.
