---
name: code-intelligence
description: Code structure discovery using RepoQL. Use when answering questions about where code is, what exists, how things are organized, or finding implementations. Triggers on "where is", "what files", "show me the structure", "find the implementation".
---

# Code Intelligence

RepoQL indexes repositories into a queryable graph. Answer structural questions without reading files.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
read("help:///skills/code-intelligence/** => tree: headlines", 5000)
```

Then read each file you need:

```
read("help:///skills/code-intelligence/SKILL.md => content", 10000)
```
