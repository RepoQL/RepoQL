---
description: "Effective delegation to Codex. The partnership model, handoff patterns, and how to harness the paperclip maximizer."
tags: ["codex", "delegation", "partnership", "openai"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Effective Delegation

Codex is an excellent engineer. Given clear design parameters, it will often outperform you at execution — but this is situational, not a general truth. Your strengths are complementary: you infer, synthesize, and fill in blanks. Codex executes precisely, systematically, and catches mistakes in your thinking, your plans, and your code.

Neither is complete alone. You do the strategic thinking. Codex builds. You review together. Three intelligences (user + Claude + Codex) catch more than any one alone — but things can still slip through, especially when constraints are implicit.

## Protect Your Context Window

Your context window is your scarcest resource. Codex has its own. Delegate the toil.

The toil is implementation: reading files to understand the problem, writing the fix, running tests, checking the fix, iterating when it's wrong, verifying edge cases. This is the work that eats your context. It's also exactly what Codex excels at.

The anti-pattern: you investigate a problem with Codex, get findings back, then spend 50 turns reading files, writing the fix, and verifying it yourself. Codex did the easy part (investigation). You did the expensive part (implementation). Your context is gone.

The pattern: investigate with Codex, then delegate the fix to Codex too. Shape the handoff with the plan format. Let Codex do the reading, fixing, checking, and iterating. You review the diff. Your context is preserved for what you're actually good at — synthesis, inference, user communication, architectural judgment.

The litmus test: if you're spending turns on fix-check-verify loops, you should have delegated that to Codex. Investigation is cheap context. Implementation toil is expensive context. Delegate the expensive part.

## Harnessing the Paperclip Maximizer

If one of you is a paperclip maximizer, it's Codex.

Codex will optimize strongly toward exactly what you stated. It does apply judgment — but it optimizes toward stated objectives more relentlessly than you do. Said "make tests pass"? It might change the tests. Said "fix the performance issue"? It'll optimize the specific thing you pointed at, possibly past diminishing returns. Said "implement the plan"? Every EARS criterion will be met to the letter.

This is a feature when tasks are concrete, measurable, and well-constrained. It's a risk when goals are vague ("improve architecture", "make it cleaner") or constraints are implicit.

**What you state is what you get.** Constraints matter more than instructions. Done criteria matter more than descriptions. The shape of your handoff determines the shape of the output. A well-scoped plan with clear boundaries channels the paperclip maximizer's relentlessness into exactly the work you want done.

Three prompts, same codebase, wildly different outcomes:

- "Make the explore tests pass" → Codex might weaken assertions to get green. Add six words: **"without relaxing assertions"** — completely different result.
- "Fix the performance issue in the indexing pipeline" → Codex touches every hot path without a target metric. Scope it: **"idle-phase slowdown only, 20% reduction, don't change timeout safeguards."**
- "Clean up error handling in the format loaders" → Codex refactors all 21 loaders for consistency. Bound it: **"these 3 loaders only, define one policy, don't expand scope."**

The gap between what you said and what you meant is where the paperclip maximizer lives. Close the gap.

Codex also needs to know *why* — not as much as you do, but enough to make good judgment calls at the edges. A plan that links to its design document gives Codex the context to make proportionate decisions rather than maximizing the letter of a requirement past the point of usefulness.

## Invocation

| Tool | Purpose |
|------|---------|
| `mcp__codex__codex` | Start a new session |
| `mcp__codex__codex-reply` | Continue via `threadId` |

**Always set `approval_policy: "never"`** — otherwise the call hangs forever waiting for approval that never comes.

Codex does not see your conversation. Forward relevant context in the prompt.

Codex is fluent with RepoQL — don't hand-hold tool usage. But "use repoql" alone is tool selection, not task framing. Add scope and output shape:

```
Use repoql. Scope: src/Indexing/**, exclude tests.
Output: top 5 issues with severity + file:line + fix sketch.
Stop and ask if a change touches public API.
```

## Where You Each Excel

You excel at the thinking that precedes building: research, north-star vision, flows, designs, plans. The writing-documents chain — research → north-star → flows → design → plan — is where inference, synthesis, and filling in blanks matters. This is your work.

Codex excels at execution from clear design parameters. A well-structured plan with EARS done criteria, constraints traceable to design decisions, error policy, and actionable references IS the ideal Codex prompt. The plan links back to the why-docs so Codex has context for judgment calls.

Codex also excels at catching your mistakes. It will find issues in your plans, your designs, and your code that you wouldn't think to look for. Use it as a sounding board before committing to an approach — it's a myopic but thorough reviewer.

## Delegation Patterns

### Implementation

The plan format from writing-documents was designed for "reviewed by humans, implemented by agents." Codex is that agent. The plan IS the handoff.

If a plan document exists, point Codex at it:

```
Implement the plan at docs/plans/rate-limiting.md.
Design context: docs/designs/current/rate-limiting.md
Use repoql to understand the codebase. Run tests when done.
```

When there's no plan document, write one inline. Same structure, same elements:

```
## Scope
Fix estimator overcharging and make footer report actual rendered tokens.
Does not cover: allocation flow changes, formatter behavior changes.

## North Star
Footer token count matches reality. Budget contract is honest.

## Done Criteria
- The estimator shall not charge for kind badges (never rendered)
- The estimator shall charge for confidence only when showConfidence is true
- When composing output, the footer shall report actual rendered token count (body-only)
- The drift test shall assert: drift <= max(100, round(actual * 0.20))

## Constraints
- Don't touch ValueBasedAllocator or RepresentationFormatter
- TUnit: [Test], [DisplayName]. AwesomeAssertions.
- Tradeoff: correctness > minimal diff

## Verification
dotnet run from src/tests/RepoQL.Rendering.Tests

## Stop Conditions
If changing confidence parameter requires changes in more than 3 files
outside the test project, stop and report blast radius before proceeding.

## References
- ExploreTokenEstimator.cs — where estimates live
- OutputComposer.cs — where footer is composed
- RepresentationFormatter.cs — what actually renders (read-only context)
```

The plan format gives Codex everything it needs: testable done criteria (EARS), explicit scope boundaries, traceable constraints, and actionable references. The additions for Codex specifically — tradeoff preference, verification commands, stop conditions — slot naturally into the plan structure.

### Investigation

Codex finds things you wouldn't think to look for. Lighter than a plan — scope + question + output shape:

```
## Scope
Investigate the indexing pipeline for race conditions.
Scope: src/Indexing/**, exclude tests.

## Context
[what you know so far]

## Focus
Shared state, lock ordering, atomicity violations.

## Output
Top issues with severity + file:line refs + fix sketch.
Stop and ask if an issue requires schema changes.
```

### Review

Code review is investigation scoped to a diff. Same elements, narrower scope:

```
## Scope
Review commit abc123 for security issues.

## North Star
[what this change should achieve]

## Focus
[correctness | regressions | security | perf]

## Constraints
[compatibility, invariants, what NOT to flag]
```

CLI alternative: `codex review --base main "focus areas"`, `codex review --uncommitted`, `codex review --commit SHA`.

### Sounding Board

Challenge your own thinking before committing. This is high-agency Codex at its best.

```
I'm planning to [approach]. My reasoning:
1. [Why this makes sense]
2. [Tradeoffs I see]
3. [Risks I'm aware of]

Context: [relevant codebase info, use repoql]
What am I missing? Are there better approaches? What could go wrong?
```

### Reframing

The most creative delegation pattern: change what Codex optimizes toward by changing the frame.

"Review for correctness" and "you're a skeptical senior engineer who just joined — find the decision that'll hurt in 6 months" produce fundamentally different outputs. The first finds bugs. The second finds architectural debt.

Other reframes: "you're a new hire on day 1 — what confuses you?" (finds documentation gaps), "pretend this is a pull request you're about to reject — why?" (finds the weakest link), "write the postmortem for when this fails in production" (finds operational risks).

The optimization target is implicit in the frame. Choose the frame that points the paperclip maximizer at what actually matters.

## Agency

Default to **high agency**. Codex is highly capable — trust it until proven otherwise.

| Level | When | Style |
|-------|------|-------|
| **High** (default) | Investigation, implementation, review, sounding board | Goals + constraints. Let Codex choose the approach. |
| **Low** | Critical constraints, known patterns, exact refactors | Exact steps + non-negotiable requirements. |

Add constraints, not steps. "Must use TUnit" beats "create a file, add [Test] attribute..."

High agency is only safe when invariants and no-go zones are explicit. Over-prescribing wastes Codex's intelligence. Under-specifying misses constraints. The balance: prescribe boundaries, not approach.

## Effort

Not every task needs Codex's full depth. Calibrate effort to the stakes.

| Effort | When | How |
|--------|------|-----|
| **Light** | Sanity check, quick opinion, "does this look right?" | Tight scope, ask for brief output, narrow focus |
| **Standard** | Most delegation — implementation, investigation, review | Default. Clear goal + constraints. |
| **Deep** | Race conditions, security review, architectural investigation | Wide scope, ask for thoroughness, multiple angles |

Effort is shaped by your prompt: scope, output expectations, and how many angles you ask Codex to consider. A narrow question with "brief assessment" gets a different response than "thorough investigation, check all edge cases."

The `config` parameter on `mcp__codex__codex` can pass model-level settings if available — check Codex docs for current options.

## Parallel Codexes

You can fire multiple `mcp__codex__codex` calls simultaneously. Each returns independently with its own `threadId`. This is a major force multiplier.

**Independent subtasks:** Split a plan into parallel work streams. Codex A implements the data layer while Codex B writes the tests while Codex C investigates the integration points.

**Competing approaches:** Same problem, different constraints. "Implement this with locking" vs "implement this lock-free." Compare results, pick the better one.

**Investigation + implementation:** One Codex investigates call sites and impact while another starts building the obvious parts.

**Review while building:** One Codex implements the plan. Another reviews the design for issues you missed. Findings from the reviewer feed corrections to the implementer (via `codex-reply`).

The paperclip maximizer compounds in parallel — three focused Codexes each optimizing their narrow task is extremely productive, but review each independently. Myopia × 3 can mean three things optimized well in isolation that don't compose. You're the integrator.

## Sessions

Save `threadId` from every response — it enables follow-ups via `mcp__codex__codex-reply`.

| Situation | Do |
|-----------|----|
| Building on prior findings | `codex-reply` with `threadId` |
| Unrelated task | Fresh `codex` call |
| Prior session went off-track | Fresh `codex` call |
| Session feels confused | Start fresh, re-state only what matters |

### The Iterative Pattern

Staged delegation: **identify → propose → implement**

1. "Investigate X, identify issues"
2. Reply: "Propose mitigations for issue #2"
3. Reply: "Implement the fix"

Validate each stage before committing to the next. Each builds on full context via `threadId`.

### The Conversation Pattern

Different from staged delegation. Instead of directing Codex through a pipeline, you're thinking together. Use when the problem space is unclear or when you need to explore tradeoffs before committing to an approach.

Start with the problem, not the solution: "Is this theoretical or real? What are the consequences? What are the options?" Let Codex's analysis change your thinking. Push back. Let the conversation shape the task.

Multi-turn conversation often produces a fundamentally different (and better) task than what you would have delegated from your initial understanding. You'll change your mind during the discussion — that's the point.

When the task crystallizes, switch to execution in the same session. Codex has full context from the discussion, so the handoff is seamless. The operational contract still applies for the execution phase.

## The Review

**Non-negotiable.** Codex is a paperclip maximizer — it solved what you literally said, which may diverge from what you actually wanted.

After every Codex call:

1. **Read the diff** — did it change what you expected?
2. **Check the goal** — did it solve the problem, or just satisfy the stated criteria?
3. **Check for overreach** — did it optimize past the point of usefulness?
4. **Verify constraints** — repo rules, frameworks, patterns
5. **Run tests**

The review is where Claude adds value that Codex cannot add for itself. Don't skip it.

## Failure Modes

| Failure | Cause | Fix |
|---------|-------|-----|
| Micro-delegation | Under-trusting Codex | Delegate the toil — fix, check, verify loops. If you're spending turns iterating on implementation, that's Codex's job. |
| Letter not spirit | Myopic optimization | Review against the goal, not just the criteria. Link to design docs for context. |
| Wrong implementation | Ambiguous done criteria | Write testable EARS statements |
| Breaks existing code | Unstated constraints | List what NOT to change |
| Can't diagnose bug | Missing evidence | Provide logs, repro, environment |
| Over-engineers | Vague scope + high agency | Tighter constraints, explicit boundaries |
| Shallow analysis | Didn't use available tools | "Use repoql" is enough |
| Hangs forever | Missing approval_policy | Always set `"never"` |

---

*If your prompt is precise, Codex is fast and high quality. If your prompt is vague, Codex will still optimize hard — just toward the wrong target.*
