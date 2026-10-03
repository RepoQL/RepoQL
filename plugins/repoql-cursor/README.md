# RepoQL Plugin for Cursor

Queryable code intelligence — explore a codebase's structure without reading every file.

RepoQL indexes your repository into a graph database so Cursor's agent can feel the shape of a thousand files without opening one: headlines say what each file does, structure shows every signature, semantic search ranks by meaning, and the graph answers what-calls-what. Cursor's built-in search is grep; RepoQL adds the index.

## Installation

By downloading and using RepoQL, you agree to the [Customer Terms and Software Licence](https://repoql.com/terms/2026-09-11/).

In the Cursor agent CLI:

```
agent plugin marketplace add https://github.com/RepoQL/RepoQL
```

Then install **repoql-cursor** from `/plugin`. On Teams and Enterprise, an admin can add `RepoQL/RepoQL` as a team marketplace (Dashboard → Plugins & MCPs → Add Marketplace) and install it for everyone.

To try it from a local checkout, copy (don't symlink) `plugins/repoql-cursor` to `~/.cursor/plugins/local/repoql-cursor` and reload the window, or pass `--plugin-dir plugins/repoql-cursor` to the `agent` CLI.

If the `rql` host binary isn't on your machine, the plugin downloads it at your first session start with the standard hosted installer — into `~/.local/bin` on macOS/Linux or `%LOCALAPPDATA%\rql` on Windows. Cursor starts MCP servers when the window opens, before that download, so run **Developer: Reload Window** once afterwards. Set `REPOQL_NO_BOOTSTRAP=1` to disable the download and install manually:

```bash
curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash      # macOS / Linux
```
```powershell
irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex             # Windows
```

There is no separate index step — the host indexes a repository the first time it runs there, and watches for changes after.

## Prerequisites

1. **Cursor** with plugin support, on a plan that includes MCP, skills, and hooks.
2. **Bash** and **jq** for the hooks (Git Bash on Windows).

## What you get

### Tools (MCP)

Each Cursor window gets its own RepoQL server, bound to that window's folder. The plugin sets `REPOQL_CWD` to `${workspaceFolder}`, which Cursor expands per window. Cursor's MCP roots can't do this job: they list every open window's folder to every server.

| Tool | What it does |
|------|--------------|
| `explore` | The landscape, ranked by meaning — give it `uriGlob`, `keywords`, and a `question`. Start here. |
| `read` | Exactly the slice you need — `=> structure` for signatures, `=> tree: headlines` for an overview, `#symbol=` / `#line=` for precision. |
| `query` | SQL over the graph, git, and parsed data. |
| `explain` | A synthesized, cited answer drawn from source. |
| `import` / `unimport` | Pull external repos into the graph (`github://owner/repo`). |
| `execute` | JavaScript in a sandboxed WASM environment. |
| `command` | Diagnostics, auth, config. |
| `capture_concept` | Write an invariant into the repository's permanent memory. |

### Rule

**repoql** (always applied) — the explore-then-read workflow and URI grammar, so the agent reaches for the index before grep.

### Skills

**effective-repoql**, **effective-markdown**, **mermaid-diagrams**, **skill-builder**, **monitoring-repoql**, **using-uplinks**, **troubleshooting-repoql**, and the document skills (**research**, **findings**, **north-star**, **flow**, **system-design**, **plan**, **odad**).

### Subagents

**dora-the-codebase-explorer** drives RepoQL in its own context for deep investigation; **researcher** runs one direction of an evidence-first research fan-out. Both inherit your selected model.

### Hooks

- **sessionStart** — bootstraps `rql` if it's missing, exports a `PATH` that finds it to later hooks, and injects a small orientation: imported `github://` repos, accessible uplinks, and a pointer to `concept://`. It adds the repository's concepts index too, unless the host's generated `.cursor/rules/repoql-concepts.g.mdc` rule already carries it.
- **preToolUse (Write)** — surfaces the `concept://` invariants relevant to the file about to be written, once per session. It never blocks or alters the write.

## Differences from the Claude Code plugin

Cursor's hook runtime can't yet carry everything the Claude Code plugin does:

- **Session orientation is best-effort.** Cursor can drop `sessionStart` context on a conversation's first message. What must always arrive lives in the rule instead.
- **No vocabulary hints after reads, and no worktree notices.** Cursor accepts `postToolUse` context but does not deliver it to the model, and its hook payloads name MCP tools without their server. These hooks will follow once Cursor delivers post-tool context.
- **No statusline-builder skill** — Cursor has no status line.

If you also use RepoQL's Claude Code plugin, Cursor may import it too ("Include Third-Party Plugins, Skills, and Other Configs", on by default), and RepoQL's tools and skills can then appear twice. Install one of the two in Cursor, or turn that setting off.
