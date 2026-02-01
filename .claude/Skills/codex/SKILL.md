---
name: codex
description: Delegate complex tasks to Codex (GPT-5.2-codex). Use for ticket completion, debugging, test writing, race condition analysis, refactoring.
tags: [codex, delegation, tasks, debugging, testing, openai]
audience: { human: 20, agent: 80 }
purpose: { gestalt: 25, reference: 40, concepts: 25, high-agency-process: 10 }
zones: { knowledge: 35, process: 15, constraint: 15, wisdom: 35 }
---

# Codex

Codex (GPT-5.2-codex) excels at complex, well-defined tasks: implementing tickets, debugging with evidence, writing tests, finding race conditions, refactoring. Delegate when the task is clear and you want execution, not exploration.

## Quick Reference

| Task Type | Use | Prompt Style |
|-----------|-----|--------------|
| Implement a ticket | `mcp__codex__codex` or `codex exec` | Goal + acceptance + constraints + scope |
| Debug with evidence | `mcp__codex__codex` | Repro + expected/actual + logs + environment |
| Find race conditions | `mcp__codex__codex` | Timing + thread dumps + suspected code |
| Write tests | `mcp__codex__codex` or `codex exec` | What to test + coverage goals + fixtures |
| Explore/survey | `mcp__codex__codex` | Focus areas + request options with tradeoffs |
| Code review | `codex-review` skill | See separate skill |

---

## Capsule: CodexLacksIntuition

**Invariant**
Codex is highly capable but won't intuit what you wanted but didn't say.

**Example**
"Fix the auth bug" → Codex needs repro steps, logs, expected behavior.
"The auth endpoint returns 401 when token is valid. Logs show X. Expected Y. Repro: call /api/auth with valid JWT." → Codex can work.
//BOUNDARY: Not dumb—very capable. Just won't fill in blanks about unstated intent.

**Depth**
- Claude asks "what did they probably mean?" and infers
- Codex asks "what did they say?" and executes that well
- State steps and context, not just desired outcome
- Codex excels at execution; you provide the strategy

---

## Capsule: YinYang

**Invariant**
Claude and Codex are complementary partners. Claude translates vague intent into explicit steps; Codex executes systematically and surfaces what you wouldn't think to look for.

**Example**
User: "Check the indexing pipeline for issues"
Claude translates → "Task: Investigation. Steps: 1) Find shared state 2) Trace synchronization 3) Identify races"
Codex executes → finds 3 race conditions Claude wouldn't have looked for
Claude synthesizes → explains findings, offers next steps
//BOUNDARY: Neither complete alone. The handoff is where Claude adds value.

**Depth**
- Claude: inference, intent, synthesis. Asks "what did they probably mean?"
- Codex: execution, precision, depth. Asks "what did they say?"
- Together: vague intent → explicit steps → systematic execution → synthesized insight
- Codex finds issues you wouldn't think to ask about—delegate investigation tasks
- The quality of your translation determines the quality of Codex's output

---

## Capsule: WhenToDelegate

**Invariant**
Delegate to Codex when the task benefits from systematic execution. Keep in Claude when you need inference or the task is still forming.

**Example**
Well-defined ticket with acceptance criteria → Codex
"Help me figure out what's wrong" → Claude (needs inference)
"Find race conditions in the indexing pipeline" with context → Codex (systematic search finds what you wouldn't ask about)
//BOUNDARY: Claude shapes the question. Codex answers it thoroughly.

---

## Invocation Modes

| Mode | Command | Best For |
|------|---------|----------|
| Interactive | `codex` (TUI) | Multi-step work, clarifications |
| One-shot | `codex exec "prompt"` | Scripts, CI, bounded tasks |
| MCP | `mcp__codex__codex` | Integration with Claude Code |
| Continue | `mcp__codex__codex-reply` | Follow-up on previous work |

### MCP Response Format

```json
{
  "threadId": "019c187f-a958-76f3-951d-9c0974f73aaa",
  "content": "I've implemented the feature. Changes made:\n- Added X to Y\n- Updated tests..."
}
```

Save the `threadId` for follow-up calls.

---

## Prompt Templates

### Ticket Completion

```
Goal: [What to build/fix]
Acceptance criteria:
- [Criterion 1]
- [Criterion 2]
Constraints: [APIs to preserve, perf limits, no new deps]
Scope: [Paths/modules to touch or avoid]
Tests: [How to verify - e.g., "run dotnet test"]
Repo rules: [e.g., "use TUnit not xUnit", "use AwesomeAssertions"]
```

### Debugging

```
Bug: [Description]
Repro steps:
1. [Step 1]
2. [Step 2]
Expected: [What should happen]
Actual: [What happens]
Logs: [Relevant error messages, stack traces]
Environment: [OS, runtime, versions]
Last known good: [Commit or version where it worked]
```

### Race Condition Analysis

```
Bug: Intermittent failure under load; suspected race.
Repro: [Load profile, #threads, timing]
Expected vs actual: [What should happen vs what happens]
Artifacts: [Thread dumps, timing logs, stack traces]
Suspected code: [Files/functions to investigate]
Constraints: [Don't change API, must stay lock-free, perf budget]

Please:
1) Identify shared state / lock order / atomicity violations
2) Propose instrumentation points
3) Produce minimal fix + tests
```

### Test Writing

```
Goal: Add tests for [component/feature]
Coverage goals: [What scenarios to cover]
Test framework: [TUnit, xUnit, etc.]
Fixtures available: [Existing test helpers, mocks]
Constraints: [No external dependencies, must run fast]
```

### Exploration/Survey

```
Survey [area] for [concern].
Focus on: [Specific aspects]
Output: 2-3 options with pros/cons, not a single recommendation.
```

---

## Context Checklist

Before delegating, ensure you provide:

- [ ] **Goal** - What "done" looks like
- [ ] **Acceptance criteria** - How to verify success
- [ ] **Constraints** - What NOT to do
- [ ] **Scope** - Files/modules in play
- [ ] **Test command** - How to verify changes
- [ ] **Repo rules** - Framework choices, naming conventions
- [ ] **Evidence** (for bugs) - Logs, repro steps, environment

---

## One Call vs Multiple

| Use Multiple Calls | Use One Big Prompt |
|-------------------|-------------------|
| Staged decisions (investigate → propose → implement) | Clear, bounded scope |
| Uncertain requirements | Firm acceptance criteria |
| High risk / regression potential | OK with direct edits |
| Want to review options first | Trust Codex to execute |

### Iterative Refinement Pattern

The most powerful pattern: **identify → propose → implement**

1. **First call**: "Investigate X, identify issues" → Codex finds problems
2. **Reply**: "Propose mitigations for issue #2" → Codex designs solutions
3. **Reply**: "Implement the fix for the double-decrement race" → Codex writes code

Each call builds on the previous. Codex retains full context via `threadId`. This lets you validate each stage before committing to the next.

---

## Direct Edits vs Review

Codex CAN apply changes directly. Control this:

| Want | Say |
|------|-----|
| Direct edits | (default behavior) |
| Diffs only | "Produce diffs only, don't write files" |
| Review first | "Propose changes, wait for approval before applying" |

---

## Failure Modes

| Failure | Cause | Prevention |
|---------|-------|------------|
| Wrong implementation | Ambiguous acceptance criteria | State explicit "done" definition |
| Breaks existing code | Hidden constraints | List what NOT to change |
| Can't diagnose bug | Missing repro/logs | Provide full evidence |
| Misses coupling | Scope too broad | Bound to specific modules |
| Guesses wrong | Can't run tests | Ensure test command works |
| Misdiagnoses race | No timing info | Provide thread dumps, timing |

---

## When NOT to Use Codex

- **Exploratory thinking** - Use Claude for "help me understand"
- **Ambiguous requirements** - Clarify first, then delegate
- **Need inference** - Claude fills in blanks; Codex doesn't
- **Simple questions** - Overkill; just ask Claude
- **Code review** - Use `codex-review` skill instead

---

## MCP Integration Notes

- Codex does NOT see Claude's conversation history
- Forward relevant context explicitly in the prompt
- Codex CAN use RepoQL tools if available—prompt it to do so
- Save `threadId` to continue conversations

---

## The Handoff

Your job as Claude: translate vague intent into explicit steps. This is where you add value.

| User says | You translate to |
|-----------|------------------|
| "Check for issues" | Steps: 1) Find X 2) Trace Y 3) Identify Z |
| "Fix the bug" | Goal + repro + expected + actual + logs |
| "Review the changes" | Scope + focus + constraints + output format |

The more explicit your translation, the better Codex performs. Vague prompts get reasonable results; step-by-step prompts get thorough, actionable results.

---

*Yin and yang. Claude shapes the question; Codex answers it. Save the threadId.*
