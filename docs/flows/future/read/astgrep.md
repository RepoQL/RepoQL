# Read Astgrep Flow

Syntax-aware structural search within matched files, returning snippets.

## Why This Matters

Astgrep matches code structure, not text. It finds "all functions returning Task" or "all try-catch blocks" regardless of formatting, naming, or whitespace. Essential for refactoring and pattern enforcement.

| Without | With |
|---------|------|
| Regex breaks on formatting variations | Structure matches regardless of style |
| Miss patterns with different variable names | Match by shape, not identifiers |
| False positives from comments/strings | Only matches actual code structure |

## Trigger

`read("<pattern> => astgrep: <ast_pattern>", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs with language info
**Failure**: Invalid pattern returns error with suggestion

### 2. Pattern Compilation
**Actor**: Read tool
**Action**: Compile ast-grep pattern for target language
**Output**: Compiled structural pattern
**Failure**: Invalid ast-grep syntax returns error with documentation link

Pattern syntax follows [ast-grep](https://ast-grep.github.io/) conventions. Language inferred from file extensions.

### 3. AST Search
**Actor**: Read tool
**Action**: Parse files and search AST for structural matches
**Output**: All structural matches with file locations

Matching ignores:
- Whitespace and formatting
- Comments
- Specific identifier names (when using metavariables)

### 4. Snippet Generation
**Actor**: Read tool
**Action**: Generate snippets showing matched code structures
**Output**: Code snippets with line numbers, matched structure highlighted

Snippet elements:
- File URI with line fragment
- Line numbers matching actual file
- Complete matched structure (not truncated mid-expression)
- Metavariable bindings if pattern uses them

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
Pattern: try { $$$ } catch ($TYPE $ERR) { $$$ }

file:///src/Data/Repository.cs#line=45,52

>45:         try
>46:         {
>47:             await _db.ExecuteAsync(sql);
>48:         }
>49:         catch (DbException ex)
>50:         {
>51:             _logger.LogError(ex, "Database error");
>52:         }
              └─ $TYPE=DbException, $ERR=ex

[3 matches shown, 0 omitted]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot search without files |
| Invalid ast-grep syntax | Return error with syntax help |
| Language not supported by ast-grep | Return error listing supported languages |
| No matches found | Return "no matches" with files searched count |
| Parse error in source file | Skip file, note in footer |

## Verification

| Environment | How |
|-------------|-----|
| Local | Search for structural pattern; verify matches ignore formatting differences |
| Automated tests | Assert: same pattern matches reformatted code; doesn't match similar text in comments |
| Production | Track language coverage; monitor parse failure rates |

## Related

- `grep.md` — literal text search
- `regex.md` — pattern text search
- `find.md` — semantic concept search
