---
description: Plan for SARIF import foundation — general-purpose source-wide annotation replacement and pure SARIF normalizer
tags: [sarif, annotations, normalization, plan, duckdb]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: SARIF Foundation — Annotation Write Method + Normalizer

Implements: [SARIF Import Design](../designs/future/sarif-import.md) — ReplaceAnnotationsBySource, SarifNormalizer, output model types, project scaffolding

## Scope

**Covers:**
- `ReplaceAnnotationsBySource` method on `DuckDbDataStoreExtensions` — general-purpose source-wide annotation replacement in one transaction (delete expired by semantic key, write spans, upsert annotations)
- `AnnotationReplaceResult` record
- New project `src/RepoQL.Sarif/` with `ISarifNormalizer` interface and `SarifNormalizer` implementation — pure function: SARIF `JsonDocument` + repo root path → `NormalizationResult`
- Normalization sub-components: `PathNormalizer`, `RuleCollector`, `SeverityResolver`, `SourceIdentifier`
- Output model types: `NormalizationResult`, `NormalizedRun`, `NormalizedResult`, `NormalizedRegion`
- `ProducerMap` — known producer name-to-slug table (data, not switch statement)
- New test project `src/tests/RepoQL.Sarif.Tests/`
- Normalizer unit tests with real SARIF fixture files (at minimum Snyk Code, CodeQL, ESLint)
- `ReplaceAnnotationsBySource` tests in `src/tests/RepoQL.Data.DuckDB.Tests/`
- Solution file updated to include new projects

**Does not cover:**
- `SarifImportService` orchestrator (Plan: sarif-02-import-service)
- `sarif://` scheme routing in gRPC host or MCP import tool (Plan: sarif-02-import-service)
- `help://` documentation (Plan: sarif-03-documentation)
- Consumer code that calls `ReplaceAnnotationsBySource` (Plan: sarif-02-import-service)

## Enables

- Plan 2 can wire the normalizer to the import service and call `ReplaceAnnotationsBySource` for writes
- Any future bulk annotation producer (test result importers, coverage importers, architecture violation checkers) can use `ReplaceAnnotationsBySource` without SARIF dependency
- Normalizer is independently testable against real SARIF fixtures before integration wiring exists

## Prerequisites

None. This is the foundation.

## North Star

The normalizer absorbs all producer-specific variance in one place. Downstream code never sees a uriBaseId, never resolves a severity cascade, never parses a Qodana extension rule. Adding support for a new producer is: add a fixture file, possibly add an entry to `ProducerMap`, fix any failing normalizer test. No changes to the import service or write layer.

## Done Criteria

### ReplaceAnnotationsBySource

- The method shall live in `src/RepoQL.Data.DuckDB/DuckDbDataStoreExtensions.cs` as a public extension method on `DuckDbDataStore`
- The method shall accept `string source`, `string kind`, `IReadOnlyList<Annotation> annotations`, `IReadOnlyList<Span> spans`
- The method shall return `AnnotationReplaceResult` with `Inserted`, `Updated`, `Expired` counts
- Within a single `WriteTransaction`, the method shall: (1) collect new semantic keys, (2) delete annotations matching source+kind whose semantic_key is NOT IN the new set, (3) write spans via bulk appender, (4) upsert annotations using semantic_key conflict resolution
- For large key sets (over 1000), the method shall use a temporary table instead of an IN-list
- A test shall verify that new annotations are inserted when none exist for the source
- A test shall verify that stale annotations (same source+kind, key not in new set) are deleted
- A test shall verify that unchanged annotations (same semantic_key, same data) are not duplicated
- A test shall verify idempotent re-write: same input twice produces zero net changes on second call
- A test shall verify empty annotations list deletes all existing annotations from that source+kind
- A test shall verify that annotations from a different source are untouched

### SarifNormalizer

- `ISarifNormalizer` interface and `SarifNormalizer` class shall live in `src/RepoQL.Sarif/`
- The normalizer shall accept `JsonDocument sarif` and `string repoRootPath`
- The normalizer shall return `NormalizationResult` with `Runs`, `SkippedResults`, `Warnings`
- The normalizer shall validate the SARIF envelope: `version` must be `"2.1.0"`, `runs` must be non-null and non-empty, each run must have `tool.driver.name`
- When envelope validation fails, the normalizer shall return a `NormalizationResult` with zero runs and the failure described in `Warnings` — the normalizer never throws. It is the import service's responsibility (Plan 02) to inspect the result and throw a fatal error for envelope failures

### PathNormalizer

- The path normalizer shall strip `file:///` scheme prefixes
- The path normalizer shall resolve known uriBaseId values (`%SRCROOT%`, `SRCROOT`, `ROOTPATH`) to repo root
- The path normalizer shall resolve `run.originalUriBaseIds` when present (takes precedence over conventions)
- The path normalizer shall relativize absolute paths against the repo root
- The path normalizer shall normalize separators (backslash to forward slash) and strip leading slashes
- The path normalizer shall URL-decode encoded characters (`%20` to space)
- Unresolvable paths (outside repo, unknown scheme) shall be preserved as-is and flagged in warnings
- A test shall verify Snyk Code paths: `routes/index.js` + `%SRCROOT%` → `routes/index.js`
- A test shall verify sonar-tools paths: `file:///src/main/Foo.java` → `src/main/Foo.java`
- A test shall verify Roslyn absolute paths: `file:///C:/source/repos/Foo.cs` relativized against repo root
- A test shall verify backslash normalization: `src\Auth\Foo.cs` → `src/Auth/Foo.cs`

### RuleCollector

- The rule collector shall collect rules from `tool.driver.rules[]`
- The rule collector shall collect rules from `tool.extensions[].rules[]` and merge into a unified lookup
- Driver rules shall take precedence over extension rules on collision
- Missing rules arrays shall not produce errors
- A test shall verify Qodana pattern: empty driver rules, rules on extensions → all rules found
- A test shall verify sonar-tools pattern: no rules array → empty lookup, no error

### SeverityResolver

- The severity resolver shall use the cascade: `result.level` > `rule.defaultConfiguration.level` > `"warning"`
- The severity resolver shall extract tool-specific severity (ideaSeverity, CVSS, SonarQube severity) into the `Data` payload
- A test shall verify explicit result level is used when present
- A test shall verify rule default level is used when result level is absent
- A test shall verify `"warning"` default when both are absent

### SourceIdentifier

- The source identifier shall map known producer names to slugs using `ProducerMap`
- Unknown names shall be slugified: strip non-alphanumeric, lowercase, collapse whitespace to hyphens
- A test shall verify known producers: `SnykCode` → `snyk-code`, `QDJVM` → `qodana-jvm`, `CodeQL command-line toolchain` → `codeql`
- A test shall verify unknown producer: `"My Custom Linter v3.2"` → `my-custom-linter-v3-2`

### Result Normalization

- Each result shall produce a `NormalizedResult` with `RuleId`, `Message`, `Level`, `NormalizedPath`, `Region`, `PartialFingerprints`, `Fingerprints`, `RuleMetadata`, `Data`
- Results missing a message (after the full fallback chain: `text` → `markdown` → `message.id` resolved against rule `messageStrings`) shall be skipped and counted in `SkippedResults`
- Results missing a `ruleId` shall be skipped and counted in `SkippedResults`
- `PartialFingerprints` shall carry the SARIF `partialFingerprints` dictionary (nullable)
- `Fingerprints` shall carry the SARIF `fingerprints` dictionary (nullable)
- The two dictionaries shall be kept separate so the import service (Plan 02) can apply priority: partialFingerprints > fingerprints > content hash
- `Region` shall normalize to `{ StartLine, StartColumn?, EndLine?, EndColumn? }` — `charOffset`/`charLength` dropped. Column values are stored as-is from SARIF (1-based per spec). `endColumn` exclusivity is inherited from SARIF convention
- A test shall verify multi-run SARIF: file with 2 runs from different tools → 2 `NormalizedRun` entries with correct source slugs
- A test shall verify malformed result skipping: result without message is skipped, subsequent results still processed

### Project Structure

- `src/RepoQL.Sarif/RepoQL.Sarif.csproj` shall target the same framework as other projects and reference `RepoQL.Contracts` only
- `src/tests/RepoQL.Sarif.Tests/RepoQL.Sarif.Tests.csproj` shall reference `RepoQL.Sarif`, `RepoQL.Data.DuckDB`, `RepoQL.Testing`
- Both projects shall be added to `RepoQL.sln`
- SARIF fixture files shall live in `src/tests/RepoQL.Sarif.Tests/Fixtures/` and be copied to output

## Constraints

- **No Microsoft SARIF SDK** — `System.Text.Json` `JsonDocument` DOM access is sufficient and avoids a heavy transitive dependency
- **No `System.Text.Json` source generators** — use `JsonDocument` for maximum flexibility with varied SARIF structures
- **Frozen schema** — `ReplaceAnnotationsBySource` uses existing `annotation` and `span` tables, no schema changes
- **TUnit + AwesomeAssertions** for all tests
- **`RepoQL.Sarif` has no DuckDB dependency in this plan** — the import service (Plan 2) adds it

## References

- [SARIF Import Design](../designs/future/sarif-import.md) — architecture, contracts, trade-offs
- [SARIF Normalization Flow](../flows/future/sarif-normalization.md) — the producer gauntlet in detail
- [SARIF Producer Landscape](../research/sarif-producer-landscape.md) — what real SARIF files contain
- Existing `ReplaceAnnotations` in `src/RepoQL.Data.DuckDB/DuckDbDataStoreExtensions.cs` (lines 220-259) — pattern to follow
- Existing `UpsertAnnotation` in same file (lines 1134-1168) — semantic_key conflict resolution
- Existing `AppendSpans` in same file (lines 876-892) — bulk span write pattern
- DuckDB test patterns in `src/tests/RepoQL.Data.DuckDB.Tests/TestServiceCollectionExtensions.cs`
- [Testing guidelines](../knowledge/testing-guidelines.md)

## Error Policy

The normalizer never throws on bad input. A malformed SARIF envelope (wrong version, missing runs) returns a `NormalizationResult` with zero runs and warnings explaining what went wrong — the import service (Plan 02) is responsible for treating zero-run results from envelope failures as fatal errors to the agent. A malformed individual result within a valid SARIF is skipped and counted. `ReplaceAnnotationsBySource` is transactional — if anything fails during the write, the entire transaction rolls back and nothing changes.
