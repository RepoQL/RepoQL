---
description: Architecture for importing SARIF static analysis results as queryable annotations in the graph
tags: [sarif, import, annotations, lint, design, normalization, static-analysis]
audience: { human: 55, agent: 45 }
purpose: { design: 85, flow: 15 }
---

# SARIF Import Design

## North Star

An agent imports a SARIF file with one call and queries every finding from every scanner through the existing SQL surface. No new tools. No new tables. No tutorials.

## Context

Static analysis scanners (Snyk, CodeQL, Qodana, Semgrep, ESLint, Roslyn, Trivy) emit SARIF 2.1.0 files. These need to become `annotation(kind='lint')` records in the graph — scoped to documents, targeting line ranges, carrying severity and rule metadata, queryable through `annotations` view and `annotations_for()` macro.

**Enables:**
- [SARIF Import Flow](../flows/future/sarif-import.md) — end-to-end pipeline
- [SARIF Normalization Flow](../flows/future/sarif-normalization.md) — producer quirk handling
- [SARIF Re-Import Flow](../flows/future/sarif-reimport.md) — stale finding expiration
- [SARIF Query Patterns Flow](../flows/future/sarif-query-patterns.md) — agent consumption

**Informed by:**
- [SARIF Producer Landscape](../research/sarif-producer-landscape.md) — what real SARIF files look like
- [SARIF Import North Star](../north-star/sarif-import.md) — what great looks like

**Key insight from research:** The universal SARIF subset is small — `ruleId`, `message.text`, `level`, `startLine`, and a file path. Everything else varies wildly by producer. Path formats, fingerprint schemes, rule locations, severity fields — all different. The design must absorb this variance in one place and present a clean interface to everything downstream.

## Constraints

| Constraint | Source | Impact |
|------------|--------|--------|
| Single writer | CLAUDE.md | All DuckDB writes through `DuckDbDataStore` |
| Schema frozen | CLAUDE.md | 5 tables never change. `annotation` already has `semantic_key`, `kind`, `severity`, `source`, `rule_id`, `message`, `data`, `scope_document_id`, `target_span_id`, `target_uri` — everything we need |
| Not a VFS import | Architecture | SARIF produces annotations, not files. No `IVirtualFileSystemImporter`, no `CompositeFileSystemMount`, no file indexing pipeline |
| Host-side processing | Two-process architecture | Real work runs in the gRPC host, not the MCP client |
| Docs with features | CLAUDE.md | Must ship with `help://` documentation |
| Errors never cascade | CLAUDE.md | One malformed SARIF result must not stop the import |

---

## Components

```
┌─────────────────────────────────────────────────────────────┐
│                      Import Tool (MCP)                       │
│  Thin adapter: detects sarif:// scheme, forwards to host    │
└─────────────────────────────────────────────────────────────┘
                              │ gRPC
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                    SarifImportService                         │
│  Host-side orchestrator: parse → normalize → resolve → write │
└─────────────────────────────────────────────────────────────┘
         │                                    │
         ▼                                    ▼
┌─────────────────┐                  ┌─────────────────┐
│ SarifNormalizer  │                  │ DuckDbDataStore  │
│                  │                  │                  │
│ - Path cleanup   │                  │ - Resolve URIs   │
│ - Rule merging   │                  │ - Create spans   │
│ - Severity res.  │                  │ - Write annots.  │
│ - Source ident.  │                  │ - Expire stale   │
│                  │                  │                  │
│ Pure function:   │                  │ Single writer:   │
│ no DB, no I/O    │                  │ one transaction  │
└─────────────────┘                  └─────────────────┘
```

### Boundary: Normalize vs Import

| Concern | Owner | Why |
|---------|-------|-----|
| Producer-specific path formats | SarifNormalizer | New producer quirks only touch normalization |
| Producer-specific rule locations | SarifNormalizer | Qodana rules on extensions, sonar-tools has none |
| Producer-specific severity fields | SarifNormalizer | `ideaSeverity`, CVSS scores, SonarQube severity words |
| Source identification (tool name → slug) | SarifNormalizer | Known producer table + slugify fallback |
| Path → document node resolution | SarifImportService | Requires graph access |
| Semantic key computation | SarifImportService | Requires resolved paths + fingerprints |
| Span creation for line ranges | SarifImportService | Requires DuckDB write |
| Annotation upsert + expiration | SarifImportService | Requires single-writer transaction |

SarifNormalizer is a pure function: SARIF JSON + repo root path in, normalized results out. No database, no file system, no I/O. Testable with real SARIF samples from each producer.

---

## Contracts

### SarifNormalizer

```csharp
public interface ISarifNormalizer
{
    NormalizationResult Normalize(JsonDocument sarif, string repoRootPath);
}

public record NormalizationResult(
    IReadOnlyList<NormalizedRun> Runs,      // empty on envelope validation failure
    int SkippedResults,
    IReadOnlyList<string> Warnings);        // contains failure reason when Runs is empty

public record NormalizedRun(
    string Source,                          // e.g. "snyk-code", "qodana-jvm"
    IReadOnlyList<NormalizedResult> Results);

public record NormalizedResult(
    string RuleId,
    string Message,
    string Level,                           // "error", "warning", "note", "none"
    string NormalizedPath,                  // relative, forward-slash, no scheme
    NormalizedRegion? Region,
    IReadOnlyDictionary<string, string>? PartialFingerprints, // from SARIF partialFingerprints
    IReadOnlyDictionary<string, string>? Fingerprints,        // from SARIF fingerprints
    JsonObject? RuleMetadata,               // name, description, helpUri, tags, CWE
    JsonObject? Data);                      // codeFlows, relatedLocations, fixes, properties

public record NormalizedRegion(
    int StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);
```

`NormalizedResult` carries everything the importer needs without any producer-specific knowledge. The normalizer has already:
- Resolved paths through the gauntlet (uriBaseId, scheme stripping, absolute→relative)
- Merged rules from driver and extensions
- Resolved severity from the result/rule/default cascade
- Extracted tool-specific severity into `Data`
- Derived a stable source slug from `tool.driver.name`
- Preserved fingerprints from both SARIF fields separately (the importer applies priority: partialFingerprints > fingerprints > content hash)

### SarifImportService

```csharp
public interface ISarifImportService
{
    Task<SarifImportResult> ImportAsync(
        string sarifFilePath,
        CancellationToken cancellationToken = default);
}

public record SarifImportResult(
    IReadOnlyList<SourceImportResult> Sources,
    int TotalFindings,
    int ResolvedToFiles,
    int UnresolvedPaths,
    IReadOnlyList<string> Warnings);

public record SourceImportResult(
    string Source,
    int Total,
    int New,
    int Updated,
    int Unchanged,
    int Expired,
    int Resolved,
    int Unresolved);
```

---

## Data Flow

### Import Path

```
import("sarif:///build/snyk-results.sarif")
    │
    ▼
ImportTool (MCP client)
    │  Detects sarif:// scheme
    │  Strips scheme → absolute file path
    ▼
gRPC host: SarifImportService.ImportAsync(path)
    │
    ├─► 1. Read file, parse as JSON
    │       Fail: "File not found at {path}" / "Invalid JSON at line {n}"
    │
    ├─► 2. SarifNormalizer.Normalize(json, repoRoot)
    │       Per-result failures → skip + count, don't stop
    │       Output: NormalizationResult with runs and results
    │
    ├─► 3. Aggregate results by source across all runs
    │       (prevents multi-run same-source clobber)
    │       For each result:
    │           Resolve path → document node via GetDocumentByUri
    │           Compute semantic key
    │           Create span if region has line info
    │           Build Annotation record
    │
    ├─► 4. For each source: source-wide replacement transaction
    │       DELETE stale (semantic_key NOT IN new keys, same source)
    │       INSERT OR REPLACE all new annotations + spans
    │
    ▼
SarifImportResult (counts, warnings)
```

### URI Scheme

```
sarif:///build/snyk-results.sarif          → absolute path
sarif:///./ci/qodana.sarif                 → relative to repo root
```

The `sarif://` scheme signals that this is an annotation-only import, not a VFS mount. The import tool detects the scheme and routes to `SarifImportService` instead of `FileSystemImportService`. The path after the scheme is the SARIF file location.

---

## Location Resolution

SARIF normalized paths are relative to repo root: `src/Auth/AuthService.cs`. These must map to existing document nodes.

```
Normalized path: "src/Auth/AuthService.cs"
    → file:///src/Auth/AuthService.cs (prepend file:///)
    → GetDocumentByUri(store, repoUri)
    → Node (scope_document_id) or null (unresolved)
```

| Resolution outcome | Handling |
|--------------------|----------|
| Document found | `scope_document_id` = node ID. If region present, create span → `target_span_id` |
| Document not found | Still imported. `scope_document_id` = synthetic unresolved-imports document. `target_uri` = `file:///{normalizedPath}#line={startLine}` |
| Path outside repo (absolute, not under root) | Preserved in `target_uri` as-is, flagged in warnings |

Unresolved findings remain queryable — an agent can see "5 findings couldn't be matched to indexed files" and decide whether to index those files first.

### Synthetic Unresolved Document

Annotations require a non-null `scope_document_id`. When a finding's path doesn't match an indexed document, the annotation is scoped to a synthetic document node:

- **URI**: `repoql:///sarif/unresolved` — a single global document for all unresolved SARIF findings
- **Creation**: Lazily created on first unresolved finding during import. If it already exists, reused.
- **Node kind**: `document` with a distinguishing property (e.g., `synthetic: true` in metadata)
- **Visibility**: Excluded from the `Files` view by URI scheme (`repoql:///` is not `file:///`). Visible in raw `node` queries.
- **Re-linking**: Not automatic. If missing files are later indexed, a re-import will resolve them to the real documents. The synthetic document accumulates unresolved findings across sources until they're resolved by re-import.

### Symbol Anchoring (Deferred)

The `target_node_id` field on annotations can point to a symbol node when a finding's region overlaps a known symbol's span. This enables queries like "findings on public methods" or "findings in class X". Symbol anchoring requires span overlap computation against the symbol table — deferred to a future plan. V1 resolves to documents and spans only.

### Span Creation

When a SARIF result has a region with `startLine`:

```csharp
var span = new Span
{
    Id = Guid.NewGuid(),
    DocumentId = documentNodeId,
    StartLine = region.StartLine,
    StartColumn = region.StartColumn,  // nullable
    EndLine = region.EndLine,          // nullable
    EndColumn = region.EndColumn       // nullable
};
```

Spans are written in the same transaction as annotations. The annotation's `target_span_id` references the span. No `TextLineMap` needed — SARIF gives us line numbers directly.

**Column semantics:** SARIF columns are 1-based per spec. `endColumn` is exclusive (points to the character after the end). RepoQL span columns are also 1-based. Columns are stored as-is from SARIF. The `endColumn` exclusivity is inherited — this matches how most editors interpret ranges. Producers that don't set `columnKind` (most of them) are assumed `utf16CodeUnits` per SARIF spec default.

---

## Semantic Keys

Format: `{source}:{ruleId}:{normalizedPath}:{startLine}:{fingerprint}`

```
snyk-code:javascript/XSS:src/routes/index.js:42:f5323d...
qodana-jvm:Java/FieldCanBeLocal:src/main/Foo.kt:15:sha256:a1b2c3...
```

### Fingerprint Priority

1. `partialFingerprints` values (Qodana's `equalIndicator/v1`, CodeQL's `primaryLocationLineHash`)
2. `fingerprints` values (Snyk's `"0"`, Semgrep's `matchBasedId/v1`)
3. Content hash fallback: SHA-256 of `{ruleId}:{path}:{startLine}:{message}`

The fingerprint disambiguates multiple findings of the same rule on the same line. Most producers provide at least one fingerprint. The content hash fallback handles producers that don't (sonar-tools).

### Stability

Semantic keys are stable across re-imports of the same scan. A finding that hasn't changed keeps its key, so `ON CONFLICT(semantic_key) DO UPDATE` is a no-op for unchanged findings. Only genuinely new/changed/expired findings cause writes.

---

## Write Pattern

Source-wide replacement in one transaction. This requires a new general-purpose method on `DuckDbDataStoreExtensions`. The existing `ReplaceAnnotations` is per-document — useful when a format analyzer re-runs on one file. Source-wide replacement is a different pattern: expire everything from a source across all documents, upsert the new set.

```csharp
public static AnnotationReplaceResult ReplaceAnnotationsBySource(
    this DuckDbDataStore store,
    string source,
    string kind,
    IReadOnlyList<Annotation> annotations,
    IReadOnlyList<Span> spans)
{
    return store.WriteTransaction((conn, tx) =>
    {
        // 1. Collect new semantic keys
        var newKeys = annotations
            .Where(a => a.SemanticKey != null)
            .Select(a => a.SemanticKey!)
            .ToHashSet();

        // 2. Delete expired: same source+kind, key not in new set
        var expired = DeleteExpiredAnnotations(conn, tx, source, kind, newKeys);

        // 3. Write spans (bulk appender)
        AppendSpans(conn, spans);

        // 4. Upsert annotations (semantic_key conflict resolution)
        var (inserted, updated) = UpsertAnnotations(conn, tx, annotations);

        return new AnnotationReplaceResult(inserted, updated, expired);
    });
}
```

Nothing about this method is SARIF-specific. Any bulk annotation producer — test result importers, coverage importers, architecture violation checkers — can use the same pattern: "here are all annotations from my source, replace what's there." SARIF is the first consumer.

The DELETE uses `NOT IN` with the full set of new semantic keys. For large imports (10K+ findings), the key set is passed as a temporary table rather than an IN-list.

### Why not delete-all + insert-all?

Delete-all would work, but:
- Loses `created_at` on unchanged findings (misleading — finding appears "new" every import)
- Writes every row even when nothing changed (wasted I/O)
- Semantic key comparison is cheap and preserves provenance

---

## Cross-Cutting Concerns

### Error Isolation

Every SARIF result is processed independently. A malformed result (missing message, unresolvable path, invalid region) is skipped and counted. The import continues. The response reports skipped results with reasons.

The normalizer produces per-result warnings. The importer produces per-result resolution failures. Both accumulate in the `SarifImportResult.Warnings` list.

### Multi-Run SARIF

A SARIF file can contain multiple runs (multiple tools or configurations). Each run has its own `tool.driver.name` → its own `source`. Results are aggregated by source across all runs before calling `ReplaceAnnotationsBySource`. This prevents the multi-run same-source clobber problem: if two runs produce the same source slug, calling replacement per-run would cause the second run's replacement to expire the first run's annotations. Aggregation ensures all results from a source are written together.

### Data Payload Schema

The `data` JSON field on each annotation carries everything not captured by the standard annotation fields:

```json
{
  "sarif_source": "snyk-code",
  "sarif_run_index": 0,
  "original_level": "error",
  "rule": {
    "name": "XSS",
    "shortDescription": "Cross-Site Scripting",
    "helpUri": "https://...",
    "help_markdown": "...",
    "tags": ["security", "CWE-79"],
    "properties": { }
  },
  "partialFingerprints": { "equalIndicator/v1": "a1b2c3..." },
  "fingerprints": { "0": "f5323d...", "1": "f0155d..." },
  "codeFlows": [ ],
  "relatedLocations": [ ],
  "fixes": [ ],
  "properties": { "priorityScore": 908 }
}
```

Standard fields are for `WHERE` and `ORDER BY`. The `data` payload is for inspection after finding a result.

### Progress Reporting

SARIF import is fast (in-memory processing, single transaction). No progress callback needed for v1. If large SARIF files (10K+ results) become common, add stage-level progress: normalizing → resolving → writing.

### Transport

`ImportResponse` gains two optional fields: `string message` (pre-formatted summary) and `string operation_id` (for async VFS imports). SARIF imports are synchronous — they set `message` with the summary but no `operation_id`. VFS imports (github://, local://) return immediately with `operation_id` — the agent can poll via `_operations()` UDF.

This is a general-purpose improvement to the import response, not SARIF-specific. Any import type can use `message` for a pre-formatted summary.

### Response Format

The host pre-formats the SARIF summary into the `message` field:

```
Imported 47 findings from snyk-code
  12 error, 28 warning, 7 info
  45 resolved to indexed files, 2 unresolved
  15 new, 8 updated, 12 unchanged, 12 expired
```

For multi-source SARIF files, one block per source. The MCP tool displays this directly.

### help:// Documentation

Ships with embedded docs queryable via `read("help:///tools/import/sarif.md", 2000)`:
- Supported producers and their quirks
- Semantic key format
- Re-import behavior
- Example queries after import

---

## Project Structure

```
src/
  RepoQL.Sarif/                          ← new project
    SarifNormalizer.cs                   ← ISarifNormalizer: pure function
    SarifImportService.cs                ← ISarifImportService: orchestrator
    Normalization/
      PathNormalizer.cs                  ← uriBaseId resolution, scheme stripping
      RuleCollector.cs                   ← driver + extensions → unified lookup
      SeverityResolver.cs                ← result/rule/default cascade
      SourceIdentifier.cs                ← tool.driver.name → slug
    Models/
      NormalizedResult.cs                ← output types
      ProducerMap.cs                     ← known producer name → slug table
```

**Dependencies:**
- `RepoQL.Sarif` → `RepoQL.Contracts` (Annotation, Span, RepoUri)
- `RepoQL.Sarif` → `RepoQL.Data.DuckDB` (DuckDbDataStore for writes)
- No dependency on `RepoQL.Indexing` or `RepoQL.FileSystem`

The normalizer needs only `System.Text.Json` and the repo root path string. The importer needs `DuckDbDataStore` for writes and `RepoUri` for path resolution. No VFS dependency — SARIF path resolution is string manipulation (prepend `file:///`) followed by a database lookup.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| `System.Text.Json` | Microsoft SARIF SDK | SDK is heavy (200KB+), pulls transitive deps, we only need a subset. JSON DOM gives us exactly the access we need |
| Source-wide expiration | File-scoped expiration | Full scans are the norm. Conservative scoping adds complexity for a rare case. Partial scan support deferred as opt-in flag |
| New project `RepoQL.Sarif` | Classes in `RepoQL.Indexing` | SARIF import is annotation-only, not file indexing. Clean separation of concerns. Normalizer has no DuckDB dependency |
| General `ReplaceAnnotationsBySource` method | Reusing per-document `ReplaceAnnotations` | Existing method is per-document. Source-wide replacement is a different pattern useful to any bulk annotation producer, not just SARIF |
| Synthetic unresolved-imports document | Dropping unresolved findings | Unresolved findings are still valuable. Agent can see them, decide to index the missing files, re-query |
| SHA-256 content hash fallback | Rejecting fingerprint-less results | sonar-tools has no fingerprints. Content hash is stable enough for re-import deduplication |

## Alternatives Considered

**SARIF SDK (Microsoft.CodeAnalysis.Sarif):** Provides full object model with validation. Rejected: heavy dependency for a subset of fields. We only need ruleId, message, level, location, fingerprints, and rule metadata. `System.Text.Json` DOM access is sufficient and keeps the dependency light.

**IVirtualFileSystemImporter for SARIF:** Would fit the existing import dispatch pattern. Rejected: SARIF produces annotations, not files. The VFS importer returns a `CompositeFileSystemMount` — a mount of files to index. SARIF has no files to mount. Forcing it into this interface would be architecturally dishonest.

**Per-document write pattern (reuse ReplaceAnnotations):** Loop over affected documents, call existing method per document. Rejected: source-wide expiration requires knowing which annotations to expire before starting. A per-document loop can't expire findings in documents that had results last time but don't this time. The general `ReplaceAnnotationsBySource` method solves this for any bulk producer.

**Separate `sarif-import` tool:** New MCP tool instead of extending `import`. Rejected: `import` already handles URI-based dispatch. Adding a scheme is simpler than a new tool. Agents already know `import`.

## Risks

| Risk | Mitigation |
|------|------------|
| Large SARIF files (50MB+) | Stream-parse if needed. V1 loads into memory — GitHub enforces 10MB compressed ceiling, real files rarely exceed 20MB |
| Producer we haven't seen | Normalizer's slugify fallback handles unknown tool names. Path normalization cascade handles common patterns. Add to known-producer table when encountered |
| Many unresolved paths (files not yet indexed) | Warning in response. Agent can import the repo first, then re-import SARIF. Or query unresolved findings via `target_uri` |
| Semantic key instability across SARIF versions | Key includes source + ruleId + path + line + fingerprint. These are stable across re-runs of the same scanner. Content hash fallback is deterministic |
| Transaction size for 10K+ findings | Use temporary table for semantic key set instead of IN-list. DuckDB handles bulk inserts efficiently via appender API |

## Extension Points

- **ISarifNormalizer** — swap implementation for SARIF spec revisions or test doubles
- **ProducerMap** — add known producers without code changes (data table, not switch statement)
- **`partial: true` flag** — future opt-in for partial scan imports with file-scoped expiration
- **Custom source override** — `import("sarif:///path", source: "my-scanner")` to override auto-detected source slug
- **Annotation kind** — v1 uses `kind='lint'` for all SARIF. Future: map SARIF `kind` property (`fail`, `pass`, `open`) to different annotation kinds

---

## Verification

| Level | What | How |
|-------|------|-----|
| Unit | Normalizer path transforms | Feed raw paths per producer → assert relative output |
| Unit | Normalizer rule collection | Qodana SARIF with rules on extensions → assert all rules found |
| Unit | Normalizer severity resolution | Result with/without level, rule with/without default → assert correct severity |
| Unit | Semantic key computation | Same result twice → same key. Different line → different key |
| Unit | Source identification | Known producer names → correct slugs. Unknown names → valid slugs |
| Integration | End-to-end import | Index a repo, import real SARIF, query `annotations` → verify results |
| Integration | Re-import expiration | Import, modify SARIF (remove findings), re-import → verify expired |
| Integration | Idempotent re-import | Import same file twice → zero changes on second import |
| Integration | Unresolved paths | Import SARIF with paths not in graph → verify `target_uri` set, warning returned |

---

## Related

- [Schema](../../Schema.md) — `annotation` table, `semantic_key`, `annotations` view
- [Vocabulary](../../Vocabulary.md) — `annotation.kind = 'lint'`, severity mapping
- [SARIF Import North Star](../../north-star/sarif-import.md)
- [SARIF Producer Landscape](../../research/sarif-producer-landscape.md)
- [SARIF Import Flow](../../flows/future/sarif-import.md)
- [SARIF Normalization Flow](../../flows/future/sarif-normalization.md)
- [SARIF Re-Import Flow](../../flows/future/sarif-reimport.md)
- [SARIF Query Patterns Flow](../../flows/future/sarif-query-patterns.md)
