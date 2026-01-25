# Plan: blame Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — BlameHandler

## Scope

**Covers:**
- `BlameHandler` implementing `IModifierHandler`
- Query git blame for matched files/lines
- Format per-line attribution
- Group consecutive lines by commit

**Does not cover:**
- Git integration (existing `git_blame()` UDF)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can trace lines to their origin
- Understand who wrote code and why

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `git_blame()` UDF

## North Star

Trace any line to its origin. Understand the reasoning behind current code.

## Done Criteria

### Handler Registration
- The BlameHandler shall register with modifier name `blame`
- The BlameHandler shall handle `CanHandle("blame")` returning true

### Execution
- The handler shall call `git_blame()` for matched files
- When pattern includes `#line=N,M`, blame only those lines
- The handler shall group consecutive lines from same commit
- The handler shall include commit hash, author, date, message summary

### Output Format
```
file:///src/Auth/TokenService.cs#line=40,50 => blame

abc123f Alice Developer (2024-01-15) "Fix token expiration edge case"
 40:     public bool IsValid(Token token)
 41:     {
 42:         if (token.ExpiresAt > DateTime.UtcNow)

def456a Bob Engineer (2024-01-10) "Add configurable token expiration"
 43:         {
 44:             var expiryMinutes = _config.ExpirationMinutes;
```

### Budget Handling
- Show blame that fits within budget
- Footer shows: `[N lines, M commits]`

## Constraints

- **Use existing UDF**: `git_blame()`
- **Respect line fragments**: `#line=N,M` limits blame scope

## References

- [Flow: blame.md](../../flows/future/read/blame.md)

## Error Policy

- Not a git repo: Return "Not in a git repository"
- File not tracked: Return "File not tracked by git"
- No history: Return "No blame available (uncommitted file)"
