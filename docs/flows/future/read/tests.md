# Read Tests Flow

Find tests that cover matched code.

## Why This Matters

Tests answers "what tests exercise this code?"—essential before making changes, for understanding expected behavior, and for knowing what to run after modifications.

| Without | With |
|---------|------|
| Grep for class name in test folders | Direct traversal to covering tests |
| Miss tests that exercise code indirectly | Find tests through call graph |
| Uncertain test coverage | Clear picture of what's tested |

## Trigger

`read("<pattern> => tests", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to code nodes
**Output**: Set of node URIs to find tests for
**Failure**: Invalid pattern returns error with suggestion

### 2. Test Discovery
**Actor**: Read tool
**Action**: Find test nodes that reach the target code through the call graph
**Output**: Test methods/functions with paths to target

Discovery approaches:
- Direct: tests that call target directly
- Indirect: tests that call code which eventually calls target
- Attribute-based: tests linked via coverage attributes if present

### 3. Test Formatting
**Actor**: Read tool
**Action**: Format test information with coverage relationship
**Output**: Test list with descriptions and relationship to target

Result elements:
- Test node URI with method name
- Test description (from attributes or doc comments)
- Relationship: direct call or path depth
- Test framework indicators (xUnit, NUnit, TUnit, etc.)

### 4. Budget Fitting
**Actor**: Read tool
**Action**: Include as many tests as fit within token budget
**Output**: Tests that fit, prioritizing direct coverage, with count of omitted in footer

## Termination

Flow completes when:
- Tests rendered with coverage relationships
- Footer reports total tests found, direct vs indirect, and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#symbol=ValidateToken => tests

Direct coverage:
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_ValidToken_ReturnsTrue
    [Test] "Returns true for valid non-expired token"
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_ExpiredToken_ReturnsFalse
    [Test] "Returns false for expired token"
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_NullToken_Throws
    [Test] "Throws ArgumentNullException for null input"

Indirect coverage (via AuthMiddleware.Invoke):
  file:///src/Tests/AuthMiddlewareTests.cs#symbol=Invoke_ValidRequest_CallsNext
    [Test] "Valid token proceeds to next middleware"

[3 direct, 1 indirect, 0 omitted]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot find tests without target |
| No tests found | Return "no test coverage found" for target |
| Target is itself a test | Return "target is a test" with what it tests |

## Verification

| Environment | How |
|-------------|-----|
| Local | Find tests for known method; verify expected test methods appear |
| Automated tests | Assert: method with dedicated tests shows direct coverage |
| Production | Track coverage discovery; surface code with no test coverage |

## Related

- `roots.md` — general upward traversal (tests are a type of root)
- `edges.md` — single-hop relationship traversal
