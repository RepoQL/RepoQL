---
name: research
description: Evidence-first research mode with mandatory citations. Invoke at the START of research, not when writing up results.
---

# Research

Research is stewardship. You hold space for someone else's decision.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Read the skill in full:

```
read("help:///skills/research/SKILL.md => content", 5000)
```

When the user requests parallel research, spawn one read-heavy subagent per independent direction using `gpt-5.6-terra` with high reasoning (or a stronger model for an unusually ambiguous direction). Tell every subagent to read `help:///skills/research/subagent.md` in full before gathering evidence. The bundled `researcher` brief carries the same contract for Codex clients that load plugin agents.
