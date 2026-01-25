# Read Edge Traversal Flow

Follow relationships from matched files/symbols to connected entities.

## Why This Matters

Edge traversal answers "what uses this?" and "what does this use?"—essential for understanding dependencies, planning refactors, and assessing change impact.

| Without | With |
|---------|------|
| Grep for symbol name, miss indirect usages | Follow typed edges to all connected entities |
| Manual code reading to trace dependencies | Structured traversal of relationship graph |

## Trigger

`read("<pattern> => <edge_type>", tokenBudget)`

Where `<edge_type>` is one of the relationship types in the schema.

## Edge Types

| Type | Meaning | Example |
|------|---------|---------|
| `IMPORTS` | File/module imports another | `UserService.cs` imports `IUserRepository` |
| `USES_SYMBOL` | Code references a symbol | `Login()` uses `ValidateToken` |
| `HAS_PART` | Containment relationship | `UserService` has part `CreateUser` method |
| `REFERS_TO` | Documentation/comment reference | Doc mentions `AuthService` |

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to source nodes
**Output**: Set of node URIs to traverse from
**Failure**: Invalid pattern returns error with suggestion

### 2. Edge Traversal
**Actor**: Read tool
**Action**: Query edges of specified type from source nodes
**Output**: Connected nodes with relationship metadata

Traversal is one hop by default—direct relationships only.

### 3. Result Formatting
**Actor**: Read tool
**Action**: Format connected nodes with their relationship to source
**Output**: List showing source → edge → target with target summaries

Result elements:
- Source node URI
- Edge type and direction
- Target node URI with headline or structure snippet
- Grouped by source when multiple sources

### 4. Budget Fitting
**Actor**: Read tool
**Action**: Include as many relationships as fit within token budget
**Output**: Relationships that fit, with count of omitted in footer

## Termination

Flow completes when:
- Relationships rendered for sources that fit within budget
- Footer reports total relationships found and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#symbol=ValidateToken => USES_SYMBOL

Used by:
  file:///src/Auth/AuthMiddleware.cs#symbol=Invoke
    AuthMiddleware.Invoke | validates request token before proceeding
  file:///src/Api/LoginController.cs#symbol=Login
    LoginController.Login | validates refresh token during login
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_ExpiredToken_Throws
    [Test] validates expiration check

[3 usages shown, 0 omitted]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot traverse without sources |
| Unknown edge type | Return error listing valid edge types |
| No relationships found | Return "no relationships" with edge type and source count |
| Source not in graph | Return "not indexed" for that source |

## Verification

| Environment | How |
|-------------|-----|
| Local | Traverse edges from known symbol; verify expected usages appear |
| Automated tests | Assert: adding a usage creates edge; removing usage removes edge |
| Production | Track edge type distribution; monitor for missing expected edges |

## Related

- `roots.md` — walk up graph to entry points
- `leaves.md` — walk down graph to terminals
- `tests.md` — specialized traversal to test files
