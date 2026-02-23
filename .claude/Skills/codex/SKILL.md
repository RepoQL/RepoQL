---
name: codex
description: Delegate complex tasks to Codex (GPT-5.2-codex). Use for ticket completion, debugging, test writing, race condition analysis, refactoring.
---

# Codex

Codex excels at complex, well-defined tasks. Delegate when the task is clear and you want execution, not exploration.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
read("help:///skills/codex/** => tree: headlines", 5000)
```

Then read each file you need:

```
read("help:///skills/codex/SKILL.md => content", 10000)
```
