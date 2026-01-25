# Read Changes Flow

Show working copy changes for matched files, grouped by changelist.

## Why This Matters

Changes answers "what's modified and pending?"—reviewing work before commit, understanding current state, and seeing what an agent has changed.

| Without | With |
|---------|------|
| Run git diff, parse output | Structured diffs with context |
| Staged and unstaged mixed | Grouped by changelist |
| Full diff output | Scoped to matched files |

## Trigger

`read("<pattern> => changes", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file paths to check for changes
**Failure**: Invalid pattern returns error with suggestion

### 2. Change Detection
**Actor**: Read tool
**Action**: Detect working copy changes for matched files
**Output**: Changed files with status and diffs

Change types:
- Modified (content changed)
- Added (new file)
- Deleted (file removed)
- Renamed (file moved/renamed)

### 3. Changelist Grouping
**Actor**: Read tool
**Action**: Group changes by changelist status
**Output**: Changes organized by staging state

Changelists:
- **Staged**: Changes added to index (will be in next commit)
- **Unstaged**: Working copy changes not yet staged
- **Untracked**: New files not yet added to git

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format changes with diffs
**Output**: Grouped changes with diff content

Result elements:
- Changelist header
- File path with change type
- Diff showing additions/deletions
- Line counts (+N -M)

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many changes as fit within token budget
**Output**: Changes that fit, with count of omitted in footer

Priority: staged before unstaged, then by file path.

## Termination

Flow completes when:
- Changes rendered grouped by changelist
- Footer reports total files changed, by changelist, and tokens used

## Example Output

```
file:///src/Auth/**/*.cs => changes

Staged (ready to commit):
  file:///src/Auth/TokenService.cs [modified +5 -2]

  @@ -42,2 +42,5 @@
  -        if (token.ExpiresAt >= DateTime.UtcNow)
  -            return true;
  +        if (token.ExpiresAt > DateTime.UtcNow)
  +        {
  +            _logger.LogDebug("Token valid until {Expiry}", token.ExpiresAt);
  +            return true;
  +        }

Unstaged (working copy):
  file:///src/Auth/AuthMiddleware.cs [modified +1 -0]

  @@ -15,0 +16,1 @@
  +        // TODO: Add rate limiting

Untracked:
  file:///src/Auth/TokenCache.cs [new file]
    (content not shown for untracked files)

[2 staged, 1 unstaged, 1 untracked]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot show changes without files |
| Files not in git repo | Return "not in a git repository" |
| No changes | Return "no changes" (working copy clean) |
| Binary file changed | Show file changed indicator without diff |

## Verification

| Environment | How |
|-------------|-----|
| Local | Modify known file; verify changes appear with correct diff |
| Automated tests | Assert: staged vs unstaged correctly distinguished |
| Production | Track changes retrieval; no special monitoring needed (git native) |

## Related

- `history.md` — committed changes over time
- `blame.md` — per-line attribution
