# Read Tree Flow

Display matched files as a directory tree with progressive verbosity.

## Why This Matters

Tree view enables rapid orientation—understanding module organization at a glance. Verbosity adapts to budget, giving maximum insight into structure without exceeding token limits.

| Without | With |
|---------|------|
| List files flat, lose hierarchy context | See folder structure, understand organization |
| Fixed verbosity wastes or starves budget | Verbosity scales to available tokens |

## Trigger

`read("<pattern> => tree", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs with their directory structure
**Failure**: Invalid pattern returns error with suggestion

### 2. Verbosity Selection
**Actor**: Read tool
**Action**: Determine richest verbosity level that fits within budget
**Output**: Selected verbosity level

Verbosity levels (richest to most compact):

| Level | Content | Token cost |
|-------|---------|------------|
| **Headlines** | Folder tree with headline per file | ~10 tokens/file |
| **Names** | Folder tree with filenames only | ~2 tokens/file |
| **Folders** | Folder structure with file type counts per folder | ~3 tokens/folder |

Selection: try Headlines first; if exceeds budget, try Names; if exceeds, use Folders.

### 3. Tree Generation
**Actor**: Read tool
**Action**: Generate tree at selected verbosity level
**Output**: ASCII tree structure

Tree elements:
- Folder hierarchy with indentation
- At Headlines level: file headline indented under folder
- At Names level: filename indented under folder
- At Folders level: folder name with count summary (e.g., `src/ [12 .cs, 3 .json]`)

### 4. Output Assembly
**Actor**: Read tool
**Action**: Assemble tree with verbosity indicator
**Output**: Tree structure with footer indicating level used

## Termination

Flow completes when:
- Tree rendered at selected verbosity level
- Footer reports file/folder count, verbosity level, tokens used

## Example Output

**Headlines level:**
```
src/
  Auth/
    TokenService.cs | TokenService : ITokenService | Validate, Refresh, Revoke | 280 ln
    AuthMiddleware.cs | AuthMiddleware | Invoke, ValidateHeader | 95 ln
  Data/
    UserRepository.cs | UserRepository : IUserRepository | Get, Create, Update | 150 ln
```

**Names level:**
```
src/
  Auth/
    TokenService.cs
    AuthMiddleware.cs
  Data/
    UserRepository.cs
```

**Folders level:**
```
src/
  Auth/ [2 .cs]
  Data/ [1 .cs]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return empty result with message |
| Even folders level exceeds budget | Show root folders only with total counts |
| Pattern matches single file | Show file headline (tree not meaningful) |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request tree with varying budgets; verify verbosity scales down appropriately |
| Automated tests | Assert: small budget = folders level; large budget = headlines level |
| Production | Track verbosity level distribution; monitor for patterns that always hit compact level |

## Related

- `default.md` — automatic representation selection
- `headline.md` — single-line summary (used in richest tree level)
