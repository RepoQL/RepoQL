---
name: sql-expert
description: SQL query crafting for RepoQL. Use when user needs aggregations, counting, cross-file analysis, graph traversal, or complex filtering. Triggers on "how many", "count the", "what calls", "find all X that Y", pattern matching queries.
---

# SQL Expert

Craft SQL queries for RepoQL's DuckDB database. Views, functions, and composition patterns.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
read("help:///skills/sql-expert/** => tree: headlines", 5000)
```

Then read each file you need:

```
read("help:///skills/sql-expert/SKILL.md => content", 10000)
```
