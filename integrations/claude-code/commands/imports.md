---
description: External repository management - import GitHub repos for cross-repo queries
allowed-tools: mcp__repoql__import, mcp__repoql__query, mcp__repoql__explore
---

# RepoQL Imports

Import external GitHub repositories to enable cross-repo queries and analysis.

## Your Request

$ARGUMENTS

## Import a Repository

```
import("github://owner/repo")
import("github://owner/repo", sparse=["src/**", "docs/**"])
```

## Check Import Status

```sql
query("SELECT uri, status, progress_pct FROM Imports")
```

## Query Across Repos

Once imported, external repos are queryable like local files:

```sql
query("SELECT uri, headline FROM Files WHERE uri LIKE 'github://owner/repo/%'")
```

```
explore(intent=Locate, uriGlob="github://owner/repo/**", keywords="authentication", tokenBudget=1500)
```

## Sparse Imports

For large repos, import only what you need:

```
import("github://facebook/react", sparse=["packages/react/**", "packages/react-dom/**"])
```

## Common Patterns

**Import a library to understand its API:**
```
import("github://microsoft/TypeScript")
explore(intent=Inventory, uriGlob="github://microsoft/TypeScript/src/**", tokenBudget=1000)
```

**Compare implementations across repos:**
```sql
query("SELECT uri, name FROM Functions WHERE name LIKE '%validate%' AND uri LIKE 'github://%'")
```

## Notes

- Imports run in the background; check status with query
- Semantic search is available after embedding generation completes
- Use sparse imports for faster indexing of large repos
