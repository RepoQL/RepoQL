# RepoQL.Formats.Mermaid

Mermaid diagram loader + analyzer for RepoQL with X‑ray summaries rendered via Liquid templates.

## Supported diagrams

- `flowchart` / `graph`: nodes, edges, subgraphs, class definitions, click statements.
- `sequenceDiagram`: participants, messages, simple block constructs (alt/opt/loop).
- `pie`: labeled slices with numeric values.

## What it does

- Tolerant parsing using a lightweight line scanner (see `MermaidLanguage`).
- Produces an AST (`MDocument`) with child statements (e.g., `FlowNodeDecl`, `FlowEdge`, `SeqParticipant`, `SeqMessage`, `PieEntry`).
- Materialization creates a `document` node with composition edges for parts and supporting edges for relations/messages.
- X‑ray fields on the Artifact are rendered via embedded templates:
  - `headline`: single line summary including diagram kind and key counts.
  - `summary`: small multi‑line description (type, size, counts per diagram type).
  - `structure`: compact outline of the diagram (nodes/participants/slices, up to 25 entries).
- Analyzer (`MermaidAnalyzer`) runs simple safety/quality rules:
  - `FlowchartEscapeLabelsRule`: promotes quoting labels that contain special characters.
  - `FlowSubgraphClosureRule`: checks subgraph/flow end pairing.
  - `SequenceAvoidBareEndRule` and `SequenceBlockClosureRule`: sequence block hygiene.
  - `PieSafetyRule`: validates basic value formatting.

## X‑ray templates

Embedded under `Templates/explore` and included as resources:

- `explore/headline.liquid`
- `explore/summary.liquid`
- `explore/structure.liquid`

Model keys available to templates:

- Common: `file_name`, `media_kind`, `media_base`, `size_bytes`, `line_count`, `diagram_kind`
- Flow: `node_count`, `edge_count`, `flow_nodes` (id, label, shape), `flow_edges` (src, dst, arrow, label)
- Sequence: `participant_count`, `message_count`, `participants` (name, alias), `messages` (from, to, arrow, text)
- Pie: `pie_count`, `slices` (label, value)

Example headline output:

```
diagram.mmd | mermaid.doc | 1.2 KB | diagram: flowchart | nodes: 7 | edges: 6
```

Example summary output:

```
Type: mermaid.doc
Size: 1.2 KB, Lines: 34
Diagram: flowchart
Flow: nodes 7, edges 6
```

Example structure output (flowchart):

```
Flowchart
- A: Start [(
- B: Process [
- C: End )]
```

## Configuration

Templates are loaded via `LiquidTemplateRenderer` using `EmbeddedFileProvider` scoped to `RepoQL.Formats.Mermaid.Templates`. To override or extend templates, either:

- Add a project reference to `RepoQL.Templating` and register a custom `ITemplateRenderer` in DI, or
- Pass a custom `ITemplateRenderer` into `MermaidLoader`.

## Notes

- X‑ray rendering is best‑effort and won’t block indexing if templates fail.
- Keep non‑headline summaries terse; richer details appear in `structure`.

