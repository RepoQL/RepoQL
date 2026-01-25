# Plan: lint Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — LintHandler

## Scope

**Covers:**
- `LintHandler` implementing `IModifierHandler`
- Query annotations for matched file URIs
- Severity filtering (`lint`, `lint: errors`, `lint: warnings`)
- Format diagnostics with locations

**Does not cover:**
- Annotation generation (existing in indexing pipeline)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can see diagnostics scoped to specific files
- Prioritize fixes by viewing errors-only

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `Annotations` view in DuckDB
- Annotations populated by indexing

## North Star

See all problems in scope with precise locations. Filter by severity when needed.

## Done Criteria

### Handler Registration
- The LintHandler shall register with modifier name `lint`
- The LintHandler shall handle `CanHandle("lint")` returning true
- The LintHandler shall handle `CanHandle("lint: errors")` returning true
- The LintHandler shall handle `CanHandle("lint: warnings")` returning true

### Parameter Parsing
- When parameter is empty or null, show all severities
- When parameter is `errors`, filter to severity = 'error'
- When parameter is `warnings`, filter to severity = 'warning'

### Execution
- The handler shall query `Annotations` view for matched file URIs
- The handler shall filter by `kind = 'lint'`
- The handler shall order by file, then line number
- The handler shall format each diagnostic with location and message

### Output Format
```
file:///src/Auth/TokenService.cs#line=42 [error] CS0103
  The name 'tokne' does not exist in the current context

file:///src/Auth/TokenService.cs#line=58 [warning] CS0168
  The variable 'ex' is declared but never used
```

### Budget Handling
- When output exceeds budget, show what fits with count of omitted
- Footer shows: `[N errors, M warnings shown, K omitted]`

## Constraints

- **Use existing view**: Query `Annotations`, don't scan files directly
- **Consistent format**: Match existing diagnostic output patterns

## References

- [Flow: lint.md](../../flows/future/read/lint.md)
- `Annotations` view in DuckDB schema

## Error Policy

- No annotations found: Return "No diagnostics in scope"
- Query fails: Return error with message
