# Codex General Use - Discovery Notes

## What is Codex?

OpenAI's agentic coding tool (GPT-5.2-codex). Three invocation modes:
- `codex` - Interactive TUI, back-and-forth, multi-step work
- `codex exec` - One-shot non-interactive, scripts/CI
- `codex review` - Code review mode (separate skill: codex-review)

When running as MCP server, Claude Code invokes via:
- `mcp__codex__codex` - Start session
- `mcp__codex__codex-reply` - Continue with threadId

## Best Use Cases

**Task Completion:**
- Implementing features from tickets
- Bug fixes across multiple files
- Refactoring with clear scope
- Config/docs updates

**Debugging:**
- Tracing errors with evidence
- Adding diagnostics
- Narrowing regressions
- Race condition analysis (with proper context)

**Test Work:**
- Writing unit/integration tests
- Adding fixtures/mocks
- Coverage around risky paths

**Repo Hygiene:**
- Removing dead code
- Normalizing patterns
- Extracting shared helpers

**Automation:**
- Migration scripts
- Data transforms
- One-off cleanups

## Core Insight: Lacks Intuition for Unstated Intent

Codex is highly capable but won't fill in the blanks:
- Claude asks "what did they probably mean?"
- Codex asks "what did they say?"

State the steps you want, not just the outcome.

## Prompt Structures

### Task Completion
```
Goal: Add X behavior to Y.
Acceptance: A, B, C.
Constraints: must keep API Z stable; avoid new deps.
Scope: files under src/FeatureX only.
Tests: run `dotnet test` after changes.
```

### Exploration/Survey
```
Survey the indexing pipeline for bottlenecks.
Summarize hotspots and propose 2-3 options with pros/cons.
```

### Debugging
```
Bug: A crashes on B.
Repro: steps...
Expected: ...
Actual: ...
Logs: ...
Environment: ...
Last known good: commit abc123
```

### Concurrency/Race Conditions
```
Bug: intermittent failure under load; suspected race.
Repro: steps, load profile, #threads, OS, runtime flags.
Expected vs actual.
Artifacts: logs, stack traces, thread dumps, timing info.
Code pointers: files/modules suspected.
Constraints: don't change API; must stay lock-free.

Please:
1) identify shared state / lock order / atomicity violations
2) propose instrumentation points
3) produce minimal fix + tests
```

## Context Needed for Ticket Completion

1. **Goal and acceptance criteria** - What "done" means
2. **Constraints** - APIs to preserve, perf limits, no new deps
3. **Scope** - Paths/modules to touch or avoid
4. **Test instructions** - How to verify
5. **Examples/fixtures** - Inputs, outputs, error logs
6. **Repo-specific rules** - e.g., "use TUnit, not xUnit"

## Failure Modes

| Failure | Cause |
|---------|-------|
| Wrong implementation | Ambiguous success criteria |
| Breaks existing code | Hidden constraints not stated |
| Can't diagnose bug | No repro steps, logs, timing |
| Misses coupling | Large scope without boundaries |
| Guesses incorrectly | Can't run build/test |
| Misdiagnoses race | No thread dumps or timing info |

## One Call vs Multiple Calls

**Multiple calls when:**
- Staged decision needed (investigate → propose → implement)
- Requirements uncertain
- High risk/regression potential
- Want to review options first

**One big prompt when:**
- Scope is clear and bounded
- Acceptance criteria firm
- OK with direct edits

## Direct Edits vs Diffs

Codex CAN apply changes directly. Say "produce diffs only" or "no writes" if you want review-first.

## MCP Isolation

When invoked via MCP:
- Does NOT see Claude's conversation history
- Only sees prompt + what it retrieves via tools
- Must forward context explicitly

## Relationship to codex-review Skill

The `codex-review` skill covers code review specifically. This skill covers:
- Task completion
- Debugging
- Test writing
- Exploration
- Any complex delegated work that isn't pure review
