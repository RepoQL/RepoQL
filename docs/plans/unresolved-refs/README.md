# Unresolved Reference Detection Plans

Implements: [Unresolved Reference Detection Design](../../designs/current/unresolved-ref-detection.md)

Three increments. Each is independently shippable and delivers queryable value.

| Plan | What it delivers | Depends on |
|------|-----------------|------------|
| [01 — Wire Format Analyzers](01-wire-format-analyzers.md) | All existing format analyzers run in production | Nothing |
| [02 — Cross-Document Edges](02-cross-document-edges.md) | REFERS_TO edges for cross-document links in markdown | Nothing |
| [03 — Cross-Document Resolver](03-cross-document-resolver.md) | Multi-file analyzer validates cross-document references | 01, 02 |

## After All Three

```sql
-- Every reference integrity issue, any format
SELECT source_uri, rule_id, severity, message
FROM Annotations
WHERE rule_id LIKE '%/unresolved-%' OR rule_id LIKE '%/ambiguous-%'

-- What's linking to a missing file?
SELECT file_uri, href, link_text
FROM markdown_links
WHERE is_resolved = false AND target_uri IS NOT NULL
```

## Format Coverage

These three plans deliver full coverage for **markdown**. Other formats (Docx, PDF, csproj) already have partial infrastructure (link extraction, `DstUri` edges in some cases) but are not covered by these plans. Once Plan 03 ships, adding a format is one change: emit `DstUri` REFERS_TO edges in that format's `Materialize()` — the cross-document resolver handles the rest.

## North Star Declarations Addressed

From `docs/north-star/unresolved-refs.md`:

| Declaration | Plan |
|-------------|------|
| Find unresolved file references | 03 |
| Find unresolved anchor references | 01 (local), 03 (cross-doc) |
| Find unresolved cross-document anchor references | 03 |
| Find ambiguous anchors | 01 (local), 03 (cross-doc) |
| Find all unresolved references in one query | 03 |
| Filter by severity, format, rule | 01 (infrastructure) |
| Distinguish "target doesn't exist" from "anchor doesn't" | 03 (three rule IDs) |
| Control which rules are active and at what severity | 01 (`.editorconfig` integration) |
