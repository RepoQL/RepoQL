# Read History Flow

Show git history for matched files, optionally filtered by relevance to keywords.

## Why This Matters

History answers "what changed here and why?"—understanding evolution, finding when behavior was introduced, and identifying who to ask about code.

| Without | With |
|---------|------|
| Run git log, parse output | Structured history with context |
| See all commits, wade through noise | Filter by keywords to find relevant changes |
| Commits without diff context | Commits with change summaries |

## Trigger

```
read("<pattern> => history", tokenBudget)                    # Recent history
read("<pattern> => history: <keywords>", tokenBudget)       # Filtered by relevance
```

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file paths for git history
**Failure**: Invalid pattern returns error with suggestion

### 2. History Retrieval
**Actor**: Read tool
**Action**: Retrieve git commits that touched matched files
**Output**: Commits with metadata and diffs

History includes:
- Commit hash (short)
- Author and date
- Commit message
- Diff for matched files

### 3. Relevance Ranking (if keywords provided)
**Actor**: Read tool
**Action**: Rank commits by relevance to keywords
**Output**: Reordered commits with most relevant first

Ranking considers:
- Commit message content
- Author name/email
- File names touched in the commit

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format commits with relevant context
**Output**: Commit list with messages and diff excerpts

Result elements:
- Commit hash, author, date
- Full commit message
- Diff summary for matched files (insertions/deletions)
- Files changed count

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many commits as fit within token budget
**Output**: Commits that fit, with count of omitted in footer

Without keywords: most recent first.
With keywords: most relevant first.

## Termination

Flow completes when:
- Commits rendered with context
- Footer reports total commits found, shown, and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs => history: expiration

abc123f (2024-01-15) Alice Developer
  Fix token expiration check for edge case

  Tokens with exactly 0 seconds remaining were incorrectly
  considered valid. Changed >= to >.

  @@ -42,1 +42,1 @@
  -        if (token.ExpiresAt >= DateTime.UtcNow)
  +        if (token.ExpiresAt > DateTime.UtcNow)

def456a (2024-01-10) Bob Engineer
  Add configurable token expiration

  Tokens now use ExpirationMinutes from config instead of
  hardcoded 60 minutes.

  [diff truncated, +15 -3 lines]

[2 commits shown (by relevance), 12 more in history]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot show history without files |
| Files not in git | Return "not tracked by git" |
| No commits for files | Return "no history" (newly added, never committed) |
| Keywords match nothing | Return recent history with note "no matches for keywords" |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request history for known file; verify commits appear with correct messages |
| Automated tests | Assert: keyword filtering surfaces relevant commits over chronological order |
| Production | Track keyword usage; monitor relevance ranking effectiveness |

## Related

- `changes.md` — working copy changes (uncommitted)
- `blame.md` — per-line attribution
- Git functions in query — programmatic git access
