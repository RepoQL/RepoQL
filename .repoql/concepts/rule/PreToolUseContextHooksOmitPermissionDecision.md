---
description: "A PreToolUse hook that only injects additionalContext must omit permissionDecision entirely."
tags:
  - "hooks"
  - "PreToolUse"
  - "permissionDecision"
  - "defer"
  - "headless"
  - "claude -p"
  - "print mode"
  - "tool_deferred"
  - "additionalContext"
  - "claude-code"
category: rule
universal: false
relevance: "file:///plugins/*/scripts/*hook*.sh;file:///plugins/*/hooks/**"
verification: "file:///plugins/repoql/scripts/concepts-write-hook.sh#symbol=*"
ttl: 180
verified:
  date: "2026-07-23"
  commit: "ae30cd0ad0b5"
---

## Capsule: PreToolUseContextHooksOmitPermissionDecision

**Invariant**
A PreToolUse hook that only injects additionalContext must omit permissionDecision entirely.

**Why**
In Claude Code's PreToolUse protocol, "defer" is not "no opinion" — in print mode (claude -p, i.e. every CI/cron/headless run) it pauses the tool call and terminates the session with exit 0 and stop_reason "tool_deferred", so the write never happens and the caller sees success. Interactive sessions ignore defer, which hides the bug during plugin development. "allow" is also wrong: it silently bypasses the user's permission prompts and allowlists for every matched call. Omitting permissionDecision is the only neutral choice — additionalContext is honored on its own. This killed 16 headless PR reviews between 2026-07-17 and 2026-07-22 before being diagnosed (fixed in plugin 1.6.19).

**Example**
**STUB** - needs a concrete example.

**Depth**
- **STUB** - add distinctions, trade-offs, and boundaries.
