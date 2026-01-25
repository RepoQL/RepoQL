# Read Regex Flow

Regular expression search within matched files, returning snippets.

## Why This Matters

Regex finds patterns—variable naming conventions, URL formats, version strings, TODO comments with specific formats. More powerful than literal grep when the target varies.

| Without | With |
|---------|------|
| Multiple greps for variations | Single pattern matches all variations |
| Miss unexpected formats | Pattern captures structural similarity |

## Trigger

`read("<pattern> => regex: <regex_pattern>", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. Regex Compilation
**Actor**: Read tool
**Action**: Compile regex pattern
**Output**: Compiled regex ready for matching
**Failure**: Invalid regex returns error with syntax indication

Regex flavor: RE2 (safe, no catastrophic backtracking).

### 3. Pattern Search
**Actor**: Read tool
**Action**: Search for regex matches in matched files
**Output**: All matches with file locations and captured groups

### 4. Snippet Generation
**Actor**: Read tool
**Action**: Generate snippets centered on each match with surrounding context
**Output**: Code snippets with line numbers, match and captures highlighted

Snippet elements:
- File URI with line fragment
- Line numbers matching actual file
- Full match highlighted
- Capture groups indicated if present

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many snippets as fit within token budget
**Output**: Snippets that fit, with count of omitted matches in footer

## Termination

Flow completes when:
- Snippets rendered for matches that fit within budget
- Footer reports total matches found, matches shown, and tokens used

## Example Output

```
file:///src/Config/Settings.cs#line=12  [pattern: TODO:\s*(.+)]

 10:     public class Settings
 11:     {
>12:         // TODO: Add validation for negative values
              └─ capture[1]: "Add validation for negative values"
 13:         public int MaxRetries { get; set; }

[4 matches shown, 1 omitted]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot search without files |
| Invalid regex syntax | Return error with position of syntax issue |
| No matches found | Return "no matches" with files searched count |
| Regex times out (pathological pattern) | Return error suggesting pattern simplification |

## Verification

| Environment | How |
|-------------|-----|
| Local | Regex for known pattern; verify matches and captures correct |
| Automated tests | Assert capture groups extracted correctly; match positions accurate |
| Production | Track regex compilation failures; monitor for timeout patterns |

## Related

- `grep.md` — literal string search (faster for exact matches)
- `find.md` — semantic search (for concepts)
- `astgrep.md` — syntax-aware structural search
