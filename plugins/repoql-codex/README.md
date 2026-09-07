---
description: RepoQL's Codex plugin, including installation, bundled capabilities, and platform-specific behavior.
tags: [repoql, codex, plugin, mcp, code-intelligence]
---

# RepoQL for Codex

RepoQL gives ChatGPT and Codex a pre-built structural index of a codebase. The plugin bundles the MCP server, task-specific skills, research agents, automatic setup, and repository context hooks in one install.

## Install

```sh
codex plugin marketplace add RepoQL/RepoQL
codex plugin add repoql-codex@repoql
```

Review and trust the plugin hooks when Codex asks. The startup hook installs `rql` from `downloads.repoql.ai` when it is missing, then the bundled MCP configuration starts `rql mcp`. Set `REPOQL_NO_BOOTSTRAP=1` to disable automatic installation.

To install `rql` manually:

```sh
# macOS or Linux
curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash

# Windows PowerShell
irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex
```

Start a new Codex task after installation or an update so the refreshed plugin and PATH are loaded.

## What is included

### Code intelligence

The `repoql` MCP server exposes 12 tools:

| Purpose | Tools |
|---|---|
| Find and understand | `explore`, `read`, `query`, `explain`, `keywords` |
| Manage and compose | `import`, `command`, `execute` |
| Observe | `watch` |
| Preserve repository knowledge | `capture_concept`, `capture_term`, `list_vocabulary` |

RepoQL addresses the workspace with `file:///`, imported repositories with `github://`, its own documentation with `help:///`, and durable repository knowledge with `concept:///` and `vocabulary:///`.

### Skills

- RepoQL operation: `effective-repoql`, `monitoring-repoql`, `troubleshooting-repoql`
- Evidence and design: `research`, `findings`, `north-star`, `flow`, `system-design`, `plan`, `odad`
- Authoring: `effective-markdown`, `mermaid-diagrams`, `skill-builder`

Codex loads a skill when its description matches the task. You can also name one directly.

### Agents

- `dora-the-codebase-explorer` maps unfamiliar codebases with RepoQL.
- `researcher` gathers evidence for one research direction and returns sourced findings without advocating a conclusion.

The `research` skill can ask Codex to delegate independent directions to researcher subagents when the user requests parallel research.

### Hooks

- `SessionStart` bootstraps `rql`, reports imported repositories, and injects `.repoql/concepts/readme.md` (or `README.md`) when present, including after context compaction.
- `PreToolUse` loads concepts relevant to files touched by `apply_patch` immediately before a change.
- **PostToolUse (reads)** — defines known terms and aliases from returned text after native `Read`/`read_file` and RepoQL MCP `read` calls. Each definition appears once per session in the serving host; a host restart resets that memory. Scope comes from the read target.

All hooks fail open: RepoQL being unavailable never blocks a task, read, or edit.

Vocabulary hints require a host and CLI with `rql vocabulary hints` support. Each read can add up to five complete definitions within 2,000 characters. The hook considers the first 65,536 Unicode characters of text, skips errors and image-only results, and does not parse shell reads such as `cat` or `sed`. An older CLI reports a diagnostic and the read continues.

## Use it

Ask Codex naturally:

- “Give me a one-screen orientation to this codebase.”
- “Find everything that handles authentication.”
- “Show me the call graph around this function.”
- “Import the upstream SDK and compare its implementation.”
- “Wait until semantic search is ready.”

The `effective-repoql` skill teaches Codex to explore broadly, resolve the repository's real vocabulary, and read only the slices needed for the answer.

## Platform-specific difference

The Claude Code package includes a status-line builder. Codex does not expose a custom status-line API, so that skill is intentionally not shipped here. All portable RepoQL capabilities are included, and the lifecycle hooks use Codex's native hook events and structured context format.

## License

MIT
