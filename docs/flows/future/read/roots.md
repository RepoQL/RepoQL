# Read Roots Flow

Walk up the call/use graph to find entry points that lead to matched content.

## Why This Matters

Roots answers "what ultimately uses this?"—finding entry points, detecting dead code, and understanding how code is reached from external triggers.

| Without | With |
|---------|------|
| Manually trace callers of callers | Automatic traversal to entry points |
| Miss indirect paths to code | Complete picture of all paths |
| Guess if code is dead | Confirm: only test roots = likely dead |

## Trigger

`read("<pattern> => roots", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to starting nodes
**Output**: Set of node URIs to find roots for
**Failure**: Invalid pattern returns error with suggestion

### 2. Upward Traversal
**Actor**: Read tool
**Action**: Walk up the usage graph until reaching nodes with no incoming edges
**Output**: Root nodes with paths from starting nodes

Traversal follows USES_SYMBOL and IMPORTS edges in reverse direction. Stops at:
- Nodes with no incoming usage edges (true entry points)
- Maximum depth (to prevent infinite traversal in cycles)

### 3. Root Classification
**Actor**: Read tool
**Action**: Classify each root by type
**Output**: Roots tagged with their nature

Root types:
- **Entry point**: API endpoint, event handler, main function
- **Test**: Test method or test fixture
- **Sample**: Example or demo code
- **Orphan**: No callers and not an entry point (potentially dead)

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format roots with path summary and classification
**Output**: Classified roots with indication of path depth

Result elements:
- Root node URI with headline
- Classification tag
- Depth from starting node
- Optionally: intermediate path (if budget allows)

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many roots as fit within token budget
**Output**: Roots that fit, with count of omitted in footer

## Termination

Flow completes when:
- Roots rendered with classifications
- Footer reports total roots found, by classification, and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#symbol=ValidateToken => roots

Entry points:
  file:///src/Api/AuthController.cs#symbol=Login
    AuthController.Login [depth: 2] via AuthMiddleware.Invoke

Tests:
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_Valid_ReturnsTrue
    [Test] ValidateToken_Valid_ReturnsTrue [depth: 1]
  file:///src/Tests/AuthMiddlewareTests.cs#symbol=Invoke_InvalidToken_Returns401
    [Test] Invoke_InvalidToken_Returns401 [depth: 2]

[1 entry point, 2 tests, 0 orphans]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot traverse without sources |
| Cycle detected | Mark cycle, continue to other paths |
| Max depth reached | Note truncation, show deepest reached |
| Starting node is already a root | Return self as root with classification |

## Verification

| Environment | How |
|-------------|-----|
| Local | Find roots for internal method; verify expected entry points and tests |
| Automated tests | Assert: code only reached by tests has only test roots |
| Production | Track orphan detection rate; surface potentially dead code |

## Related

- `leaves.md` — walk down graph to terminals
- `edges.md` — single-hop relationship traversal
- `tests.md` — specialized traversal to tests
