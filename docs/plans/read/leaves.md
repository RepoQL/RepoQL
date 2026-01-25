# Plan: leaves Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — LeavesHandler

## Scope

**Covers:**
- `LeavesHandler` implementing `IModifierHandler`
- Walk down call/use graph to find terminals
- Classify leaves (external, database, primitive, internal)
- Handle cycles in graph

**Does not cover:**
- Edge indexing (existing infrastructure)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can see what code ultimately depends on
- Understand external dependencies
- Find database access points

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `edge` table
- External reference detection

## North Star

Walk down to the end of the line. Know what external systems this code touches.

## Done Criteria

### Handler Registration
- The LeavesHandler shall register with modifier name `leaves`
- The LeavesHandler shall handle `CanHandle("leaves")` returning true

### Execution
- The handler shall traverse USES_SYMBOL and IMPORTS edges forward
- The handler shall continue until nodes have no outgoing edges
- The handler shall detect external references (framework, library)
- The handler shall detect and handle cycles

### Leaf Classification
- **External**: Framework or library call (e.g., `HttpClient.SendAsync`)
- **Database**: Data access operation (detect by type/pattern)
- **Primitive**: Language primitive or built-in
- **Internal terminal**: Internal code with no further calls

### Output Format
```
file:///src/Auth/TokenService.cs#symbol=RefreshAsync => leaves

External:
  System.DateTime.UtcNow [depth: 1]
    Time provider for expiration check
  Microsoft.Extensions.Logging.ILogger.LogInformation [depth: 2]
    Logging via AuthMiddleware

Database:
  file:///src/Data/TokenStore.cs#symbol=GetAsync [depth: 1]
    Token retrieval from store

[2 external, 1 database, 0 internal terminals]
```

### Budget Handling
- Show leaves that fit within budget
- Footer shows: `[N external, M database, K internal]`

### Traversal Limits
- Max depth: 20 hops (configurable)
- Cycle detection: Mark visited nodes, skip if seen

## Constraints

- **Use existing edges**: USES_SYMBOL, IMPORTS for traversal
- **External detection**: Namespace-based heuristics (System.*, Microsoft.*, etc.)

## References

- [Flow: leaves.md](../../flows/future/read/leaves.md)
- `edge` table schema

## Error Policy

- Max depth reached: Note truncation in output
- Cycle detected: Mark cycle, continue other paths
- No leaves found: Return "Code appears to be terminal itself"
