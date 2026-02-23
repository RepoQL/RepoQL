---
description: Plan for SARIF import service, sarif:// routing, and async import response with operation ID
tags: [sarif, import, annotations, gRPC, plan, routing, proto, async]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: SARIF Import Service + Import Response Improvements

Implements: [SARIF Import Design](../designs/future/sarif-import.md) — SarifImportService, scheme routing, location resolution, semantic key computation, DI registration. Also improves import response transport for all import types.

## Scope

**Covers:**
- `ISarifImportService` interface and `SarifImportService` class in `src/RepoQL.Sarif/` — host-side orchestrator: reads SARIF file, calls normalizer, resolves paths to document nodes, computes semantic keys, creates spans, builds Annotation records, calls `ReplaceAnnotationsBySource`
- `SarifImportResult` record — per-source results with counts and warnings
- Semantic key computation: `{source}:{ruleId}:{normalizedPath}:{startLine}:{fingerprint}`
- Fingerprint priority: partialFingerprints > fingerprints > SHA-256 content hash fallback
- Path-to-document resolution via `GetDocumentByUri` on `DuckDbDataStore`
- Unresolved path handling: `target_uri` set, counted in warnings
- `sarif://` scheme detection in `RepoQlServiceImpl.ImportRepository` — before calling `FileSystemImportService`
- DI registration of `ISarifImportService`
- `RepoQL.Sarif.csproj` updated to add `RepoQL.Data.DuckDB` project reference
- **Proto changes**: add `optional string message` and `optional string operation_id` to `ImportResponse`
- **Async VFS imports**: `ImportRepository` returns immediately with `operation_id` for VFS imports (github://, local://) instead of awaiting completion
- **SARIF imports are synchronous**: no operation created, result returned directly with `message`
- `ImportTool` updated: description mentions `sarif://`, formats SARIF summary from `message`, shows operation ID for VFS imports
- Integration tests: end-to-end import, re-import expiration, idempotent re-import, unresolved paths

**Does not cover:**
- `ReplaceAnnotationsBySource` or `SarifNormalizer` (Plan: sarif-01-foundation)
- `help://` documentation (Plan: sarif-03-documentation)
- Custom source override parameter on import (future extension)
- `partial: true` flag for partial scan imports (future extension)
- Command tool wait/poll for operations (existing `_operations()` UDF already supports this)

## Enables

- End-to-end SARIF import from any agent: `import("sarif:///build/snyk-results.sarif")`
- Annotations from any SARIF 2.1.0 producer queryable via `annotations` view and `annotations_for()` macro
- Re-import correctly expires stale findings and adds new ones
- VFS imports (GitHub repos) return immediately — agent gets an operation ID and can proceed while indexing happens
- Agent can check operation progress via `query("SELECT * FROM _operations()")` or `query("SELECT * FROM _operation('id')")`
- Plan 3 can document the working feature

## Prerequisites

- Plan: sarif-01-foundation (provides `ReplaceAnnotationsBySource`, `ISarifNormalizer`, normalizer implementation, output model types)

## North Star

An agent calls `import("sarif:///build/snyk-results.sarif")` and immediately gets a summary of what landed. It calls `import("github://org/repo")` and immediately gets an operation ID — no waiting. Both return fast. The agent queries when ready.

## Done Criteria

### Proto Changes

- `ImportResponse` in `src/RepoQL.Protocol/Protos/repoql.proto` shall gain two optional fields: `optional string message` and `optional string operation_id`
- `ImportResult` (the C# wrapper in `src/RepoQL.Protocol/ImportResult.cs`) shall expose `Message` and `OperationId` properties
- `IRepoQlClient.ImportRepositoryAsync` shall propagate the new fields through to callers

### Async VFS Imports

- For VFS imports (github://, local://), `RepoQlServiceImpl.ImportRepository` shall create the operation and return immediately with `operation_id` set — it shall NOT await `operation.Completion`
- The `message` field shall carry a summary like `"Importing {count} files from {source} — operation {id}"`
- Existing `total_files`, `indexed_count`, etc. shall be populated from the initial operation state (counts at enqueue time)

### SarifImportService

- `ISarifImportService` and `SarifImportService` shall live in `src/RepoQL.Sarif/`
- The service shall accept a file path string and a `CancellationToken`
- When the file does not exist, the service shall throw with an actionable message: `"SARIF file not found at {path}"`
- When the file contains invalid JSON, the service shall throw with: `"Invalid JSON in SARIF file at {path}: {parseError}"`
- When the normalizer returns zero runs (indicating envelope validation failure — wrong version, missing runs, missing tool.driver.name), the service shall throw with the normalizer's warning message — envelope failures are fatal at the service level, not silent warnings
- When normalization returns zero results across all runs, the service shall return a result with a warning, not throw
- The service shall call `ISarifNormalizer.Normalize(json, repoRootPath)` to get normalized results
- The service shall aggregate normalized results by source across all runs before calling `ReplaceAnnotationsBySource` — this prevents multi-run same-source clobber where a later run's replacement would expire an earlier run's annotations
- The service shall return `SarifImportResult` with per-source entries (each with total/new/updated/unchanged/expired/resolved/unresolved counts) plus aggregate totals

### Location Resolution

- For each normalized result, the service shall resolve `normalizedPath` to a document node via `file:///{normalizedPath}` and `GetDocumentByUri`
- When a document is found, `scope_document_id` shall be set to the document node's ID
- When a document is found and a region with `StartLine` exists, the service shall create a `Span` with the region's line/column info and set `target_span_id`
- When a document is NOT found, the annotation shall still be created with `target_uri` set to `file:///{normalizedPath}#line={startLine}` and `scope_document_id` set to a synthetic unresolved-imports document
- The count of unresolved paths shall be reported in per-source result entries

### Semantic Keys

- The service shall compute semantic keys in the format `{source}:{ruleId}:{normalizedPath}:{startLine}:{fingerprint}`
- Fingerprint shall be the first non-empty value from: `PartialFingerprints` dictionary values (on the `NormalizedResult`), then `Fingerprints` dictionary values — the normalizer preserves these as separate fields for this priority
- When no fingerprints exist, the service shall compute a SHA-256 hash of `{ruleId}:{path}:{startLine}:{message}` as the fingerprint
- Semantic keys shall be stable across re-imports of the same scan results

### Annotation Construction

- Each annotation shall have `Kind = "lint"` and `Source` from the normalized run's source slug
- `Severity` shall map SARIF levels: `"error"` → `"error"`, `"warning"` → `"warning"`, `"note"` → `"info"`, `"none"` → `"hint"`
- `RuleId` shall be the verbatim SARIF `ruleId`
- `Message` shall be the normalized message text (fallback chain: text → markdown → messageStrings resolution)
- `Data` shall be a JSON object carrying: rule metadata, fingerprints, codeFlows, relatedLocations, fixes, properties, tool-specific severity
- The service shall call `ReplaceAnnotationsBySource` once per source (aggregated across runs)

### Scheme Routing

- In `RepoQlServiceImpl.ImportRepository`, after `RepoUri.TryParse`, when the scheme equals `sarif` (case-insensitive), the service shall resolve the file path from the URI
- The service shall call `ISarifImportService.ImportAsync(resolvedPath, cancellationToken)` directly
- The service shall format the `SarifImportResult` into the `message` field of `ImportResponse`
- The service shall NOT set `operation_id` for SARIF imports (they complete synchronously)
- The service shall NOT call `FileSystemImportService` for `sarif://` URIs

### DI Registration

- `ISarifImportService` shall be registered as a singleton in the host's service collection
- `SarifImportService` constructor shall accept `ISarifNormalizer`, `DuckDbDataStore`, and the repo root path
- Registration shall be in `RepoIndexerServiceCollectionExtensions.AddRepoIndexer()` or `ServeCommands` host builder

### ImportTool Update

- The `ImportTool` description shall mention `sarif:///path/to/file.sarif` as a supported URI pattern
- For SARIF imports, the tool shall display the `message` from the response (the pre-formatted summary)
- For VFS imports, the tool shall display the `operation_id` and `message`, guiding the agent to query `_operations()` for progress
- The tool shall no longer block waiting for VFS import completion

### Tests

- A test shall verify end-to-end: seed 3 document nodes, import SARIF with findings on 2 files, query `SELECT * FROM annotations WHERE kind = 'lint'`, verify correct count and field values
- A test shall verify re-import expiration: import SARIF with 5 findings, re-import with 4 (one removed), verify 4 annotations remain
- A test shall verify idempotent re-import: import same SARIF twice, second import returns zero new/expired
- A test shall verify unresolved paths: import SARIF with a finding on a file not in the graph, verify `target_uri` is set
- A test shall verify multi-run SARIF with same source: file with 2 runs from same tool, annotations aggregated before write (no clobber)
- A test shall verify multi-run SARIF with different sources: file with 2 runs from different tools, annotations from both sources exist independently
- A test shall verify semantic key stability across imports
- A test shall verify that VFS import returns operation_id without blocking

## Constraints

- **Single writer** — all DuckDB writes through `DuckDbDataStore.WriteTransaction`
- **Transport parity** — `sarif://` works via CLI, MCP, and gRPC (scheme detection is host-side)
- **Errors never cascade** — one bad result in a SARIF file never stops the import of other results
- **Envelope failures are fatal** — wrong SARIF version, missing runs, missing tool name → error to agent (not a silent warning)
- **TUnit + AwesomeAssertions** for all tests

## References

- [SARIF Import Design](../designs/future/sarif-import.md) — data flow, location resolution, semantic keys
- [SARIF Import Flow](../flows/future/sarif/sarif-import.md) — end-to-end pipeline stages
- [SARIF Re-Import Flow](../flows/future/sarif/sarif-reimport.md) — expiration lifecycle
- [SARIF Import North Star](../north-star/sarif-import.md) — what great looks like
- `RepoQlServiceImpl.ImportRepository` at `src/RepoQL.ConsoleApp/Host/RepoQlServiceImpl.cs` — scheme detection + async return
- `repoql.proto` at `src/RepoQL.Protocol/Protos/repoql.proto` — `ImportResponse` message to extend
- `ImportResult.cs` at `src/RepoQL.Protocol/ImportResult.cs` — C# wrapper to update
- `ImportTool` at `src/RepoQL.ConsoleApp/Tools/ImportTool.cs` — MCP surface
- DI registration in `src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs`
- `DuckDbTestStore` at `src/tests/RepoQL.Testing/Indexing/DuckDbTestStore.cs`
- `GetDocumentByUri` in `src/RepoQL.Data.DuckDB/DuckDbDataStoreExtensions.cs`
- Operations UDFs: `_operations()`, `_operation(id)` — existing SQL surface for operation tracking

## Error Policy

File-level errors (not found, invalid JSON) and envelope validation failures (wrong version, missing runs) are fatal — they surface as actionable error messages to the agent. Result-level errors (malformed location, unresolvable path, missing fields) are collected in warnings and reported in the import result. The import always completes if the SARIF envelope is valid, even if every result fails to resolve. The write transaction is atomic — if it fails, nothing changes.
