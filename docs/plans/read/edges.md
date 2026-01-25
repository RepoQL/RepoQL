# Plan: edges Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — EdgeHandler

## Scope

**Covers:**
- `EdgeHandler` implementing `IModifierHandler`
- Traverse edges by type from matched files/symbols
- Single-hop traversal (direct relationships)
- Format connected nodes with summaries

**Does not cover:**
- Edge indexing (existing infrastructure)
- Multi-hop traversal (use `roots` or `leaves`)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can see "what uses this" and "what does this use"
- Understand direct dependencies

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `edge` table
- Edge types: IMPORTS, USES_SYMBOL, HAS_PART, REFERS_TO

## North Star

Follow typed edges one hop. See what's directly connected.

## Done Criteria

### Handler Registration
- The EdgeHandler shall register with modifier names matching edge types
- The EdgeHandler shall handle `CanHandle("IMPORTS")`, `CanHandle("USES_SYMBOL")`, etc.
- The EdgeHandler shall be case-insensitive for edge type matching

### Execution
- The handler shall query `edge` table for edges of specified type
- The handler shall find edges where source OR target matches input URIs
- The handler shall retrieve node information for connected entities
- The handler shall format with headlines or structure snippets

### Output Format
```
file:///src/Auth/TokenService.cs#symbol=ValidateToken => USES_SYMBOL

Used by:
  file:///src/Auth/AuthMiddleware.cs#symbol=Invoke
    AuthMiddleware.Invoke | validates request token
  file:///src/Api/LoginController.cs#symbol=Login
    LoginController.Login | validates refresh token

Uses:
  file:///src/Data/TokenStore.cs#symbol=GetAsync
    TokenStore.GetAsync | retrieves token from store

[3 relationships shown]
```

### Budget Handling
- Show relationships that fit within budget
- Footer shows: `[N relationships shown, M omitted]`

## Constraints

- **Single hop**: Only direct relationships, not transitive
- **Use existing table**: Query `edge` table directly

## References

- [Flow: edges.md](../../flows/future/read/edges.md)
- `edge` table schema
- Edge types in schema documentation

## Error Policy

- Unknown edge type: Return error listing valid edge types
- No relationships: Return "No {type} relationships found"
- Node not in graph: Return "Not indexed" for that source
