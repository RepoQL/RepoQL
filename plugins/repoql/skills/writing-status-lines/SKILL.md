---
name: writing-status-lines
description: "Build a Claude Code status line powered by RepoQL — blend the session transcript with the indexed graph (cost, cache, tools, index state, dashboard link) and avoid the footguns that make a status line break or lie. Use when writing or extending a status-line script."
---

# Writing RepoQL-Powered Status Lines

A status line is a one-line dashboard Claude Code renders under the prompt, fed the live session as JSON on stdin. RepoQL lets it blend the session transcript with the indexed graph — surfacing cost, cache-hit ratio, per-tool footprint, index state, imports, and a clickable dashboard link. This skill encodes the contract, the query toolkit, and the rules you can't derive from first principles.

## Load

Skill files are irreducible — they cannot be summarized and still communicate what they need to. Browse what's available, then read each file you need with `=> content` to get full text — never structure.

```
read("help:///skills/writing-status-lines/** => tree: headlines", 5000)
```

Then read each file you need:

```
read("help:///skills/writing-status-lines/SKILL.md => content", 10000)
```
