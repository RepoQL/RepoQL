---
name: effective-repoql
description: "Effective use of RepoQL — the workflow, the techniques, and the wild magic. Use when you want to get more out of RepoQL or need to understand what's possible."
---

# Effective RepoQL

RepoQL is wild magic — composable, responsive to intent, and forgiving. This skill teaches you to wield it.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
read("help:///skills/effective-repoql/** => tree: headlines", 5000)
```

Then read each file you need:

```
read("help:///skills/effective-repoql/SKILL.md => content", 10000)
```
