---
description: Query embedded docs - search RepoQL documentation
allowed-tools: mcp__repoql__explore, mcp__repoql__read
---

# RepoQL Help

Search and read RepoQL's embedded documentation.

## Your Question

$ARGUMENTS

## Find Documentation

**Search by topic:**
```
explore(intent=Locate, uriGlob="help://**", keywords="$ARGUMENTS", tokenBudget=1500)
```

**Browse all docs:**
```
explore(intent=Inventory, uriGlob="help://**", tokenBudget=1000)
```

## Key Documentation Areas

| Path | Contents |
|------|----------|
| `help:///quickstart.md` | Getting started, SQL patterns |
| `help:///repoql/tools/query/views/*` | View definitions (Files, Functions, Types) |
| `help:///repoql/tools/query/functions/*` | Function signatures (search, snippet, etc.) |
| `help:///repoql/tools/query/formats/*` | Format-specific features (C#, Markdown, etc.) |

## Quick Queries

**List all help topics:**
```sql
query("SELECT uri, headline FROM Files WHERE uri LIKE 'help://%' ORDER BY uri")
```

**Find specific topic:**
```sql
query("SELECT uri, headline FROM Files WHERE uri LIKE 'help://%' AND headline LIKE '%search%'")
```

## Read Documentation

Once you find a help URI, read it:
```
read("help:///quickstart.md", 3000)
read("help:///repoql/tools/query/functions/search.md", 2000)
```

## Ask Questions About Docs

Use the question modifier for synthesis:
```
read("help:///repoql/tools/query/** => question: How do I use the search function?", 2500)
```
