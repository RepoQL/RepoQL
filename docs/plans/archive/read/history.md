# Plan: history Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — HistoryHandler

## Scope

**Covers:**
- `HistoryHandler` implementing `IModifierHandler`
- Query git history for matched files
- Optional keyword ranking via embeddings
- Format commits with messages and diff excerpts

**Does not cover:**
- Git integration (existing `git_log()`, `git_diff()` UDFs)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can see what changed and why
- Keyword filtering surfaces relevant commits

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `git_log()` UDF
- Existing `git_diff()` UDF
- Embeddings infrastructure (for keyword ranking)

## North Star

See what changed, when, why. Keywords surface relevant commits over chronological noise.

## Done Criteria

### Handler Registration
- The HistoryHandler shall register with modifier name `history`
- The HistoryHandler shall handle `CanHandle("history")` returning true
- The HistoryHandler shall handle `CanHandle("history: <keywords>")` returning true

### Parameter Parsing
- When parameter is empty, return recent commits chronologically
- When parameter contains keywords, rank commits by relevance

### Execution
- The handler shall call `git_log()` for matched file paths
- The handler shall retrieve commit hash, author, date, message
- The handler shall retrieve diff excerpt for each commit
- When keywords provided, rank by similarity to commit message + diff

### Output Format
```
abc123f (2024-01-15) Alice Developer
  Fix token expiration check for edge case

  @@ -42,1 +42,1 @@
  -        if (token.ExpiresAt >= DateTime.UtcNow)
  +        if (token.ExpiresAt > DateTime.UtcNow)

def456a (2024-01-10) Bob Engineer
  Add configurable token expiration
  [diff truncated, +15 -3 lines]
```

### Budget Handling
- Show commits that fit within budget
- Footer shows: `[N commits shown, M more in history]`

## Constraints

- **Use existing UDFs**: `git_log()`, `git_diff()`, don't shell out
- **Scope to matched files**: Only history for files matching pattern

## References

- [Flow: history.md](../../flows/future/read/history.md)
- Git UDFs documentation

## Error Policy

- Not a git repo: Return "Not in a git repository"
- No history for files: Return "No commits found for matched files"
