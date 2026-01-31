---
description: Plan for web UI Inspect view - file details, nodes, edges, annotations
tags: [ui, plan, inspect, edges, traversal]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Inspect View

Implements: [Web UI Design](../designs/web-ui.md) — Inspect View, IInspectService, Edge Traversal

## Scope

**Covers:**
- `IInspectService` interface and implementation
- Inspect view component showing file metadata
- Nodes display grouped by kind
- Edges display (outgoing and incoming) with click navigation
- Annotations display with severity indicators
- Embedding status display
- Edge traversal via NavigationState
- Back navigation

**Does not cover:**
- Blame display (Plan: web-ui-8-git)
- History display (Plan: web-ui-8-git)
- Similar files via embeddings (stretch goal)

## Enables

Once Inspect view exists:
- **File verification** — Developers can see what RepoQL extracted from any file
- **Edge traversal** — Click edges to navigate the graph
- **Link target for other views** — Search, Annotations can link to Inspect
- **Parser debugging** — Verify nodes/edges are correct for file type

## Prerequisites

- Plan: web-ui-1-foundation complete (NavigationState for edge traversal)
- gRPC `ExecuteRawQuery` for underlying queries

## North Star

Select a file, see everything RepoQL knows about it in under 200ms. Click an edge, land on the target file. Click back, return to where you were.

## Done Criteria

### IInspectService
- The InspectService shall accept a file URI
- The InspectService shall return `InspectResult` with metadata, nodes, edges, annotations, embeddings
- The InspectService shall execute queries in parallel for performance
- When file not found, the result shall include appropriate error

### Inspect View
- The Inspect view shall be accessible via navigation (route: `/inspect`)
- The Inspect view shall accept URI parameter from NavigationState
- The Inspect view shall display a URI input for manual entry
- When URI changes, the view shall load file data

### File Metadata Section
- The view shall display file URI as header
- The view shall display language/media type
- The view shall display line count and token estimate
- The view shall display embedding status (✓ Ready / ○ Pending / ✗ None)
- The view shall display headline from X-ray summary
- The view shall display structure (collapsible)

### Nodes Section
- The view shall display nodes grouped by kind
  - Classes, Interfaces, Methods, Functions, Sections, etc.
- Each node shall show name and line range (e.g., `[47-89]`)
- When no nodes, show "No structure extracted"

### Edges Section
- The view shall display outgoing edges (what this file references)
  - Show edge type (CALLS, IMPORTS, REFERS_TO, etc.)
  - Show target URI or symbol
  - Show source line number
- The view shall display incoming edges (what references this file)
  - Show edge type
  - Show source URI
- Each edge shall be clickable

### Edge Click (Traversal)
- When user clicks an edge, the view shall navigate to target
- Navigation shall use NavigationState.NavigateTo with target URI
- If target includes line number, NavigationParams shall include line
- After navigation, Inspect view shall load for new URI

### Back Navigation
- When CanGoBack is true, a Back button shall appear
- When Back clicked, NavigationState.GoBack shall be called
- Previous file shall be displayed

### Annotations Section
- The view shall display annotations for this file
- Annotations sorted by severity (errors first)
- Each annotation shows: severity icon, rule ID, message, line
- Severity icons: ✕ error (red), ⚠ warning (yellow), ℹ info (blue)

### Error States
- When file not indexed: "File not in index. Is it in .gitignore?"
- When file indexed but no structure: "No structure extracted (binary or unsupported)"
- When connection lost: Error displayed, retry available

### Loading State
- While queries running, show skeleton UI
- Load sections as they complete (don't wait for all)

## Constraints

- **Parallel queries** — Design specifies parallel for performance
- **Edge limit** — Show max 50 edges per direction; "show more" for rest
- **No inline code view** — Show structure, not full source code

## References

- [Web UI Design](../designs/web-ui.md) — Inspect View section, IInspectService contract
- [File Inspection Flow](../flows/ui/file-inspection.md) — Queries, display structure
- [Edge Traversal Flow](../flows/ui/edge-traversal.md) — Navigation behavior

## Error Policy

Query errors:
1. Show error message in relevant section
2. Other sections still load if their queries succeed
3. "Retry" button for failed sections

Edge traversal errors:
1. If target not in index: Show message "Target not indexed" with URI
2. If target external: Show "External reference" with original URI
3. Do not navigate away from current file on error

## Verification

| Scenario | How to verify |
|----------|---------------|
| Load file | Navigate to Inspect with C# file URI, verify metadata appears |
| Nodes | Verify classes and methods appear grouped |
| Outgoing edges | Verify IMPORTS edges show for `using` statements |
| Incoming edges | Open heavily-used file, verify incoming CALLS edges |
| Edge click | Click an edge, verify target file loads |
| Back | Click edge, then Back, verify original file returns |
| Annotations | Open file with warnings, verify they appear |
| Not indexed | Enter path to .gitignored file, verify error message |
| Manual URI | Type URI in input, press Enter, verify file loads |
