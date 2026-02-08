---
description: Vision for the Claude Code plugin — what it means to give an agent structural awareness of any codebase
tags: [plugin, claude-code, integration, orientation, skills, agents]
audience: { human: 60, agent: 40 }
purpose: { north-star: 100 }
---

# Claude Code Plugin: What Great Looks Like

> An agent with RepoQL installed has a sense that agents without it don't — structural awareness of any codebase, from the first message.

An agent opens a session in an unfamiliar repository. Before the user types anything, the agent already knows: 847 files across 12 languages, a service layer with 6 primary endpoints, a test suite covering 3 of them, documentation with a getting-started guide and an architecture overview. It didn't read a single file. It didn't burn a single tool call. That context was waiting when the session started — injected by a hook that ran `repoql read` against the repo and the embedded docs. The user says "how does auth work?" and the agent doesn't grep, doesn't guess filenames, doesn't open 15 files hoping to find the right one. It explores with intent, reads the three symbols that matter, and answers. The plugin didn't teach the agent a new workflow — it gave it the instincts to use RepoQL the way it was meant to be used. Explore first. Don't read files for structure. Let the graph do the work.

---

## Orientation

- An agent should arrive in any session already knowing the shape of the repository — file count, languages, directory structure, key entry points
- An agent should arrive already knowing what documentation exists — headlines, topics, where to look for answers about the tool itself
- An agent should be able to act on this orientation context without spending tool calls to acquire it
- An agent should be able to distinguish a fresh repo from one it's seen before based on injected context

```
# Session starts. Agent already sees:
847 files | 12 languages | src/Services (6), src/Models (23), tests/ (142)
Docs: getting-started.md, architecture.md, API-reference.md, deployment.md
```

---

## Discovery

- An agent should be able to find where a concept lives without knowing filenames, directories, or symbol names
- An agent should reach for explore before read as a natural instinct — not because it was told to, but because the plugin shaped its defaults
- An agent should be able to answer structural questions — "what depends on X?", "how many endpoints exist?", "which files changed recently?" — through SQL, not file reading
- An agent should be able to navigate from headline to structure to specific symbol with progressive disclosure, spending tokens proportional to need

---

## Learning

- An agent should be able to use RepoQL effectively on its first session with the plugin installed — no prior experience required
- An agent should acquire effective patterns (explore-first, intent matching, token budgeting) through skills that activate in context, not through documentation it has to find and read
- An agent should be able to discover what RepoQL can do by querying `help://` — the tool teaches itself
- An agent should get better at using RepoQL over a session as skills reinforce effective patterns at the moment they're relevant

---

## Investigation

- An agent should be able to delegate deep research to a subagent that comes pre-configured with RepoQL patterns — explore, gather evidence, synthesize, cite
- An agent should be able to trust that the subagent's output includes evidence with specific locations, not vague summaries
- An agent should be able to launch an investigation with a question and get back a structured report with findings, evidence, relationships, and confidence levels

---

## Installation

- An agent should be able to use RepoQL regardless of how it was installed — `repoql install`, the hosted install script, or direct plugin installation
- An agent should get the same capabilities regardless of the installation path — the end state is identical
- An agent should be able to bootstrap the missing piece — if the plugin is installed but the binary isn't, the plugin gets it; if the binary is installed but the plugin isn't, the binary installs it
- A user should be able to install from a single command and have everything work on their next session

---

## Adaptation

- An agent should benefit from the plugin in any repository, any language, any size — not just repos that have opted in to RepoQL
- An agent should get useful orientation even in a repo that has never been indexed — the plugin triggers indexing and provides what it can
- An agent should be able to use the same skills and patterns whether the repo has 10 files or 100,000
- An agent should be able to use RepoQL alongside other MCP servers without conflicts — the plugin adds a sense, it doesn't replace the agent's existing tools

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Agent arrives already oriented | Zero tool calls wasted on "what's in this repo?" |
| Explore-first is instinct, not instruction | The right pattern happens naturally |
| First session is effective | No learning curve, no "read the docs first" |
| Deep investigation is delegatable | Structured reports with evidence, not vague summaries |
| Any install path converges | User never ends up in a broken half-state |
| Works in any repo | Universal value, not opt-in per project |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Require the agent to orient itself each session | An agent should arrive already knowing the shape |
| Require reading docs to learn the tool | An agent should acquire patterns through contextual skills |
| Require the user to install from both directions | An agent should get everything from a single install path |
| Require repo-specific configuration | An agent should benefit from the plugin in any repo |
| Give the agent RepoQL tools without teaching effective use | An agent should have instincts, not just access |
| Provide orientation that costs tool calls | An agent should have context injected before it acts |

---

*An agent with the plugin installed should feel like it gained a sense — and working without it should feel like losing one.*
