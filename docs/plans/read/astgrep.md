# Plan: astgrep Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — AstGrepHandler

## Scope

**Covers:**
- `AstGrepHandler` implementing `IModifierHandler`
- Shell out to ast-grep binary
- Parse results into snippets
- Graceful fallback when binary unavailable

**Does not cover:**
- ast-grep installation
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can search by code structure, not text
- Find patterns regardless of formatting/naming
- Refactoring support

## Prerequisites

- Plan: ModifierDispatcher complete
- ast-grep binary in PATH (optional)
- `ReadDocument.TextContent` for fallback

## North Star

Match code structure, not text. Find all functions returning Task regardless of names or formatting.

## Done Criteria

### Handler Registration
- The AstGrepHandler shall register with modifier name `astgrep`
- The AstGrepHandler shall handle `CanHandle("astgrep")` returning true

### Parameter Parsing
- The parameter shall be the ast-grep pattern
- When parameter is empty, return error requesting pattern

### Binary Detection
- The handler shall check if `ast-grep` or `sg` binary is available
- When unavailable, return graceful error with installation instructions

### Execution
- The handler shall write matched file content to temp files
- The handler shall invoke ast-grep with pattern on temp files
- The handler shall parse JSON output from ast-grep
- The handler shall format matches as snippets with metavariable bindings

### Output Format
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

[3 matches, 0 omitted]
```

### Budget Handling
- Show matches that fit within budget
- Footer shows: `[N matches shown, M omitted]`

### Language Support
- Infer language from file extension
- Support: C#, TypeScript, JavaScript, Python, Go, Rust, Java

## Constraints

- **External dependency**: ast-grep binary must be installed separately
- **Graceful degradation**: Clear error when binary unavailable
- **Temp file cleanup**: Always clean up temp files after execution

## References

- [Flow: astgrep.md](../../flows/future/read/astgrep.md)
- [ast-grep documentation](https://ast-grep.github.io/)
- [ast-grep pattern syntax](https://ast-grep.github.io/guide/pattern-syntax.html)

## Error Policy

- Binary not found: Return "ast-grep not installed. Install from https://ast-grep.github.io/"
- Invalid pattern: Return ast-grep error message
- Language not supported: Return "ast-grep does not support {extension} files"
- Parse error in source: Skip file, note in footer
