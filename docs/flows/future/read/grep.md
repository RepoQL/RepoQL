# Read Grep Flow

Literal string search within matched files, returning snippets.

## Why This Matters

Grep finds exact text matches—function names, error messages, configuration keys. When you know the literal string, grep is faster and more precise than semantic search.

| Without | With |
|---------|------|
| Semantic search may find related but not exact | Literal match guarantees exact string found |
| Must read files to find occurrences | Direct to every occurrence with context |

## Trigger

`read("<pattern> => grep: <search_string>", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. String Search
**Actor**: Read tool
**Action**: Search for literal string occurrences in matched files
**Output**: All matches with file locations

Search is case-sensitive by default. Matches within any text content (code, comments, strings).

### 3. Snippet Generation
**Actor**: Read tool
**Action**: Generate snippets centered on each match with surrounding context
**Output**: Code snippets with line numbers, match highlighted

Snippet elements:
- File URI with line fragment
- Line numbers matching actual file
- Match position marked within line
- Surrounding context lines

### 4. Budget Fitting
**Actor**: Read tool
**Action**: Include as many snippets as fit within token budget
**Output**: Snippets that fit, with count of omitted matches in footer

Matches shown in file order, then by line number within file.

## Termination

Flow completes when:
- Snippets rendered for matches that fit within budget
- Footer reports total matches found, matches shown, files with matches, and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#line=23  [1 of 4 in file]

 21:     private readonly ILogger _logger;
 22:
>23:     public TokenService(ITokenStore store, ILogger<TokenService> logger)
 24:     {
 25:         _store = store;

file:///src/Auth/TokenValidator.cs#line=15  [1 of 2 in file]

 13:     public class TokenValidator
 14:     {
>15:         private readonly TokenService _tokenService;
 16:

[6 matches shown in 2 files, 0 omitted]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot search without files |
| No matches found | Return "no matches" with files searched count |
| Search string empty | Return error with message |

## Verification

| Environment | How |
|-------------|-----|
| Local | Grep for known string; verify all occurrences found |
| Automated tests | Assert match count equals manual count; line numbers accurate |
| Production | Track grep usage; no special monitoring needed (deterministic) |

## Related

- `regex.md` — pattern-based search
- `find.md` — semantic search (for concepts, not literals)
- `astgrep.md` — syntax-aware structural search
