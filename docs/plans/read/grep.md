# Plan: grep Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — GrepHandler

## Scope

**Covers:**
- `GrepHandler` implementing `IModifierHandler`
- Literal string search within matched files
- Return snippets with line numbers and context
- Case-sensitive by default

**Does not cover:**
- Pattern resolution (handled by dispatcher)
- Regex patterns (separate `regex` modifier)

## Enables

- Agents can find exact string occurrences
- Direct to every match with surrounding context

## Prerequisites

- Plan: ModifierDispatcher complete
- `ReadDocument.TextContent` available

## North Star

Find exact text matches. Return snippets with context so agent understands each match.

## Done Criteria

### Handler Registration
- The GrepHandler shall register with modifier name `grep`
- The GrepHandler shall handle `CanHandle("grep")` returning true

### Parameter Parsing
- The parameter shall be the search string
- When parameter is empty, return error requesting search string

### Execution
- The handler shall search `TextContent` of each matched document
- The handler shall find all occurrences of the literal string
- The handler shall generate snippet for each match with context lines
- The handler shall include line numbers matching actual file

### Output Format
```
file:///src/Auth/TokenService.cs#line=23  [1 of 4 in file]

 21:     private readonly ILogger _logger;
 22:
>23:     public TokenService(ITokenStore store, ILogger<TokenService> logger)
 24:     {
 25:         _store = store;

[6 matches in 2 files]
```

### Budget Handling
- Show matches that fit within budget
- Footer shows: `[N matches shown in M files, K omitted]`

## Constraints

- **Case-sensitive**: Default behavior matches exact case
- **No regex**: Literal string only, use `regex` modifier for patterns

## References

- [Flow: grep.md](../../flows/future/read/grep.md)

## Error Policy

- Empty search string: Return "Please provide search string after 'grep:'"
- No matches: Return "No matches for '{string}' in N files"
