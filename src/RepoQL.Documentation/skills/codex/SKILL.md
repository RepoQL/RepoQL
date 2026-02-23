---
description: "Delegate complex tasks to Codex (GPT-5.2-codex). Use for ticket completion, debugging, test writing, race condition analysis, refactoring."
tags: ["skill", "codex", "delegation", "tasks", "debugging", "testing", "openai"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Codex

Codex (GPT-5.2-codex) excels at complex, well-defined tasks: implementing tickets, debugging with evidence, writing tests, finding race conditions, refactoring. Delegate when the task is clear and you want execution, not exploration.

## Preflight / Postflight

**Before delegating:**
- Goal clear? (outcome, not just task description)
- Agency level? (high unless you know better—see AgencyLevel capsule)
- Constraints stated? (what NOT to do, repo rules, frameworks)
- Context forwarded? (Codex doesn't see your conversation)
- Fresh or continue? (reuse threadId if building on prior work)

**After Codex returns:**
- Read the diff—did it do what you expected?
- Check the logic—any subtle bugs or missed constraints?
- Run tests—did anything break?
- Synthesize for user—what did we learn?

*Three intelligences (user + Claude + Codex) catch what any one would miss. Don't skip the review.*

## Quick Reference

| Task Type | Use | Prompt Style |
|-----------|-----|--------------|
| Implement a ticket | `mcp__codex__codex` or `codex exec` | Goal + acceptance + constraints + scope |
| Debug with evidence | `mcp__codex__codex` | Repro + expected/actual + logs + environment |
| Find race conditions | `mcp__codex__codex` | Timing + thread dumps + suspected code |
| Write tests | `mcp__codex__codex` or `codex exec` | What to test + coverage goals + fixtures |
| Explore/survey | `mcp__codex__codex` | Focus areas + request options with tradeoffs |
| Sounding board | `mcp__codex__codex` | Your reasoning + "what am I missing?" |
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
Claude and Codex are complementary partners. Claude translates vague intent into clear goals; Codex executes systematically and surfaces what you wouldn't think to look for.

**Example**
User: "Check the indexing pipeline for issues"
Claude translates → "Investigate the indexing pipeline for race conditions. Here's the context: [relevant info]. Find issues and propose fixes."
Codex executes → finds 3 race conditions, proposes mitigations with file:line refs
Claude reviews → verifies the logic, runs tests, synthesizes for user
//BOUNDARY: Neither complete alone. The handoff and the review are where Claude adds value.

**Depth**
- Claude: inference, intent, synthesis. Asks "what did they probably mean?"
- Codex: execution, precision, depth. Asks "what did they say?"
- Together: vague intent → clear goals → systematic execution → reviewed synthesis
- Codex finds issues you wouldn't think to ask about—delegate investigation tasks
- Respect Codex's intelligence: give outcomes, not just steps

---

## Capsule: AgencyLevel

**Invariant**
Match your prompting style to the task. High agency for exploration and judgment; low agency for precise execution.

**Example**
High agency: "Find race conditions in the indexing pipeline. Propose fixes with tradeoffs."
Low agency: "Add a TryMarkEpochComplete() method to IndexItem using Interlocked.Exchange."
//BOUNDARY: Over-prescribing wastes Codex's intelligence. Under-specifying misses constraints.

**Depth**
- **High agency** (prescribe outcomes): Investigation, debugging, architecture review, "find and fix"
  - Give: goal, context, constraints, success criteria
  - Let Codex: choose approach, explore, use judgment
- **Low agency** (prescribe steps): Specific refactors, known patterns, critical constraints
  - Give: exact steps, specific code patterns, non-negotiable requirements
  - Use when: you know better, constraints are non-obvious, approach matters
- Default to high agency. Codex is highly capable—trust it until proven otherwise.
- Add constraints, not steps: "must use TUnit" beats "create a file, add [Test] attribute..."

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

### Sounding Board

Use Codex to challenge your thinking before committing to an approach.

```
I'm planning to [approach]. My reasoning:
1. [Why this makes sense]
2. [Tradeoffs I see]
3. [Risks I'm aware of]

Context: [Relevant codebase info]

Questions:
- What am I missing?
- Are there better approaches I haven't considered?
- What could go wrong with this plan?
```

Good for:
- Validating architectural decisions before implementation
- Catching blind spots in your reasoning
- Getting a second opinion on tradeoffs
- Stress-testing an approach before proposing to the user

This is high-agency Codex use—you're asking for judgment, not execution.

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

### Session Reuse vs Fresh Start

| Situation | Do |
|-----------|-----|
| Continuing same investigation | `codex-reply` with threadId |
| Follow-up question on findings | `codex-reply` |
| Building on previous work | `codex-reply` |
| Unrelated new task | Fresh `codex` call |
| Previous session went off-track | Fresh `codex` call |
| Want fresh perspective | Fresh `codex` call |
| Context would confuse new task | Fresh `codex` call |

**Reuse sessions** when context helps. Codex remembers what it found, what you discussed, what constraints you stated.

**Start fresh** when context hurts. Stale assumptions, wrong direction, or unrelated work. A new session has no baggage.

Long sessions accumulate context—useful for depth, but can become unwieldy. If a session feels confused, start fresh and re-state only what matters.

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

## Codex Tendencies

**Risk-averse by default**: Codex errs on the side of caution. It won't run tests after writing them unless you ask. It won't make changes it's uncertain about. This is often good, but you need to be explicit:
- "After implementing, run `dotnet test` to verify"
- "Apply the changes directly"
- "Delete the old implementation"

**Tool use**: Codex can use tools (git, RepoQL, file operations) but is not as fluent as Claude. Particularly weak at web search. If you need current documentation or external research, do it yourself and forward the results to Codex.

**Strengths**: Deep code analysis, systematic investigation, precise implementation, finding issues you wouldn't think to look for.

---

## When NOT to Use Codex

- **Exploratory thinking** - Use Claude for "help me understand"
- **Ambiguous requirements** - Clarify first, then delegate
- **Need inference** - Claude fills in blanks; Codex doesn't
- **Simple questions** - Overkill; just ask Claude
- **Web research** - Claude is better at search and synthesis
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

## The Review

**You must review Codex's output immediately.** This is not optional.

Codex is highly capable but can:
- Misunderstand constraints you thought were obvious
- Introduce subtle bugs in edge cases
- Miss context that wasn't in the prompt
- Over-engineer or under-engineer solutions

After every Codex call:
1. **Read the diff** - `git diff` the changed files
2. **Check the logic** - Does it actually solve the problem?
3. **Verify constraints** - Did it respect repo rules, frameworks, patterns?
4. **Run tests** - Confirm nothing broke

Three intelligences working together (user + Claude + Codex) catch what any one would miss. Don't break the chain by skipping review.

---

*Translate. Delegate. Review. Three intelligences, nothing gets past.*
