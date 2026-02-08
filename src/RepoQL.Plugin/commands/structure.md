---
description: Directory overview - see codebase organization at a glance
allowed-tools: mcp__repoql__read
---

# RepoQL Structure

See the codebase organization without reading files. Uses pre-indexed headlines and structure data.

## Your Request

$ARGUMENTS

## Quick Patterns

**Full directory tree (folders only):**
```
read("file:///** => tree: folders", 5000)
```

**Tree with files:**
```
read("file:///src/** => tree: files", 1000)
```

**Tree with headlines (one-line summaries):**
```
read("file:///src/** => tree: headlines", 2000)
```

**Specific directory structure:**
```
read("file:///src/Services/** => tree: headlines", 1500)
```

## Tree Modifiers

| Modifier | Shows | Token Cost |
|----------|-------|------------|
| `tree: folders` | Just directories with file counts | Low |
| `tree: files` | Directories + filenames | Medium |
| `tree: headlines` | Directories + files + one-line summaries | Higher |

## Combining with Exclusions

**Source without tests:**
```
read("file:///src/**;!**/tests/** => tree: headlines", 2000)
```

**Only C# files:**
```
read("file:///src/**/*.cs => tree: headlines", 2000)
```

## Budget Tips

- Broader scopes need more budget or simpler modifiers
- 100 files at 2000 tokens = ~20 tokens each = headlines
- Narrow scope to get richer detail
