---
name: codex-review
description: Invoke Codex for code reviews via MCP or CLI. Use when reviewing diffs, commits, or PRs with GPT-5.2-codex.
tags: [code-review, codex, mcp, openai]
audience: { human: 20, agent: 80 }
purpose: { gestalt: 20, reference: 50, concepts: 20, high-agency-process: 10 }
zones: { knowledge: 45, process: 20, constraint: 15, wisdom: 20 }
---

# Codex Code Review

Codex (GPT-5.2-codex) provides a second perspective on code changes. Use for security reviews, regression checks, or when you need another set of eyes on complex changes.

## Quick Reference

| Need | Use | When |
|------|-----|------|
| Deep review with follow-up | MCP: `mcp__codex__codex` | Security, complex changes, need to trace call sites |
| Fast sanity check | CLI: `codex review --base main` | Pre-push, CI, batch reviews |
| Review uncommitted work | CLI: `codex review --uncommitted` | Before committing |
| Review specific commit | CLI: `codex review --commit SHA` | Post-merge review |
| Continue a review | MCP: `mcp__codex__codex-reply` | Follow up on findings |

---

## What Codex Does NOT Have Access To

| Context | Available? | Implication |
|---------|------------|-------------|
| Your Claude conversation | No | Must provide all context in prompt |
| Local files (MCP mode) | Yes, via tools | Can run git, read files, use RepoQL |
| Local files (CLI mode) | Yes, directly | Reads from working directory |
| Runtime behavior | No | Cannot assess production traffic, feature flags |
| External APIs | No | Cannot verify third-party contracts |

**Key insight**: Codex is isolated and lacks intuition for unstated intent. It sees only what you provide plus what it retrieves via tools. Unlike Claude, Codex won't fill in the blanks—state the steps you want, not just the outcome.

---

## MCP Invocation

### Starting a Review

```
mcp__codex__codex(prompt: """
Task: Code review
Goal: Verify auth changes don't introduce vulnerabilities
Scope: commit abc1234
Focus: security, input validation
Output: Findings by severity with file:line refs
""")
```

### Response Format

```json
{
  "threadId": "019c1846-80c3-74c1-9546-e235f4562034",
  "content": "**Findings**\n- High: SQL injection in UserService.cs:42..."
}
```

### Continuing a Review

```
mcp__codex__codex-reply(
  threadId: "019c1846-80c3-74c1-9546-e235f4562034",
  prompt: "What's the fix for the SQL injection issue?"
)
```

---

## CLI Invocation

```bash
# Review uncommitted changes
codex review --uncommitted "Focus on correctness and regressions"

# Review against base branch
codex review --base main "Security and backward compatibility"

# Review specific commit
codex review --commit abc123 "Auth and data consistency"

# With custom working directory
codex review --base main -C /path/to/repo "Review for bugs"
```

---

## Prompt Template

High-signal reviews require explicit scope and focus:

```
Task: Code review
Goal: <what should this change achieve?>
Scope: <commit SHA | branch diff | file list>
Focus: <correctness | regressions | security | perf | tests>
Constraints: <compatibility, frameworks, invariants>
Notes: <known risks, ignore patterns, non-obvious intent>
Output: Findings ordered by severity with file/line refs
```

### Capsule: CodexLacksIntuition

**Invariant**
Codex is highly capable but won't intuit what you wanted but didn't say.

**Example**
"Review this for issues" → Codex reviews what's in front of it, doesn't think to fetch the diff first.
"Get the diff for commit abc123, then review for security issues" → Codex fetches, then reviews.
//BOUNDARY: Not dumb—very capable. Just won't fill in the blanks about unstated intent.

**Depth**
- Claude asks "what did they probably mean?" and acts on inference
- Codex asks "what did they say?" and does that well
- State the steps you want, not just the outcome you want
- Codex excels at execution; you provide the strategy

---

## Using RepoQL During MCP Review

Codex can query RepoQL to understand context. Prompt it explicitly:

```
"Before reviewing, use mcp__repoql__explore to find what calls AuthService.
Then review commit abc123 for security issues."
```

RepoQL queries Codex can run:
- `mcp__repoql__query("SELECT * FROM edge WHERE type='CALLS' AND target LIKE '%AuthService%'")`
- `mcp__repoql__explore(intent: "Locate", keywords: "authentication")`
- `mcp__repoql__read(uri: "file:///src/Auth/TokenValidator.cs")`

---

## Codex Strengths and Limitations

| Good At | Bad At |
|---------|--------|
| Multi-file changes, refactors | Runtime behavior, traffic patterns |
| Security review (trained for it) | Environment-specific config |
| Finding missing tests, edge cases | Deep domain invariants (unless stated) |
| Tracing code paths | External APIs not in repo |
| Catching common bugs | Generated vs hand-written distinction |

---

## Common Mistakes

| Mistake | Problem | Fix |
|---------|---------|-----|
| "Review this code" | No scope, Codex guesses wrong | Specify commit/branch/files |
| Expecting inference | Codex won't intuit unstated steps | State steps, not just outcomes |
| Not providing goal | Codex doesn't know "correct" | State expected behavior |
| Large diff, no context | Hallucinated intent | Break up or provide summary |
| Expecting file reads | MCP Codex needs explicit instruction | Say "read file X first" |
| Ignoring threadId | Can't continue conversation | Save and reuse threadId |
| Missing constraints | Generic advice | State frameworks, compat requirements |

---

## Failure Modes

| Failure | Cause | Prevention |
|---------|-------|------------|
| Wrong findings | Reviewed wrong diff base | Specify exact commit/branch |
| Missed changes | Unstaged files excluded | Use `--uncommitted` or stage first |
| False positives | Generated code reviewed | Note "ignore src/Generated/" |
| Overconfidence | No test info provided | Include test results in prompt |
| Shallow analysis | Didn't use available tools | Prompt to use RepoQL/git |

---

## When NOT to Use This Skill

- **Simple typo fixes**: Overkill; just commit
- **Understanding code**: Use RepoQL explore instead
- **Writing code**: Use Codex exec, not review
- **CI/CD setup**: Review is read-only; use codex exec for changes

---

*State the steps, not just the outcome. Provide scope explicitly. Save the threadId.*
