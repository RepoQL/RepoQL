---
name: skill-builder
description: This skill guides intentional skill design. Use when creating, improving, or reviewing OpenClaw skills. Requires a zone assessment to clarify what kind of skill is being built before writing content.
---

# Skill Builder

A skill encodes what cannot be derived from first principles. Before writing, understand what you're encoding.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
repoql_read(uriGlob="help:///skills/skill-builder/** => tree: headlines", tokenBudget=5000)
```

Then read each file you need:

```
repoql_read(uriGlob="help:///skills/skill-builder/SKILL.md => content", tokenBudget=10000)
```
