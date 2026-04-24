# RepoQL for Codex

Queryable code intelligence for OpenAI's Codex CLI.

## Prerequisite

The plugin drives the `rql` CLI — install it first:

```sh
curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash
```

(PowerShell: `irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex`)

Verify with `rql --version`, then register the MCP server:

```sh
rql install --agent codex
```

This writes `[mcp_servers.repoql]` into `~/.codex/config.toml`. The plugin below adds skills and an exploration agent on top of that.

## Install

```sh
codex plugin marketplace add RepoQL/RepoQL
codex plugin install repoql-codex
```

## What's inside

- **Skills** — `effective-repoql`, `troubleshooting-repoql`, `skill-builder`, `effective-markdown`, `mermaid-diagrams`. Auto-loaded by Codex when the `description` matches the current task.
- **Agent** — `dora-the-codebase-explorer` for deep, multi-step codebase investigation.
- **MCP server** — `rql mcp` registered as `repoql`, exposing `read`, `explore`, `query`, `explain`, `command`, `import`.

## Orientation

There's no session-start hook in Codex. To get repo orientation into context at the top of a session, add this to your project's `AGENTS.md`:

```
This project uses RepoQL. At the start of any non-trivial task, call
`mcp__repoql__read` with `file:///** => tree: folders` to orient yourself
on repo layout, and `help://** => tree: headlines` for available docs.
```

The `effective-repoql` skill covers everything else.
