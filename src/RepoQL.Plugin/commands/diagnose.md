---
description: Troubleshooting - check RepoQL health and diagnose issues
allowed-tools: mcp__repoql__query, Bash
---

# RepoQL Diagnose

Check RepoQL health and troubleshoot issues.

## Your Issue

$ARGUMENTS

## Quick Diagnostics

Run the built-in diagnostic command:
```sql
query(":diagnostics:")
```

## Common Checks

**Index status:**
```sql
SELECT COUNT(*) as total_files,
       SUM(CASE WHEN error_count > 0 THEN 1 ELSE 0 END) as files_with_errors
FROM Files;
```

**Semantic search readiness:**
```sql
SELECT uri, headline FROM Files WHERE uri LIKE 'help:///repoql/status%';
```

**Recent indexing activity:**
```sql
SELECT uri, headline FROM Files ORDER BY uri DESC LIMIT 20;
```

## Process Checks

**Is RepoQL running?**
```bash
pgrep -f "repoql" || echo "RepoQL not running"
```

**Check MCP connection:**
The `query(":diagnostics:")` command tests the full stack.

## Common Issues

| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| "Connection refused" | RepoQL not running | Start with `repoql mcp` |
| Empty search results | Index not built | Wait for indexing or run `repoql index` |
| Slow semantic search | Embeddings generating | Check diagnostics for progress |
| Missing files | Files not in scope | Check `.repoql/config.toml` patterns |

## Reset Index

If index is corrupted:
```bash
rm -rf .repoql/index.db
repoql index
```

## Get Help

```
explore(intent=Locate, uriGlob="help://**", keywords="troubleshooting errors", tokenBudget=1500)
```
