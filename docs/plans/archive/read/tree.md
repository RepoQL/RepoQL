# Plan: tree Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — TreeHandler

## Scope

**Covers:**
- `TreeHandler` implementing `IModifierHandler`
- Wire existing `=> tree` syntax to new modifier system
- Progressive verbosity (headlines → names → folders+counts)

**Does not cover:**
- Tree generation logic (existing in `IReadContentProvider.FormatAsTreeAsync`)
- Pattern resolution (handled by dispatcher)

## Enables

- Unified syntax: `=> tree` works like other modifiers
- Existing tree functionality preserved

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `IReadContentProvider.FormatAsTreeAsync`
- Existing `ExecuteTreeAsync` in `ReadOrchestrator`

## North Star

Directory tree with verbosity that adapts to budget. Richest representation that fits.

## Done Criteria

### Handler Registration
- The TreeHandler shall register with modifier name `tree`
- The TreeHandler shall handle `CanHandle("tree")` returning true

### Execution
- The handler shall delegate to existing `FormatAsTreeAsync`
- The handler shall try full tree (with headlines) first
- When full tree exceeds budget, try names-only tree
- When names-only exceeds budget, try folders-with-counts

### Verbosity Levels
- **Headlines**: Folder tree with headline per file (~10 tokens/file)
- **Names**: Folder tree with filenames only (~2 tokens/file)
- **Folders**: Folder structure with file type counts (~3 tokens/folder)

### Budget Handling
- The handler shall select richest verbosity that fits budget
- When even folders exceed budget, set `ExceedsBudget = true`

### Output Format (headlines)
```
src/
  Auth/
    TokenService.cs | TokenService : ITokenService | 280 ln
    AuthMiddleware.cs | AuthMiddleware | 95 ln
```

## Constraints

- **Reuse existing**: Leverage `FormatAsTreeAsync`, don't reimplement
- **Existing syntax migration**: `=> tree` in old regex should route here

## References

- [Flow: tree.md](../../flows/future/read/tree.md)
- [ReadOrchestrator.cs](../../../src/RepoQL.Explore/ReadOrchestrator.cs) — `ExecuteTreeAsync`

## Error Policy

- Empty match: Return "No files matched: {pattern}"
- Tree generation fails: Return error with message
