---
name: statusline-builder
description: "Compose a personalized Claude Code status line from a menu of RepoQL-powered widgets — session cost, burn rate, cache hit ratio, context and rate-limit gauges, PR state, index health, live operations, git state. Shows the user what is possible, lets them pick, assembles and tests the script. Use when asked to set up, build, improve, customize, or extend a status line."
---

# Statusline Builder

A status line is a one-line dashboard Claude Code renders under the prompt, fed the live session as JSON on stdin. RepoQL powers a menu of widgets — session cost and burn rate, cache hits, context and rate-limit gauges, PR state, index health, live operations, git state. This skill shows the user what is possible, lets them pick, and assembles a tested, personalized script from verified fragments.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
read("help:///skills/statusline-builder/** => tree: headlines", 5000)
```

Then read each file you need:

```
read("help:///skills/statusline-builder/SKILL.md => content", 10000)
```
