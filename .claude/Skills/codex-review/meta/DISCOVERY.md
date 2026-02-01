# Codex Code Review - Discovery Notes

## What is Codex?

OpenAI's Codex CLI is an agentic coding tool powered by GPT-5.2-codex. It can be invoked:
1. **Interactively** via `codex` command (TUI)
2. **Non-interactively** via `codex review` or `codex exec`
3. **As MCP server** via `codex mcp-server` (stdio transport)

When running as MCP server, Claude Code can invoke it via:
- `mcp__codex__codex` - start a conversation
- `mcp__codex__codex-reply` - continue with threadId

## Two Invocation Modes

### CLI Mode (`codex review`)
- Fast, non-interactive, scriptable
- Good for CI/automation, pre-push checks
- Supports: `--uncommitted`, `--base <branch>`, `--commit <SHA>`
- Less context, no back-and-forth
- Cannot ask clarifying questions

### MCP Mode (via Claude Code)
- Interactive, context-rich
- Can ask clarifying questions, follow up on specific files
- Can use RepoQL to trace call sites, understand patterns
- Slower, requires user presence
- Session maintained via threadId

## Codex Capabilities

**What it does well:**
- Review diffs, PRs, commit ranges
- Find bugs, regressions, security issues
- Trace code paths across files
- Check for missing tests, edge cases
- Use RepoQL for structural queries during review
- Order findings by severity with file/line references

**What it does poorly:**
- Runtime behavior, production traffic patterns
- Environment-specific behavior (feature flags, config, secrets)
- Deep domain-specific invariants unless spelled out
- Distinguishing generated vs hand-written code
- External APIs, data contracts not in repo
- Can hallucinate when requirements are vague

## Context That Matters

**Makes reviews better:**
- Clear goal and expected behavior
- Exact scope (diff, commit SHA, file list)
- Relevant constraints (perf, security, backward compat)
- Test results
- Known risk areas explicitly called out

**Makes reviews worse:**
- "Please review" with no scope
- Large refactors without intent description
- Missing requirements, no test info
- Mixed generated + handwritten code
- Requires knowledge of external services

## Failure Modes

1. **Wrong diff base** - reviewing against wrong branch/commit
2. **Unstaged changes** - not included in review scope
3. **Large diffs without guidance** - Codex infers and may be wrong
4. **Hidden coupling** - shared config, migrations not in diff
5. **Generated code** - reviewed as if human-authored
6. **Missing tests** - leads to overconfidence
7. **Ambiguous output** - general advice instead of actionable findings

## Optimal Prompt Structure

```
Task: Code review
Goal: <what should this change achieve?>
Scope: <git diff/commit SHA/file list>
Focus: <correctness | regressions | security | perf | tests>
Constraints: <compatibility, frameworks, invariants>
Tests: <what was run and results>
Notes: <known risks, non-obvious intent, ignore generated files>
Output: Findings ordered by severity with file/line refs
```

## RepoQL Integration

When Codex runs as MCP server, it has access to RepoQL tools:
- `mcp__repoql__explore` - find related code
- `mcp__repoql__query` - SQL queries on codebase
- `mcp__repoql__read` - fetch file content

**Recommended flow:**
1. Inventory - what changed, what's related
2. Locate - find call sites, tests, related patterns
3. Inspect - deep read of most relevant files

## Technical Details

- Model: `gpt-5.2-codex` (optimized for agentic coding)
- Context: auto-detected, configurable in `~/.codex/config.toml`
- MCP isolation: Codex does NOT see Claude conversation history
- Read-only vs write: `codex review` is read-only; use `codex exec` + `codex apply` for changes

## CLI Examples

```bash
# Review uncommitted changes
codex review --uncommitted "Focus on correctness and regressions"

# Review against base branch
codex review --base main "Security and backward compatibility"

# Review specific commit
codex review --commit abc123 "Auth and data consistency"
```

## MCP Examples

```
# Start review conversation
mcp__codex__codex(prompt: "Review changes in src/Auth/*.cs for security issues")

# Continue conversation
mcp__codex__codex-reply(threadId: "...", prompt: "What about the error handling?")
```
