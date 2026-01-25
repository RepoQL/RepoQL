# Read Lint Flow

Show diagnostics (warnings and errors) from matched files.

## Why This Matters

Lint surfaces problems in specific scope—compilation errors, style warnings, potential bugs. Agents can prioritize fixes and understand code health.

| Without | With |
|---------|------|
| Run linter, parse output manually | Structured diagnostics with locations |
| See all problems in project | Scoped to files that matter |
| No severity filtering | Filter to errors only or warnings only |

## Trigger

```
read("<pattern> => lint", tokenBudget)              # All diagnostics
read("<pattern> => lint: errors", tokenBudget)     # Errors only
read("<pattern> => lint: warnings", tokenBudget)   # Warnings only
```

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. Diagnostic Retrieval
**Actor**: Read tool
**Action**: Retrieve annotations of type 'lint' for matched files
**Output**: Diagnostics with locations and messages

Diagnostics come from:
- Language server analysis (compilation errors)
- Linter rules (style, potential bugs)
- Custom analyzers (project-specific rules)

### 3. Severity Filtering
**Actor**: Read tool
**Action**: Filter by severity if specified
**Output**: Filtered diagnostics

Severity levels:
- **Error**: Compilation failures, critical issues
- **Warning**: Potential problems, style violations
- **Info/Hint**: Suggestions (excluded by default)

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format diagnostics with location and context
**Output**: Diagnostic list grouped by file

Result elements:
- File URI with line fragment
- Severity indicator
- Rule ID (for lookup/suppression)
- Message describing the issue
- Code snippet showing diagnostic location (if budget allows)

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many diagnostics as fit within token budget
**Output**: Diagnostics that fit, with count of omitted in footer

Priority: errors before warnings, then by file order.

## Termination

Flow completes when:
- Diagnostics rendered with locations
- Footer reports total by severity and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs => lint: errors

file:///src/Auth/TokenService.cs#line=42 [error] CS0103
  The name 'tokne' does not exist in the current context

 41:         var existing = await _store.GetAsync(refreshToken);
>42:         if (tokne?.IsExpired ?? true)
                 ^^^^^
 43:             throw new TokenExpiredException();

file:///src/Auth/TokenService.cs#line=58 [error] CS1002
  ; expected

 57:         _logger.LogInformation("Token refreshed")
>58:         return newToken;
             ^

[2 errors, 0 warnings in scope (3 warnings filtered)]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot lint without files |
| No diagnostics in scope | Return "no diagnostics" (code is clean) |
| Diagnostics not available (not indexed) | Return "diagnostics pending" with status |

## Verification

| Environment | How |
|-------------|-----|
| Local | Request lint for file with known issues; verify all appear with correct locations |
| Automated tests | Assert: file with syntax error shows error diagnostic |
| Production | Track diagnostic distribution; monitor for missing analyzer coverage |

## Related

- `content.md` — full file content (includes diagnostics inline)
- Annotations table — underlying diagnostic storage
