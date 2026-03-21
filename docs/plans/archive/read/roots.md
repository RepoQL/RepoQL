# Plan: roots Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — RootsHandler

## Scope

**Covers:**
- `RootsHandler` implementing `IModifierHandler`
- Walk up call/use graph to find entry points
- Classify roots (entry point, test, sample, orphan)
- Handle cycles in graph

**Does not cover:**
- Edge indexing (existing infrastructure)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can find what ultimately uses code
- Detect dead code (only test roots = potentially dead)
- Understand entry points

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `edge` table
- Node classification (tests, entry points)

## North Star

Walk up to where the buck stops. Know if code is reachable from production entry points or just tests.

## Done Criteria

### Handler Registration
- The RootsHandler shall register with modifier name `roots`
- The RootsHandler shall handle `CanHandle("roots")` returning true

### Execution
- The handler shall traverse USES_SYMBOL and IMPORTS edges in reverse
- The handler shall continue until nodes have no incoming edges
- The handler shall detect and handle cycles (mark, don't infinite loop)
- The handler shall classify each root by type

### Root Classification
- **Entry point**: API endpoint, event handler, main function
- **Test**: Test method or test fixture (detect by path, attributes)
- **Sample**: Example or demo code (detect by path)
- **Orphan**: No callers and not classified as entry point

### Output Format
```
file:///src/Auth/TokenService.cs#symbol=ValidateToken => roots

Entry points:
  file:///src/Api/AuthController.cs#symbol=Login
    AuthController.Login [depth: 2] via AuthMiddleware.Invoke

Tests:
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_Valid
    [Test] ValidateToken_Valid [depth: 1]

[1 entry point, 1 test, 0 orphans]
```

### Budget Handling
- Show roots that fit within budget
- Footer shows: `[N entry points, M tests, K orphans]`

### Traversal Limits
- Max depth: 20 hops (configurable)
- Cycle detection: Mark visited nodes, skip if seen

## Constraints

- **Use existing edges**: USES_SYMBOL, IMPORTS for traversal
- **Classification heuristics**: Path-based detection for tests/samples

## References

- [Flow: roots.md](../../flows/future/read/roots.md)
- `edge` table schema

## Error Policy

- Max depth reached: Note truncation in output
- Cycle detected: Mark cycle, continue other paths
- No roots found: Return "Code appears to be entry point itself"
