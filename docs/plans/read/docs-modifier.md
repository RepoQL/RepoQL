# Plan: docs Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — DocsHandler

## Scope

**Covers:**
- `DocsHandler` implementing `IModifierHandler`
- Find documentation related to matched code
- Query REFERS_TO edges
- Find markdown files by semantic similarity

**Does not cover:**
- Edge indexing (existing infrastructure)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can connect code to documentation
- Find explanatory context for implementation

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `edge` table with REFERS_TO edges
- Existing `Files` view
- Embeddings for semantic similarity

## North Star

Connect code to docs. Find explicit references and semantic matches.

## Done Criteria

### Handler Registration
- The DocsHandler shall register with modifier name `docs`
- The DocsHandler shall handle `CanHandle("docs")` returning true

### Execution
- The handler shall query `edge` table for REFERS_TO edges to matched URIs
- The handler shall search markdown files for semantic similarity
- The handler shall check conventional locations (README near code, /docs folder)
- The handler shall rank by: explicit reference > semantic similarity > proximity

### Output Format
```
file:///src/Auth/TokenService.cs => docs

Explicit references:
  file:///docs/architecture/authentication.md#section=token-lifecycle
    "Token Lifecycle" section describes refresh flow

Semantic matches:
  file:///docs/security/session-management.md
    Discusses session vs token tradeoffs (related concepts)

Proximity:
  file:///src/Auth/README.md
    Auth module overview

[3 explicit, 1 semantic, 1 proximity]
```

### Budget Handling
- Show docs that fit within budget
- Footer shows: `[N explicit, M semantic, K proximity]`

## Constraints

- **Use existing infrastructure**: `edge` table, `Files` view, embeddings
- **Markdown detection**: Filter to `*.md` files for doc search

## References

- [Flow: docs.md](../../flows/future/read/docs.md)
- `edge` table schema

## Error Policy

- No docs found: Return "No related documentation found"
- No edges indexed: Fall back to semantic + proximity only
