---
description: Architecture for importing SARIF static analysis results as queryable annotations in the graph
tags: [sarif, import, annotations, lint, design, normalization, static-analysis]
audience: { human: 55, agent: 45 }
purpose: { design: 85, flow: 15 }
---

# SARIF Import Design

An agent imports a SARIF file with one call and queries every finding from every scanner through the existing SQL surface. No new tools. No new tables. No tutorials.

```
import("sarif:///build/snyk-results.sarif")

SELECT source, severity, rule_id, message, resolved_target_uri
FROM annotations WHERE kind = 'lint'
ORDER BY severity_rank DESC
```

## Context

Static analysis scanners — Snyk, CodeQL, Qodana, Semgrep, ESLint, Roslyn, Trivy — emit SARIF 2.1.0 files. These become `annotation(kind='lint')` records, scoped to documents, targeting line ranges, carrying severity and rule metadata, queryable through the `annotations` view and `annotations_for()` macro.

**The problem is variance.** The universal SARIF subset is small: `ruleId`, `message.text`, `level`, `startLine`, and a file path. Everything else differs by producer. Paths use three different `uriBaseId` conventions. Rules live on `tool.driver` for most producers but on `tool.extensions` for Qodana. Fingerprints use two different SARIF fields with five different key names. Half of producers generate no fingerprints at all. This design absorbs all of that variance in one place — the normalizer — so everything downstream sees clean, uniform data.

**Informed by:** [SARIF Producer Landscape](../../research/sarif-producer-landscape.md) (what real files contain), [SARIF Import North Star](../../north-star/sarif-import.md) (what great looks like)

**Enables:** [Import Flow](../../flows/future/sarif/sarif-import.md), [Normalization Flow](../../flows/future/sarif/sarif-normalization.md), [Re-Import Flow](../../flows/future/sarif/sarif-reimport.md), [Query Patterns](../../flows/future/sarif/sarif-query-patterns.md)

---

## Architecture

Two components with a hard boundary between them.

```
┌───────────────────────────────────────────────────────────────┐
│                    Import Tool (MCP client)                     │
│     Detects sarif:// scheme, forwards path to gRPC host        │
└───────────────────────┬───────────────────────────────────────┘
                        │ gRPC
                        ▼
┌───────────────────────────────────────────────────────────────┐
│                    SarifImportService                           │
│     Orchestrator: parse → normalize → resolve → write          │
└───────────┬───────────────────────────────┬───────────────────┘
            │                               │
            ▼                               ▼
┌─────────────────────┐          ┌─────────────────────┐
│   SarifNormalizer    │          │   DuckDbDataStore    │
│                      │          │                      │
│ Pure function:       │          │ Single writer:       │
│ SARIF JSON + repo    │          │ resolve paths to     │
│ root path in,        │          │ documents, create    │
│ normalized results   │          │ spans, upsert        │
│ out. No DB, no I/O.  │          │ annotations, expire  │
│                      │          │ stale findings.      │
│ Path normalization   │          │ One transaction.     │
│ Rule collection      │          │                      │
│ Severity resolution  │          │                      │
│ Source identification │          │                      │
└─────────────────────┘          └─────────────────────┘
```

### The Boundary

The normalizer absorbs producer variance. The import service handles graph operations. New producer quirks only touch the normalizer. New graph features only touch the import service.

| Concern | Owner | Why |
|---------|-------|-----|
| Path formats (uriBaseId, file:/// scheme, absolute paths) | Normalizer | Each producer has its own conventions |
| Rule locations (driver vs extensions vs absent) | Normalizer | Qodana puts rules on extensions, sonar-tools has none |
| Severity resolution (result/rule/default cascade) | Normalizer | Three levels of fallback, tool-specific severity in properties |
| Source identification (tool.driver.name → slug) | Normalizer | Known producer map + slugify fallback |
| Path → document node resolution | Import service | Requires graph access |
| Semantic key computation | Import service | Requires resolved paths + fingerprint selection |
| Span creation | Import service | Requires DuckDB write |
| Annotation upsert + stale expiration | Import service | Requires single-writer transaction |

### Error Policy

The normalizer never throws. A malformed SARIF envelope (wrong version, missing runs) returns a `NormalizationResult` with zero runs and the failure described in `Warnings`. A run without `tool.driver.name` is skipped with a warning — the normalizer cannot derive a source slug without it. If all runs are skipped (no valid source identification in any run), the result is zero runs. A malformed individual result (missing ruleId, message, or location) is skipped and counted.

The import service inspects the normalizer's output: zero runs from envelope failure or all-skipped is a fatal error surfaced to the agent; zero results across valid runs is a warning (legitimate — a clean scan expires all previous findings).

---

## Contracts

### ISarifNormalizer

```csharp
public interface ISarifNormalizer
{
    NormalizationResult Normalize(JsonDocument sarif, string repoRootPath);
}

public record NormalizationResult(
    IReadOnlyList<NormalizedRun> Runs,      // empty on envelope failure — Warnings explains why
    int SkippedResults,
    IReadOnlyList<string> Warnings);

public record NormalizedRun(
    string Source,                          // slug: "snyk-code", "qodana-jvm", "codeql"
    IReadOnlyList<NormalizedResult> Results);

public record NormalizedResult(
    string RuleId,                          // verbatim from SARIF
    string Message,                         // text preferred; markdown fallback; messageStrings last
    string Level,                           // "error" | "warning" | "note" | "none"
    string NormalizedPath,                  // relative, forward-slash, no scheme, no uriBaseId
    NormalizedRegion? Region,
    IReadOnlyDictionary<string, string>? PartialFingerprints,  // separate from Fingerprints
    IReadOnlyDictionary<string, string>? Fingerprints,         // for priority during key computation
    JsonObject? RuleMetadata,               // name, description, helpUri, tags, CWE
    JsonObject? Data);                      // codeFlows, relatedLocations, fixes, tool-specific severity

public record NormalizedRegion(
    int StartLine,                          // 1-based
    int? StartColumn,                       // 1-based, stored as-is from SARIF
    int? EndLine,                           // 1-based
    int? EndColumn);                        // 1-based, exclusive (SARIF convention)
```

The normalizer has already resolved paths through the gauntlet (uriBaseId, scheme stripping, absolute-to-relative), merged rules from driver and extensions, resolved severity from the cascade, extracted tool-specific severity into `Data`, and derived a stable source slug. Fingerprints are preserved in two separate dictionaries so the import service can apply selection priority.

Results are skipped when they lack a `ruleId`, a `message` (after fallback chain: text → markdown → messageStrings resolution), or a location with `artifactLocation.uri`. Locationless findings cannot produce stable semantic keys.

### ISarifImportService

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
    int Updated,        // same key, different data
    int Unchanged,      // same key, same data — no write
    int Expired,        // existed before, absent now
    int Resolved,       // path matched an indexed document
    int Unresolved);    // path not found in graph
```

### ReplaceAnnotationsBySource

```csharp
public static AnnotationReplaceResult ReplaceAnnotationsBySource(
    this DuckDbDataStore store,
    string source,
    string kind,
    IReadOnlyList<Annotation> annotations,
    IReadOnlyList<Span> spans);

public record AnnotationReplaceResult(int Inserted, int Updated, int Expired);
```

Nothing about this method is SARIF-specific. Any bulk annotation producer — test results, coverage, architecture violations — uses the same pattern: "here are all annotations from my source, replace what's there." SARIF is the first consumer.

---

## Data Flow

```
import("sarif:///build/snyk-results.sarif")
  │
  ▼
Import tool detects sarif:// → resolves file path → gRPC to host
  │
  ▼
SarifImportService.ImportAsync(path)
  │
  ├── 1. Read file, parse as JsonDocument
  │     File not found → "SARIF file not found at {path}"
  │     Invalid JSON → "Invalid JSON in SARIF file: {error}"
  │
  ├── 2. SarifNormalizer.Normalize(json, repoRoot)
  │     Envelope failure (wrong version, missing runs) → zero runs, fatal error to agent
  │     Per-result failures → skip + count, continue
  │
  ├── 3. Aggregate results by source across all runs
  │     For each result:
  │       Resolve path → document node via GetDocumentByUri
  │       Compute semantic key (source:ruleId:path:line:fingerprint)
  │       Create span if region has startLine
  │       Build Annotation record
  │
  ├── 4. Per source: ReplaceAnnotationsBySource (one transaction per source)
  │     Delete annotations from this source whose key is absent from new set
  │     Write spans via bulk appender
  │     Upsert annotations via ON CONFLICT(semantic_key) DO UPDATE
  │     (multi-source SARIF: separate transactions per source — independent rollback)
  │
  ▼
SarifImportResult → formatted into ImportResponse.message
```

### URI Scheme

`sarif://` signals an annotation-only import. The path after the scheme is the SARIF file location — absolute or relative to the repo root. The import tool detects the scheme and routes to `SarifImportService` instead of `FileSystemImportService`.

```
sarif:///build/snyk-results.sarif     → absolute path
sarif:///./ci/qodana.sarif            → relative to repo root
```

### Multi-Run Aggregation

A SARIF file can contain multiple runs. Each run is normalized independently — own `tool.driver.name`, own rules, own path conventions. After normalization, results are aggregated by source across runs before writing. Two runs from the same tool (different configurations) become one aggregated batch. This prevents the clobber problem: calling `ReplaceAnnotationsBySource` per-run would cause the second run's replacement to expire the first run's findings.

---

## Location Resolution

Normalized paths are relative to the repo root. The import service resolves them to document nodes.

```
"src/Auth/AuthService.cs"
  → file:///src/Auth/AuthService.cs
  → GetDocumentByUri(store, uri)
  → document node (scope_document_id) or null
```

| Outcome | Handling |
|---------|----------|
| Document found | `scope_document_id` = node ID. Region with startLine → create span → `target_span_id` |
| Document not found (repo-relative path) | `scope_document_id` = synthetic unresolved document. `target_uri` = `file:///{path}#line={startLine}` |
| Path outside repo (absolute, unresolvable) | `scope_document_id` = synthetic unresolved document. `target_uri` = original absolute URI (must be valid `RepoUri` — wrap bare paths in `file:///` if no scheme) |

Unresolved findings remain queryable. An agent sees "5 findings couldn't be matched to indexed files" and can index those files, then re-import.

### Synthetic Unresolved Document

Annotations require a non-null `scope_document_id`. Unresolved findings are scoped to a synthetic document:

- **URI**: `repoql:///sarif/unresolved`
- **Creation**: Lazy — created on first unresolved finding, reused thereafter
- **Visibility**: Excluded from `Files` view because the synthetic node has no `artifact_id` (the `Files` view joins `node` on `artifact`). Visible in raw `node` queries
- **Re-linking**: Not automatic. Re-importing after indexing the missing files resolves them to real documents

### Span Creation

When a result has a region with `startLine`, a span is created in the same transaction:

```csharp
new Span {
    Id = Guid.NewGuid(),
    DocumentId = documentNodeId,
    StartLine = region.StartLine,
    StartColumn = region.StartColumn,   // nullable, 1-based
    EndLine = region.EndLine,           // nullable, 1-based
    EndColumn = region.EndColumn        // nullable, 1-based, exclusive
};
```

SARIF columns are 1-based per spec. `endColumn` is exclusive (character after the end). These values are stored as-is — RepoQL spans use the same conventions. No `TextLineMap` needed; SARIF gives us line numbers directly.

### Symbol Anchoring (Deferred)

The `target_node_id` field can point to a symbol node when a finding's region overlaps a known symbol's span. This enables "findings on public methods" or "findings in class X." Deferred to a future plan — V1 resolves to documents and spans only.

---

## Semantic Keys

Stable identity for each finding, enabling idempotent upsert and re-import deduplication.

**Format:** `{source}:{ruleId}:{normalizedPath}:{startLine}:{fingerprint}`

```
snyk-code:javascript/XSS:src/routes/index.js:42:f5323d...
qodana-jvm:Java/FieldCanBeLocal:src/main/Foo.kt:15:sha256:a1b2c3...
snyk-oss:SNYK-JS-ADMZIP-1065796:package.json:0:sha256:b4e5f6...
```

When `startLine` is absent (result has no region, or region uses only `charOffset`), `0` is used in the key. This is stable — the same result always produces the same key — and distinct from line-based keys which are 1-based.

### Fingerprint Selection

Priority order, deterministic within each level:

1. **partialFingerprints** — sort keys alphabetically, take first non-empty value (Qodana's `equalIndicator/v1`, CodeQL's `primaryLocationLineHash`)
2. **fingerprints** — sort keys alphabetically, take first non-empty value (Snyk's `"0"`, Semgrep's `matchBasedId/v1`)
3. **Content hash** — SHA-256 of `{ruleId}:{path}:{startLine}:{message}` (fallback for sonar-tools, Roslyn, Trivy, ESLint)

The fingerprint disambiguates multiple findings of the same rule on the same line. Sorted key selection ensures determinism regardless of dictionary iteration order.

### Stability

Same scan → same keys. `ON CONFLICT(semantic_key) DO UPDATE` is a no-op when nothing changed. Only genuinely new, changed, or expired findings cause writes.

---

## Write Pattern

Source-wide replacement in one transaction. The existing `ReplaceAnnotations` is per-document (useful when a format analyzer re-runs on one file). Source-wide replacement is a different pattern: expire everything from a source across all documents, upsert the new set.

```csharp
store.WriteTransaction((conn, tx) =>
{
    // 1. Collect new semantic keys
    var newKeys = annotations.Select(a => a.SemanticKey!).ToHashSet();

    // 2. Delete expired: same source+kind, key not in new set
    //    For 1K+ keys, use a temp table instead of IN-list
    var expired = DeleteExpiredAnnotations(conn, tx, source, kind, newKeys);

    // 3. Write spans (bulk appender)
    AppendSpans(conn, spans);

    // 4. Upsert annotations (ON CONFLICT(semantic_key) DO UPDATE)
    //    Preserves created_at for unchanged rows
    var (inserted, updated) = UpsertAnnotations(conn, tx, annotations);

    return new AnnotationReplaceResult(inserted, updated, expired);
});
```

**Why not delete-all + insert-all?** Delete-all loses `created_at` on unchanged findings (misleading provenance — findings appear "new" every import), writes every row even when nothing changed (wasted I/O), and can't distinguish new from updated in the response. Semantic key comparison is cheap and preserves the full lifecycle.

### Annotation Field Mapping

| Source | Annotation field | Notes |
|--------|-----------------|-------|
| Computed | `semantic_key` | `{source}:{ruleId}:{path}:{line}:{fingerprint}` |
| Fixed | `kind` | Always `"lint"` |
| Resolved level | `severity` | `error` / `warning` / `info` (from `note`) / `hint` (from `none`) |
| Normalized source | `source` | Slug: `"snyk-code"`, `"qodana-jvm"` |
| Verbatim | `rule_id` | SARIF `ruleId` |
| Fallback chain | `message` | text → markdown → messageStrings |
| Graph lookup | `scope_document_id` | Resolved document or synthetic unresolved |
| Deferred | `target_node_id` | Symbol anchoring — future plan |
| Created span | `target_span_id` | When region has startLine |
| Fallback URI | `target_uri` | For unresolved paths |
| Structured payload | `data` | Everything else (see below) |

### Data Payload

Standard fields are for `WHERE` and `ORDER BY`. The `data` payload is for inspection after finding a result.

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
    "properties": {}
  },
  "partialFingerprints": { "equalIndicator/v1": "a1b2c3..." },
  "fingerprints": { "0": "f5323d...", "1": "f0155d..." },
  "codeFlows": [],
  "relatedLocations": [],
  "fixes": [],
  "properties": { "priorityScore": 908 }
}
```

---

## Transport Improvements

Not SARIF-specific — these improve the import response for all import types.

`ImportResponse` gains two optional fields:

| Field | SARIF imports | VFS imports (github://, local://) |
|-------|---------------|-----------------------------------|
| `message` | Pre-formatted summary (counts, resolution stats) | `"Importing N files from source — operation {id}"` |
| `operation_id` | Not set (synchronous) | Set immediately (agent polls `_operations()` for progress) |

SARIF imports complete synchronously and return the full summary in `message`. VFS imports return immediately with an `operation_id` — the agent can proceed while indexing happens and check progress via `SELECT * FROM _operations()`.

### Response Format

```
Imported 47 findings from snyk-code
  12 error, 28 warning, 7 info
  45 resolved to indexed files, 2 unresolved
  15 new, 8 updated, 12 unchanged, 12 expired
```

Multi-source SARIF files: one block per source. The MCP tool displays `message` directly.

---

## Project Structure

```
src/
  RepoQL.Sarif/                          ← new project
    SarifNormalizer.cs                   ← ISarifNormalizer: pure function
    SarifImportService.cs                ← ISarifImportService: orchestrator
    Normalization/
      PathNormalizer.cs                  ← uriBaseId resolution, scheme stripping, relativization
      RuleCollector.cs                   ← driver + extensions → unified rule lookup
      SeverityResolver.cs                ← result/rule/default cascade
      SourceIdentifier.cs                ← tool.driver.name → slug
    Models/
      NormalizedResult.cs                ← output model types
      ProducerMap.cs                     ← known producer name → slug (data table, not switch)
```

**Dependencies:**
- `RepoQL.Sarif` → `RepoQL.Contracts` (Annotation, Span, RepoUri)
- `RepoQL.Sarif` → `RepoQL.Data.DuckDB` (DuckDbDataStore — import service only, not normalizer)
- No dependency on `RepoQL.Indexing` or `RepoQL.FileSystem`

The normalizer needs only `System.Text.Json` and a repo root path string. The import service needs `DuckDbDataStore` for writes and `RepoUri` for path resolution.

---

## Constraints

| Constraint | Source | Impact |
|------------|--------|--------|
| Single writer | CLAUDE.md | All writes through `DuckDbDataStore.WriteTransaction` |
| Schema frozen | CLAUDE.md | `annotation` table has everything needed: `semantic_key`, `kind`, `severity`, `source`, `rule_id`, `message`, `data`, `scope_document_id`, `target_span_id`, `target_uri` |
| Not a VFS import | Architecture | SARIF produces annotations, not files. No `IVirtualFileSystemImporter`, no file indexing pipeline |
| Host-side processing | Two-process model | Real work in the gRPC host, not the MCP client |
| Errors never cascade | CLAUDE.md | One malformed result never stops the import of others |
| Docs with features | CLAUDE.md | Ships with `help://` documentation: `read("help:///tools/import/sarif.md", 2000)` |
| No SARIF SDK | Performance | `System.Text.Json` DOM access — avoids heavy transitive dependency for a field subset |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| `System.Text.Json` DOM | Microsoft SARIF SDK | SDK is 200KB+ with transitive deps. We need ~10 fields. JSON DOM gives exactly the access needed |
| Source-wide expiration | File-scoped expiration | Full scans are the norm (Snyk, CodeQL, Qodana, Semgrep all scan everything in standard CI). File-scoped adds complexity for the rare partial scan case |
| New `RepoQL.Sarif` project | Classes in `RepoQL.Indexing` | SARIF is annotation-only, not file indexing. Normalizer has no DuckDB dependency — clean separation |
| `ReplaceAnnotationsBySource` | Reusing per-document `ReplaceAnnotations` | Existing method is per-document. Source-wide replacement can't be composed from per-document calls (can't expire findings in documents that had results before but don't now) |
| Synthetic unresolved document | Dropping unresolved findings | Unresolved findings are valuable. Agent sees them, indexes missing files, re-imports |
| SHA-256 content hash fallback | Rejecting fingerprint-less results | 4 of 8 producers have no fingerprints. Content hash is stable enough for re-import deduplication |
| Deterministic key sort | Arbitrary dictionary iteration | Same SARIF → same semantic key, regardless of platform or runtime |

## Alternatives Considered

**SARIF SDK (Microsoft.CodeAnalysis.Sarif):** Full object model with validation and v1→v2 conversion. Rejected: heavy dependency for a subset of fields. The SDK normalizes structure but not semantic content across tools.

**IVirtualFileSystemImporter:** Would fit existing dispatch. Rejected: SARIF produces annotations, not files. The VFS interface returns a `CompositeFileSystemMount`. Forcing annotations through it would be architecturally dishonest.

**Per-document write loop (reuse ReplaceAnnotations):** Loop over documents, call existing method per document. Rejected: can't expire findings in documents that had results before but don't in the new scan. Source-wide replacement is fundamentally different from per-document replacement.

**Separate `sarif-import` tool:** New MCP tool. Rejected: `import` already handles URI-based dispatch. Adding a scheme is simpler. Agents already know `import`.

---

## Risks

| Risk | Mitigation |
|------|------------|
| Large SARIF files (50MB+) | V1 loads into memory. GitHub enforces 10MB compressed. Stream-parse if needed later |
| Unknown producer | Slugify fallback handles any tool name. Path cascade handles common patterns. Add to ProducerMap when encountered |
| Many unresolved paths | Warning in response. Agent can index the repo first, then re-import SARIF |
| Semantic key instability across tool versions | Key components (source, ruleId, path, line, fingerprint) are stable across scanner re-runs. Content hash fallback is deterministic |
| Large transaction (10K+ findings) | Temp table for key sets instead of IN-list. DuckDB bulk appender for spans |

## Extension Points

| Extension | Mechanism |
|-----------|-----------|
| New producer | Add entry to `ProducerMap` (data table, not code change) |
| Partial scan imports | `partial: true` flag — scopes expiration to files present in import, not source-wide |
| Custom source override | `import("sarif:///path", source: "my-scanner")` — override auto-detected slug |
| Additional annotation kinds | Map SARIF `kind` property to different annotation kinds (V1 always uses `"lint"`) |
| SARIF spec revisions | `ISarifNormalizer` — swap implementation |

---

## Verification

| Level | What | How |
|-------|------|-----|
| Unit | Path normalization per producer | Feed raw paths → assert relative output. Snyk, sonar-tools, Roslyn, Trivy patterns |
| Unit | Rule collection | Qodana SARIF (rules on extensions) → all rules found. sonar-tools (no rules) → empty, no error |
| Unit | Severity resolution | Explicit level, rule default, both absent → assert correct cascade |
| Unit | Source identification | Known names → correct slugs. Unknown → valid slugified slug |
| Unit | Semantic key computation | Same result twice → same key. Different line → different key |
| Unit | Fingerprint selection | Both dictionaries present → partialFingerprints wins. Neither → content hash |
| Integration | End-to-end import | Index repo, import SARIF, query `annotations WHERE kind = 'lint'` → verify fields |
| Integration | Re-import expiration | Import 5 findings, re-import with 4 → 1 expired |
| Integration | Idempotent re-import | Import same file twice → zero changes second time |
| Integration | Unresolved paths | Finding on non-indexed file → `target_uri` set, warning returned |
| Integration | Multi-run aggregation | Same source across 2 runs → one aggregated write, no clobber |

---

## Related

- [Schema](../../Schema.md) — `annotation` table, `semantic_key` uniqueness, `annotations` view
- [Vocabulary](../../Vocabulary.md) — `annotation.kind = 'lint'`, severity mapping
- [SARIF Import North Star](../../north-star/sarif-import.md)
- [SARIF Producer Landscape](../../research/sarif-producer-landscape.md)
- [SARIF Import Flow](../../flows/future/sarif/sarif-import.md)
- [SARIF Normalization Flow](../../flows/future/sarif/sarif-normalization.md)
- [SARIF Re-Import Flow](../../flows/future/sarif/sarif-reimport.md)
- [SARIF Query Patterns](../../flows/future/sarif/sarif-query-patterns.md)
