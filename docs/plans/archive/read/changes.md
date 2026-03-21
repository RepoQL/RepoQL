# Plan: changes Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — ChangesHandler

## Scope

**Covers:**
- `ChangesHandler` implementing `IModifierHandler`
- Query working copy changes for matched files
- Group by changelist (staged, unstaged, untracked)
- Show diffs for modified files

**Does not cover:**
- Git integration (existing `git_status()`, `git_diff()` UDFs)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can review pending work before commit
- See what's staged vs unstaged

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `git_status()` UDF
- Existing `git_diff()` UDF

## North Star

See pending work organized by changelist. Understand what's about to be committed.

## Done Criteria

### Handler Registration
- The ChangesHandler shall register with modifier name `changes`
- The ChangesHandler shall handle `CanHandle("changes")` returning true

### Execution
- The handler shall call `git_status()` for matched file paths
- The handler shall classify files as staged, unstaged, or untracked
- The handler shall call `git_diff()` for modified files
- The handler shall group output by changelist

### Changelist Groups
- **Staged**: Changes in index (will be in next commit)
- **Unstaged**: Working copy changes not staged
- **Untracked**: New files not added to git

### Output Format
```
Staged (ready to commit):
  file:///src/Auth/TokenService.cs [modified +5 -2]

  @@ -42,2 +42,5 @@
  -        if (token.ExpiresAt >= DateTime.UtcNow)
  +        if (token.ExpiresAt > DateTime.UtcNow)

Unstaged (working copy):
  file:///src/Auth/AuthMiddleware.cs [modified +1 -0]

Untracked:
  file:///src/Auth/TokenCache.cs [new file]
```

### Budget Handling
- Show changes that fit within budget
- Truncate large diffs with `[diff truncated, +N -M lines]`

## Constraints

- **Use existing UDFs**: `git_status()`, `git_diff()`
- **Scope to matched files**: Only changes for files matching pattern

## References

- [Flow: changes.md](../../flows/future/read/changes.md)

## Error Policy

- Not a git repo: Return "Not in a git repository"
- No changes: Return "No changes in matched files (working copy clean)"
