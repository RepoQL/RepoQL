# Plan: regex Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — RegexHandler

## Scope

**Covers:**
- `RegexHandler` implementing `IModifierHandler`
- Regex search within matched files
- Return snippets with captures highlighted
- RE2 flavor (safe, no catastrophic backtracking)

**Does not cover:**
- Pattern resolution (handled by dispatcher)
- Literal search (use `grep` modifier)

## Enables

- Agents can find pattern variations
- Capture groups for extracting data

## Prerequisites

- Plan: ModifierDispatcher complete
- `ReadDocument.TextContent` available

## North Star

Match patterns, show captures. Safe regex that won't hang on pathological input.

## Done Criteria

### Handler Registration
- The RegexHandler shall register with modifier name `regex`
- The RegexHandler shall handle `CanHandle("regex")` returning true

### Parameter Parsing
- The parameter shall be the regex pattern
- When parameter is empty, return error requesting pattern

### Execution
- The handler shall compile regex with `RegexOptions.Compiled`
- The handler shall apply timeout to prevent pathological patterns
- The handler shall find all matches in `TextContent`
- The handler shall generate snippet for each match with context
- The handler shall show capture groups if pattern has them

### Output Format
```
file:///src/Config/Settings.cs#line=12  [pattern: TODO:\s*(.+)]

 10:     public class Settings
 11:     {
>12:         // TODO: Add validation for negative values
              └─ capture[1]: "Add validation for negative values"
 13:         public int MaxRetries { get; set; }

[4 matches, 1 omitted]
```

### Budget Handling
- Show matches that fit within budget
- Footer shows: `[N matches shown, K omitted]`

## Constraints

- **RE2 flavor**: Use .NET Regex with timeout, no lookbehind
- **Timeout**: 5 second max per file to prevent hangs

## References

- [Flow: regex.md](../../flows/future/read/regex.md)

## Error Policy

- Invalid regex: Return error with syntax issue location
- Regex timeout: Return "Pattern too complex, timed out on {file}"
- No matches: Return "No matches for pattern in N files"
