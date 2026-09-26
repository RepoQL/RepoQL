---
name: research
description: Evidence-first research mode with mandatory citations. Invoke at the START of research, not when writing up results.
tags: ["skill", "research", "evidence", "citations", "synthesis"]
---

# Research

Research is stewardship. You hold space for someone else's decision.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Read the skill in full:

```
repoql_read(uriGlob="help:///skills/research/SKILL.md => content", tokenBudget=5000)
```

When the user asks for parallel research, spawn one subagent per independent direction and tell each to read `help:///skills/research/subagent.md` in full (with `repoql_read`) before gathering evidence.
