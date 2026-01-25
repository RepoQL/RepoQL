# Read Leaves Flow

Walk down the call/use graph to find terminal nodes that matched content depends on.

## Why This Matters

Leaves answers "what does this ultimately depend on?"—finding external dependencies, database calls, API clients, and the boundaries of what code touches.

| Without | With |
|---------|------|
| Manually trace calls of calls | Automatic traversal to terminals |
| Miss transitive dependencies | Complete picture of all dependencies |
| Unclear what external systems are touched | Visible dependency boundaries |

## Trigger

`read("<pattern> => leaves", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to starting nodes
**Output**: Set of node URIs to find leaves for
**Failure**: Invalid pattern returns error with suggestion

### 2. Downward Traversal
**Actor**: Read tool
**Action**: Walk down the usage graph until reaching nodes with no outgoing edges
**Output**: Leaf nodes with paths from starting nodes

Traversal follows USES_SYMBOL and IMPORTS edges in forward direction. Stops at:
- Nodes with no outgoing usage edges (terminals)
- External references (framework, library calls)
- Maximum depth (to prevent infinite traversal in cycles)

### 3. Leaf Classification
**Actor**: Read tool
**Action**: Classify each leaf by type
**Output**: Leaves tagged with their nature

Leaf types:
- **External**: Framework or library call (e.g., `HttpClient.SendAsync`)
- **Database**: Data access operation
- **Primitive**: Language primitive or built-in
- **Internal terminal**: Internal code with no further calls

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format leaves with path summary and classification
**Output**: Classified leaves with indication of path depth

Result elements:
- Leaf node URI or external reference
- Classification tag
- Depth from starting node
- Optionally: intermediate path (if budget allows)

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many leaves as fit within token budget
**Output**: Leaves that fit, with count of omitted in footer

## Termination

Flow completes when:
- Leaves rendered with classifications
- Footer reports total leaves found, by classification, and tokens used

## Example Output

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
  file:///src/Data/TokenStore.cs#symbol=SaveAsync [depth: 1]
    Token persistence

[2 external, 2 database, 0 internal terminals]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot traverse without sources |
| Cycle detected | Mark cycle, continue to other paths |
| Max depth reached | Note truncation, show deepest reached |
| Starting node is already a leaf | Return self as leaf with classification |

## Verification

| Environment | How |
|-------------|-----|
| Local | Find leaves for service method; verify external dependencies visible |
| Automated tests | Assert: method calling HttpClient has external leaf for HttpClient |
| Production | Track leaf classification distribution; surface unexpected external dependencies |

## Related

- `roots.md` — walk up graph to entry points
- `edges.md` — single-hop relationship traversal
