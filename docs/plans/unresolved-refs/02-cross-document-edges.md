# Plan: Cross-Document Reference Edges

Implements: [Design §2 — Emit REFERS_TO Edges](../../designs/current/unresolved-ref-detection.md#2-emit-refers_to-edges-for-cross-document-links) and [Design §5 — Updated Views](../../designs/current/unresolved-ref-detection.md#5-updated-views)

## Scope

**Covers:**
- Emit `DstUri`-only REFERS_TO edges for cross-document links in `MarkdownLoader.Materialize`
- URI resolution for relative paths against the document's own URI
- `EdgeKey` for idempotent upserts
- Update `markdown_links` view with `is_resolved` and `target_uri` columns

**Does not cover:**
- Cross-document reference resolver (Plan: 03)
- REFERS_TO edges in other format loaders (Docx, PDF, csproj — future increments)
- External URL edges (design decision: out of scope)

## Enables

Once cross-document edges exist:
- The reference graph is explicit — `SELECT * FROM edge WHERE type = 'REFERS_TO' AND destination_node_id IS NULL` shows all unresolved references
- `markdown_links` view exposes resolution status — agents can query `WHERE is_resolved = false`
- **Plan 03** has edges to work with — the cross-document resolver queries these edges
- "What links to this file?" becomes a graph query instead of string matching on `href`

## Prerequisites

- None — this is independent of Plan 01. Can be built in parallel.

## North Star

Every cross-document reference in markdown is a REFERS_TO edge in the graph. The reference graph is complete and queryable without parsing node properties.

## Done Criteria

### Edge Emission

- When a markdown link `href` is a relative path (not starting with `#`, not starting with `http://` or `https://`), `MarkdownLoader.Materialize` shall emit a REFERS_TO edge with `DstUri` set and `DstId` null
- The REFERS_TO edge shall have `IsComposition = false`
- The REFERS_TO edge `SrcId` shall be the `md_link` node ID
- The REFERS_TO edge `ScopeDocumentId` shall be the document node ID
- When the `href` contains a fragment (`other.md#section`), the edge `DstUri` shall be the path without the fragment, and `Props["anchor"]` shall contain the fragment as a slugified string (using `MarkdownTextUtilities.Slug()`, matching heading slug convention)
- When the `href` does not contain a fragment, the edge `Props` shall not contain an `anchor` key

### URI Resolution

- The `DstUri` shall be resolved relative to the document's own URI
- When `href` is `./sibling.md` and the document is `file:///docs/guide.md`, `DstUri` shall be `file:///docs/sibling.md`
- When `href` is `../api/ref.md` and the document is `file:///docs/guides/intro.md`, `DstUri` shall be `file:///docs/api/ref.md`
- When URI resolution fails (malformed href), no edge shall be emitted and no exception shall propagate

### EdgeKey

- The REFERS_TO edge `EdgeKey` shall be `"{srcNodeId}→{dstUri}"` (source link node ID, arrow, resolved DstUri string)
- When the same document is reindexed, existing REFERS_TO edges from its links shall be replaced via `EdgeKey` matching, not duplicated

### Existing Behavior

- When a markdown link `href` starts with `#`, existing local anchor behavior shall be unchanged (resolved REFERS_TO edge with `DstId` if heading found)
- When a markdown link `href` starts with `http://` or `https://`, no REFERS_TO edge shall be emitted
- Image links shall not emit REFERS_TO edges (existing behavior — images are content references, not document references)

### markdown_links View

- The `markdown_links` view shall include an `is_resolved` column (boolean): `true` when the REFERS_TO edge has `destination_node_id` set, `false` otherwise
- The `markdown_links` view shall include a `target_uri` column: the `destination_uri` from the REFERS_TO edge, null for links with no outbound REFERS_TO edge (external URLs, images)
- When a link has no REFERS_TO edge (external URL), both `is_resolved` and `target_uri` shall be null

### Tests

- When a markdown file contains `[link](./other.md)`, indexing shall produce a REFERS_TO edge with `DstUri` pointing to the resolved path of `other.md`
- When a markdown file contains `[link](./other.md#section)`, the edge `DstUri` shall point to `other.md` and `Props["anchor"]` shall be `"section"`
- When a markdown file contains `[link](https://example.com)`, no REFERS_TO edge shall be emitted from that link node
- When a markdown file contains `[link](#local)`, existing local anchor edge behavior shall be preserved
- When `href` is malformed (e.g., contains invalid URI characters), no edge shall be emitted and indexing shall complete without error
- The `markdown_links` view shall return `is_resolved = false` for a cross-document link whose target is not indexed

## Constraints

- **Relative paths only** — no edges for `http://`, `https://`, `mailto:`, or other external schemes. Design decision.
- **No image links** — `is_image = true` links are content embeds, not document references
- **URI normalization** — use `RepoUri` for resolution to stay consistent with the rest of the system
- **Schema frozen** — changes are to the view definition only, not to the `edge` table

## References

- [Design](../../designs/current/unresolved-ref-detection.md) — §2 and §5
- [Detection flow](../../flows/current/indexing/unresolved-ref-detection.md) — stage 1 (parsing)
- `src/Formats/RepoQL.Formats.Markdown/MarkdownLoader.cs` — `Materialize()` method
- `src/Formats/RepoQL.Formats.Markdown/Schema/markdown_views.sql` — current view definition
- `src/RepoQL.Contracts/Models/Edge.cs` — `DstUri`, `EdgeKey`, `Validate()`

## Error Policy

Malformed `href` values must not prevent indexing. If URI resolution fails, skip the edge for that link and continue. Log at debug level — malformed hrefs are common in real repositories and not worth warning about.
