# Read Blame Flow

Show per-line attribution for matched files—who wrote each line and when.

## Why This Matters

Blame answers "who wrote this and why?"—tracing lines to their origin, finding the right person to ask, and understanding the reasoning behind code through commit messages.

| Without | With |
|---------|------|
| Run git blame, parse output | Structured attribution with context |
| See commit hashes, look up separately | Inline commit messages |
| Full file blame | Scoped to matched files or line ranges |

## Trigger

```
read("<pattern> => blame", tokenBudget)                        # Full file blame
read("<pattern>#line=N,M => blame", tokenBudget)              # Specific line range
```

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file paths with optional line ranges
**Failure**: Invalid pattern returns error with suggestion

### 2. Blame Retrieval
**Actor**: Read tool
**Action**: Retrieve git blame information for matched content
**Output**: Per-line attribution with commit metadata

Attribution includes:
- Commit hash
- Author
- Date
- Commit message (first line)

### 3. Attribution Grouping
**Actor**: Read tool
**Action**: Group consecutive lines by commit for readability
**Output**: Grouped blame with commit context

Grouping collapses consecutive lines from the same commit into ranges, reducing repetition while preserving attribution.

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format blame with code and attribution
**Output**: Code lines with commit information

Result elements:
- Line numbers
- Code content
- Commit info (hash, author, date, message summary)
- Grouped by commit for consecutive lines

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as much blame as fits within token budget
**Output**: Blame that fits, with indication of lines omitted in footer

## Termination

Flow completes when:
- Blame rendered with attribution
- Footer reports lines shown, commits referenced, and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#line=40,50 => blame

abc123f Alice Developer (2024-01-15) "Fix token expiration edge case"
 40:     public bool IsValid(Token token)
 41:     {
 42:         if (token.ExpiresAt > DateTime.UtcNow)

def456a Bob Engineer (2024-01-10) "Add configurable token expiration"
 43:         {
 44:             var expiryMinutes = _config.ExpirationMinutes;
 45:             _logger.LogDebug("Token expires in {Minutes}m", expiryMinutes);

789xyz0 Carol Tech (2023-12-01) "Initial token service implementation"
 46:             return true;
 47:         }
 48:         return false;
 49:     }
 50: }

[11 lines, 3 commits]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot blame without files |
| File not in git | Return "not tracked by git" |
| File has no history | Return "no blame available" (uncommitted new file) |
| Line range out of bounds | Return available lines with note |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request blame for known file; verify authors and dates correct |
| Automated tests | Assert: blame attribution matches git blame output |
| Production | Track blame usage; no special monitoring needed (git native) |

## Related

- `history.md` — commit history for files
- `changes.md` — working copy changes
