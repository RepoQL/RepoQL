# Plan: tests Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — TestsHandler

## Scope

**Covers:**
- `TestsHandler` implementing `IModifierHandler`
- Find tests that cover matched code
- Direct and indirect coverage via call graph
- Format test information with coverage relationship

**Does not cover:**
- Edge indexing (existing infrastructure)
- Test execution
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can find what tests exercise code
- Know what to run after changes
- Understand test coverage

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `edge` table
- Test file/method detection (path, attributes)

## North Star

Find tests that cover this code. Know direct vs indirect coverage.

## Done Criteria

### Handler Registration
- The TestsHandler shall register with modifier name `tests`
- The TestsHandler shall handle `CanHandle("tests")` returning true

### Execution
- The handler shall traverse USES_SYMBOL edges in reverse to find callers
- The handler shall filter to nodes classified as tests
- The handler shall distinguish direct coverage (test calls target) from indirect
- The handler shall extract test name and description

### Test Detection
- Path patterns: `*/Tests/*`, `*/Test/*`, `*.Tests.*`, `*.Test.*`
- Attributes: `[Test]`, `[Fact]`, `[Theory]`, `[TestMethod]`
- Base classes: Test fixtures

### Output Format
```
file:///src/Auth/TokenService.cs#symbol=ValidateToken => tests

Direct coverage:
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_ValidToken
    [Test] "Returns true for valid non-expired token"
  file:///src/Tests/TokenServiceTests.cs#symbol=ValidateToken_Expired
    [Test] "Returns false for expired token"

Indirect coverage (via AuthMiddleware.Invoke):
  file:///src/Tests/AuthMiddlewareTests.cs#symbol=Invoke_ValidRequest
    [Test] "Valid token proceeds to next middleware"

[2 direct, 1 indirect]
```

### Budget Handling
- Show tests that fit within budget
- Footer shows: `[N direct, M indirect, K omitted]`

## Constraints

- **Use existing edges**: Traverse call graph to find test callers
- **Heuristic detection**: Path and attribute-based test identification

## References

- [Flow: tests.md](../../flows/future/read/tests.md)
- Test detection patterns in indexing

## Error Policy

- No tests found: Return "No test coverage found for this code"
- Target is test: Return "Target is itself a test" with what it tests
