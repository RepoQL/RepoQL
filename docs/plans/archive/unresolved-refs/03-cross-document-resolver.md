# Plan: Cross-Document Reference Resolver

Implements: [Design §3 — Cross-Document Reference Resolver](../../designs/current/unresolved-ref-detection.md#3-cross-document-reference-resolver) and [Design §4 — Rule ID Convention](../../designs/current/unresolved-ref-detection.md#4-rule-id-convention)

## Scope

**Covers:**
- `CrossDocumentReferenceResolver` as `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>`
- Registration in multi-file analysis pipeline
- Resolution of `DstUri` edges against the indexed graph
- DstId backfill when targets resolve
- Three rule IDs: `unresolved-ref`, `unresolved-anchor`, `ambiguous-anchor`
- Annotation source string for scoped replacement

**Does not cover:**
- External URL validation (design decision: out of scope)
- Targeted re-analysis optimization (design defers to full epoch scan)
- Format-specific resolution logic beyond graph queries
- Recovery features (fuzzy matching, git history — north-star declarations for future work)

## Enables

Once the cross-document resolver exists:
- `SELECT * FROM Annotations WHERE rule_id LIKE '%/unresolved-%' OR rule_id LIKE '%/ambiguous-%'` returns all reference integrity issues across the repository
- References that were unresolved become resolved when their targets are indexed — annotations self-heal across epochs
- Agents can distinguish "file missing" from "file exists but heading missing" from "ambiguous heading"
- The reference graph becomes fully navigable — resolved edges have `DstId` set

This is the capstone increment. With all three plans complete, the north-star's core Detection and Querying declarations are satisfied.

## Prerequisites

- **Plan 01** — format analyzers must run in production (local anchor detection is live)
- **Plan 02** — cross-document REFERS_TO edges must exist (the resolver reads them)

## North Star

Every cross-document reference is validated. Unresolved references surface as lint annotations with enough information to diagnose and fix.

## Done Criteria

### CrossDocumentReferenceResolver

- The `CrossDocumentReferenceResolver` shall implement `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>`
- The resolver shall query all REFERS_TO edges where `destination_uri IS NOT NULL` and `scope_document_id` matches the item's document node (both unresolved and previously resolved edges)
- The resolver shall use the annotation source string `"RepoQL.CrossDocRef"`

### Target Verification

- When an edge has `DstId` set, the resolver shall verify the target node still exists in the graph
- When the target node no longer exists (deleted/pruned), the resolver shall clear `DstId` to null and treat the edge as unresolved

### File Resolution

- When a REFERS_TO edge's `DstUri` does not match any document URI in the graph, the resolver shall emit an annotation with `rule_id = '{format}/unresolved-ref'`
- When a REFERS_TO edge's `DstUri` matches a document URI in the graph and the edge has no `Props["anchor"]`, the resolver shall set `DstId` to the target document's node ID
- The annotation `message` shall include the unresolved `href` value
- The annotation `Target.NodeId` shall be the source link node (the `md_link` that contains the reference)

### Anchor Resolution

- When a REFERS_TO edge's `DstUri` matches a document and `Props["anchor"]` is set, the resolver shall check whether the target document contains a heading with that slug
- When the heading slug does not exist in the target document, the resolver shall emit an annotation with `rule_id = '{format}/unresolved-anchor'`
- When the heading slug exists exactly once, the resolver shall set `DstId` to the heading node ID
- When the heading slug exists more than once in the target document, the resolver shall emit an annotation with `rule_id = '{format}/ambiguous-anchor'` and resolve `DstId` to the first occurrence

### Format Detection

- The resolver shall determine the rule ID prefix from the item's `MediaType.Kind` using a known mapping:

  | MediaType kind | Rule ID prefix |
  |----------------|---------------|
  | `markdown.doc` | `markdown` |
  | `word.document` | `docx` |
  | `pdf.document` | `pdf` |
  | `csharp.project` | `csproj` |

- When the kind is not in the mapping, the resolver shall use the first segment before the dot as the prefix (e.g., `yaml.config` → `yaml`)
- When `MediaType` or `Kind` is null, the resolver shall use `unknown` as the prefix

### Annotation Shape

- All annotations shall use `kind = "lint"`
- All annotations shall default to `severity = "warning"`
- The `SemanticKey` shall follow `"{documentUri}#rule:{ruleId}@node:{linkNodeId}"` to ensure uniqueness per reference (consistent with `MarkdownAnalyzer`'s existing convention — the link node is the user-visible element)
- The `Data` JSON shall include `href` (the original link text) and `target_uri` (the resolved `DstUri`)

### Re-Analysis Behavior

- When the resolver runs on a document whose targets have become available since last analysis, previously unresolved edges shall be resolved and no annotation emitted for them
- When the resolver runs on a document whose targets have been deleted, new annotations shall be emitted for newly broken references
- The `AnnotationResultWriter` scoped delete-then-insert shall clear stale annotations from source `"RepoQL.CrossDocRef"` before inserting the new set
- Edge updates (`DstId` backfill, `DstId` clearing for deleted targets) shall be performed as side effects via `DuckDbDataStore` during the resolver's processing, before returning the annotation array

### DI Registration

- The `CrossDocumentReferenceResolver` shall be registered as `IAsyncPipeline<IAnnotatedArtifact, Annotation[]>` in the indexing service collection
- The `MultiFileAnalysisPipeline` shall receive the resolver as a processor

### Tests

- When a markdown file links to `./exists.md` and that file is indexed, no `unresolved-ref` annotation shall be emitted and the edge `DstId` shall be set
- When a markdown file links to `./missing.md` and that file is not indexed, an annotation with `rule_id = 'markdown/unresolved-ref'` shall be emitted
- When a markdown file links to `./exists.md#valid-heading` and the heading exists, no annotation shall be emitted and `DstId` shall point to the heading node
- When a markdown file links to `./exists.md#invalid-heading` and the heading does not exist, an annotation with `rule_id = 'markdown/unresolved-anchor'` shall be emitted
- When a target document has duplicate headings with the same slug, an annotation with `rule_id = 'markdown/ambiguous-anchor'` shall be emitted
- When a previously missing target is indexed in a later epoch, re-analysis shall clear the `unresolved-ref` annotation
- When a previously valid target is deleted, re-analysis shall clear the edge's `DstId` and emit a new `unresolved-ref` annotation
- When an edge has `DstId` set but the target node no longer exists in the graph, the resolver shall treat it as unresolved

## Constraints

- **No network I/O** — external URL validation is explicitly out of scope per design
- **Full epoch re-analysis** — the resolver re-checks all `DstUri` edges (unresolved AND previously resolved) for each document in the epoch. This catches deleted targets. Targeted optimization deferred per design.
- **Single writer** — edge updates (`DstId` backfill) go through `DuckDbDataStore`
- **Idle processing only** — the resolver runs in `MultiFileAnalysisPipeline`, after the hot path has drained for the epoch
- **Annotation source isolation** — `"RepoQL.CrossDocRef"` is separate from format analyzer sources (e.g., `"RepoQL.Markdown"`). Scoped replacement must not delete single-file analysis annotations.

## References

- [Design](../../designs/current/unresolved-ref-detection.md) — §3 and §4
- [Detection flow](../../flows/current/indexing/unresolved-ref-detection.md) — stage 4 (cross-document validation)
- [Resolution flow](../../flows/current/indexing/unresolved-ref-resolution.md) — how annotations get cleared
- [Multi-file analysis flow](../../flows/current/indexing/multi-file-analysis.md) — pipeline mechanics
- `src/RepoQL.Core/Analysis/AnnotationResultWriter.cs` — scoped delete-then-insert
- `src/Indexing/RepoQL.Indexing/Indexing/Pipelines/Analysis/MultiFileAnalysisPipeline.cs` — pipeline host

## Error Policy

Per-document failures must not block other documents. If the resolver throws on a document, log the exception with the document URI and continue to the next document. Edge update failures (DstId backfill) are non-fatal — the edge stays unresolved and will be retried on the next epoch.
