---
description: Design for detecting and resolving unresolved references across all formats in the indexing pipeline
tags: [unresolved-ref, lint, annotations, cross-document, references, design]
audience: { human: 55, agent: 45 }
purpose: { design: 85, flow: 15 }
---

# Unresolved Reference Detection Design

## North Star

An agent finds every reference that doesn't resolve — across all files, all formats, all reference types — in one query, without opening a single file.

See `docs/north-star/unresolved-refs.md` for the full declaration set.

## Context

References are links, imports, and dependencies that name a target. When the target doesn't exist, that's an unresolved reference. Surfacing these as lint annotations makes reference integrity a queryable surface.

The architecture is mostly in place. Format loaders already extract link nodes. `Edge.Validate()` supports `DstUri`-only edges for deferred resolution. The `IAnalyzer` interface exists for graph-aware analysis. The annotation system supports scoped replacement. What's missing is wiring.

This design addresses five gaps:

1. `IFormatAnalyzer` implementations don't run in production (bridge exists only in test code)
2. Non-anchor links get no REFERS_TO edges — the cross-document reference graph doesn't exist
3. No multi-file analyzer checks cross-document references
4. The existing rule ID (`markdown/broken-link`) doesn't match the `unresolved-ref` convention
5. The `markdown_links` view has no resolution status

### Input Documents

| Document | What it provides |
|----------|-----------------|
| `docs/north-star/unresolved-refs.md` | What great looks like — declarations to evaluate against |
| `docs/flows/current/indexing/unresolved-ref-detection.md` | Detection flow: parsing → single-file → multi-file |
| `docs/flows/current/indexing/unresolved-ref-resolution.md` | Resolution flow: target changes → re-analysis → annotations cleared |
| `docs/flows/current/indexing/single-file-analysis.md` | Existing single-file analysis pipeline |
| `docs/flows/current/indexing/multi-file-analysis.md` | Existing multi-file analysis pipeline |

## Constraints

- Schema is frozen. No new tables. Extend via views, macros, UDFs.
- Single writer to DuckDB through `DuckDbDataStore`.
- Annotations use `kind = "lint"`, differentiated by `rule_id`.
- One bad file never breaks anything else. Parse failures, analyzer crashes — all isolated.
- No network I/O in the pipeline. External URL validation is out of scope.
- Rule severity is configurable via `.editorconfig`.

## Design

### 1. Wire Format Analyzers into Production

`FormatRegistryAnalyzer` bridges `IFormatRegistry.TryResolveByMedia` → `descriptor.Analyzer.AnalyzeAsync`. It exists in `RepoQL.Testing` as `IndexedRepoBuilder.FormatRegistryAnalyzer`. Move it to `RepoQL.Core` and register it as `IAsyncPipeline<IParsedArtifact, Annotation[]>`.

```
FormatRegistryAnalyzer : IAsyncPipeline<IParsedArtifact, Annotation[]>
├── Receives: IParsedArtifact (has Records, MediaType, document_model stash)
├── Resolves: IFormatRegistry.TryResolveByMedia(item.MediaType) → FormatDescriptor
├── Calls: descriptor.Analyzer.AnalyzeAsync(documentModel, context)
├── Returns: Annotation[] mapped from AnalysisResult[]
└── Failure: Analyzer throws → log, return empty array
```

This unblocks all existing `IFormatAnalyzer` implementations — `MarkdownAnalyzer`, `CSharpAnalyzer`, `CsProjAnalyzer`, `JsonSecretDetector`, etc. — in production with no per-format changes.

**Registration**: One line in `RepoIndexerServiceCollectionExtensions`:
```csharp
services.AddSingleton<IAsyncPipeline<IParsedArtifact, Annotation[]>, FormatRegistryAnalyzer>();
```

### 2. Emit REFERS_TO Edges for Cross-Document Links

During `Materialize()`, format loaders emit `DstUri`-only REFERS_TO edges for references that point outside the document. This makes the reference graph explicit in the edge table.

**Scope**: Only references that could resolve within the repository. Relative file paths (`./other.md`, `../api/guide.md`), repo-absolute paths, and cross-document anchors (`other.md#section`). Not external URLs (`https://...`) — those are not resolvable within the graph.

For markdown, the change is in `MarkdownLoader.Materialize`:

```
For each link in state.Surface.Links:
  if href starts with '#':
    (existing) resolve locally, emit DstId edge if found
  else if href is a relative or repo-absolute path:
    (new) emit REFERS_TO edge with DstUri = resolved path, DstId = null
    if href contains '#': store slugified anchor in Edge.Props["anchor"]
  else:
    (external URL) no edge — href stays in node Props only
```

**URI resolution**: Relative paths are resolved against the document's own URI. `./sibling.md` in `file:///docs/guide.md` becomes `file:///docs/sibling.md`. The fragment (`#section`) is stripped from the URI, slugified (via `MarkdownTextUtilities.Slug()`), and stored in `Edge.Props["anchor"]`. Slugification ensures the stored value matches heading slugs in the target document.

**EdgeKey**: `"{srcNodeId}→{dstUri}"` ensures idempotent upserts on reindex.

Other formats follow the same pattern: emit `DstUri`-only edges for resolvable references during their own `Materialize()` phase.

### 3. Cross-Document Reference Resolver

A new multi-file analyzer that runs during idle processing. Registered as `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>`.

```
CrossDocumentReferenceResolver : IAsyncPipeline<IAnnotatedArtifact, Annotation[]>
├── Query: SELECT edges WHERE type = 'REFERS_TO' AND DstUri IS NOT NULL
│          AND scope_document_id = item.DocumentNodeId
├── For each edge (unresolved OR previously resolved):
│   ├── If DstId is set: verify target node still exists
│   │   └── Target gone → clear DstId, treat as unresolved below
│   ├── Check: Does DstUri exist in Files view?
│   │   ├── No → annotation: {format}/unresolved-ref
│   │   └── Yes, edge has Props["anchor"]:
│   │       ├── Check: Does heading slug exist in target document's nodes?
│   │       │   ├── No → annotation: {format}/unresolved-anchor
│   │       │   └── Exactly one → resolve: UPDATE edge SET DstId = heading_node_id
│   │       └── Check: Is heading slug duplicated?
│   │           └── Yes → resolve DstId to first occurrence + annotation: {format}/ambiguous-anchor
│   └── Yes, no anchor → resolve: UPDATE edge SET DstId = document_node_id
├── Side effects: edge DstId updates via DuckDbDataStore
├── Returns: Annotation[] for unresolved and ambiguous references
└── Failure: Per-item exception logged, item skipped
```

**Format detection**: The resolver reads `item.MediaType` to determine the rule ID prefix. The resolver is format-agnostic — it works on any REFERS_TO edge with `DstUri`.

| MediaType kind | Rule ID prefix |
|----------------|---------------|
| `markdown.doc` | `markdown` |
| `word.document` | `docx` |
| `pdf.document` | `pdf` |
| `csharp.project` | `csproj` |
| (unknown) | `unknown` |

The prefix is derived from `MediaType.Kind` — the first segment before the dot, or the full kind if no dot.

**Edge resolution**: When a target is found, the resolver updates the edge's `DstId` via `DuckDbDataStore`. This is a write during idle processing, which is safe because the hot path has drained for the epoch.

**Re-analysis semantics**: On subsequent epochs, the resolver re-checks all `DstUri` edges for the document — both unresolved and previously resolved. For resolved edges, it verifies the target node still exists. If the target has been deleted (pruned), `DstId` is cleared and the edge is treated as unresolved. This prevents stale false negatives when targets are removed.

**Write path**: The resolver returns `Annotation[]` through the pipeline. Edge updates (`DstId` backfill and clearing) are side effects performed via `DuckDbDataStore` during processing. Annotations are persisted by the pipeline's existing result handler, which calls `AnnotationResultWriter` with source `"RepoQL.CrossDocRef"` for scoped replacement.

### 4. Rule ID Convention

Three rules, one `kind`:

| Rule ID | When emitted | Stage |
|---------|-------------|-------|
| `{format}/unresolved-ref` | Target file/document doesn't exist | Single-file (local anchors) or multi-file (cross-doc) |
| `{format}/unresolved-anchor` | Target file exists but named anchor doesn't | Multi-file only |
| `{format}/ambiguous-anchor` | Target heading slug appears more than once (DstId resolved to first occurrence) | Single-file or multi-file |

All annotations use `kind = "lint"`. Severity defaults to `warning`, configurable via `.editorconfig`.

**Rename**: `markdown/broken-link` → `markdown/unresolved-ref`. Single breaking change, no migration needed — annotations are regenerated on reindex.

**Semantic key convention**: `"{documentUri}#rule:{ruleId}@node:{linkNodeId}"` — same pattern as the existing `MarkdownAnalyzer`.

### 5. Updated Views

Extend `markdown_links` to surface resolution status:

```sql
CREATE OR REPLACE VIEW markdown_links AS
SELECT
  d.uri AS file_uri,
  CASE WHEN s.start_line IS NOT NULL
    THEN d.uri || '#line=' || CAST(s.start_line AS VARCHAR)
    ELSE NULL
  END AS link_uri,
  json_extract_string(l.properties, '$.href') AS href,
  json_extract_string(l.properties, '$.text') AS link_text,
  json_extract_string(l.properties, '$.title') AS link_title,
  -- Resolution status from REFERS_TO edge
  ref.destination_node_id IS NOT NULL AS is_resolved,
  ref.destination_uri AS target_uri,
  s.start_line, s.end_line, s.start_column, s.end_column
FROM node l
JOIN edge e ON e.destination_node_id = l.id
           AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON l.span_id = s.id
LEFT JOIN edge ref ON ref.source_node_id = l.id AND ref.type = 'REFERS_TO'
WHERE l.kind = 'md_link';
```

New columns: `is_resolved` (bool), `target_uri` (the `DstUri` from the REFERS_TO edge, null for links with no edge — external URLs).

Agents query unresolved references either way:

```sql
-- Via annotations (recommended — includes message and severity)
SELECT * FROM Annotations WHERE rule_id LIKE '%/unresolved-%' OR rule_id LIKE '%/ambiguous-%'

-- Via the link graph (raw resolution status)
SELECT * FROM markdown_links WHERE is_resolved = false AND target_uri IS NOT NULL
```

### Cross-Cutting Concerns

**Annotation source strings**: Each format's analyzer uses its own source string (`RepoQL.Markdown`, `RepoQL.Docx`, etc.). The cross-document resolver uses a separate source string (`RepoQL.CrossDocRef`) so its annotations can be replaced independently from single-file annotations for the same document.

**Ordering**: Single-file analysis (hot path) runs before multi-file analysis (idle path). A local anchor can be flagged as unresolved in single-file analysis, and a cross-document reference for the same document gets checked later. No conflict — they use different rule IDs and different sources.

**ReadOnly items**: Imported repos (`github://`) are read-only and skip analysis. Their files are indexed as targets — local files can reference them — but the imported files themselves don't get analyzed for outbound unresolved references.

**Incremental behavior**: The scoped delete-then-insert pattern in `AnnotationResultWriter` handles incremental changes correctly. When a file is reindexed, its annotations from the same source are deleted and regenerated. When a target file changes, the source file is re-analyzed in the next epoch and stale annotations are replaced.

## Trade-offs

| Decision | What we get | What we give up |
|----------|------------|-----------------|
| REFERS_TO edges only for repo-resolvable links | Clean edge table, no noise from external URLs | External URL validation requires separate mechanism |
| Format-agnostic cross-doc resolver | One implementation works for all formats | Format-specific resolution logic (e.g., C# `using` → namespace) needs separate handling |
| Full epoch re-analysis | Simplicity, correctness | Redundant work when only one target changed — optimize later with inverted index |
| Three rule IDs | Agents can filter precisely | More rules to configure in `.editorconfig` |
| Edge resolution (DstId backfill) during idle | Reference graph becomes fully navigable | Write during idle processing (safe, but adds write surface) |

## Alternatives Considered

### A. Query md_link nodes directly instead of REFERS_TO edges

The cross-document resolver could scan `md_link` nodes with `href` in properties rather than checking edges. This avoids adding edges at parse time.

**Rejected because**: It keeps the reference graph implicit. Edges are the graph's native way to express relationships. Without REFERS_TO edges, queries like "what links to this file?" require joining through node properties and string matching on `href`, which is fragile and format-specific. With edges, it's `SELECT * FROM edge WHERE destination_uri = '...' AND type = 'REFERS_TO'`.

### B. Emit edges for external URLs too

Every link — including `https://example.com` — gets a REFERS_TO edge with `DstUri`.

**Rejected because**: External URLs can't resolve within the graph. They'd add edges that are permanently unresolvable, polluting queries. External URL validation is a separate concern (network I/O, rate limiting, caching) and should be a separate feature if added.

### C. Use `IAnalyzer` interface instead of `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>`

The `IAnalyzer` interface exists for graph-aware analysis. Wire it into the multi-file analysis pipeline.

**Deferred**: `IAnalyzer` takes a `containerUri` string, not an `IAnnotatedArtifact`. The multi-file pipeline expects `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>`. Bridging `IAnalyzer` into the pipeline is a broader refactor that benefits all analyzers, not just reference detection. Worth doing, but independently. The cross-document resolver can implement `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>` directly for now.

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| False positives during indexing | Target not yet indexed → unresolved annotation | Next epoch re-analysis clears it. Epoch boundary ensures this self-heals. The annotation `Data` JSON includes `"reason": "target_not_found"` — agents can cross-reference with UriRegistry to distinguish "not found" from "not yet indexed" if needed. |
| Performance on large repos | Many links × many files = many edge checks | Resolver queries by `scope_document_id` — one query per document, not per link. DuckDB handles this efficiently. |
| URI resolution edge cases | Relative paths with `..`, case sensitivity, URL encoding | Resolve URIs using the same `RepoUri` normalization used elsewhere. Delegate to `RepoUri.Resolve()`. |
| Rename of `markdown/broken-link` | Existing `.editorconfig` rules reference old ID | Annotations regenerate on reindex. Document the rename in release notes. |
| Edge writes during idle processing | Expands the write surface beyond `IndexingCommitter` | All idle writes go through `DuckDbDataStore` (single writer). No concurrency risk. |
