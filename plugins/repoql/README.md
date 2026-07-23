# RepoQL Plugin for Claude Code

Queryable code intelligence — explore a codebase's structure without reading every file.

RepoQL indexes your repository into a graph database so Claude can feel the shape of a thousand files without opening one: headlines say what each file does, structure shows every signature, semantic search ranks by meaning, and the graph answers what-calls-what. Fewer tokens spent, faster answers, nothing missed.

## Installation

```
/plugin marketplace add RepoQL/RepoQL
/plugin install repoql@repoql-plugins
```

That's the whole install. If the `rql` host binary isn't already on your machine, the plugin downloads it on your next session start by running the standard hosted installer for your platform — into `~/.local/bin` on macOS/Linux, or `%LOCALAPPDATA%\rql` on Windows. The result is identical to a manual install: one canonical binary that `rql update` and every other agent harness share; the plugin never keeps a private copy. On macOS/Linux the tools work in that same session; on Windows the installer's PATH change reaches newly started terminals, so the tools appear from your next session.

Set `REPOQL_NO_BOOTSTRAP=1` to disable the auto-download and install manually instead:

```bash
curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash      # macOS / Linux
```
```powershell
irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex             # Windows
```

There is no separate index step — the host indexes a repository automatically the first time it runs there, and watches for changes after.

## Prerequisites

1. **Claude Code 2.0+**
2. **Bash** for the shell hooks and the bootstrap (Git Bash on Windows).

## What you get

### Tools (MCP)

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

### Skills

Auto-activating: **effective-repoql**, **effective-markdown**, **mermaid-diagrams**, **skill-builder**, **statusline-builder**, **monitoring-repoql**, and **troubleshooting-repoql**.

### Agent

**dora-the-codebase-explorer** — a deep codebase-investigation agent that drives RepoQL in its own context.

### Hooks

- **SessionStart** — bootstraps the `rql` binary if it's missing (see Installation), then injects a deliberately small orientation: the mounted `github://` repos (directly usable) and a pointer to the `concept://` invariants. Repo structure and docs are large and re-derivable, so the agent pulls them on demand (`read` / `explore`) rather than paying for them every session.
- **PreToolUse (Write/Edit)** — surfaces the `concept://` invariants relevant to the file being edited, once per session, as extra context just before the write.

## How to use it

### Explore before read

```
# Wrong: guess a path and read blindly
read("file:///src/**/*Auth*.cs", 5000)

# Right: explore finds, read fetches just the slice
explore(uriGlob="file:///src/**", keywords="authentication", question="where is the JWT signature verified?")
read("file:///src/Auth.cs#symbol=ValidateToken => content", 800)
```

### SQL for computation

```sql
-- File-type distribution
SELECT mime, COUNT(*) AS files FROM Files GROUP BY mime ORDER BY files DESC;

-- Largest files by token count
SELECT name, token_count FROM Files ORDER BY token_count DESC LIMIT 10;
```

Views: `Files`, `Functions`, `Types`, `Filesystems`, `Annotations`. Underlying tables: `node`, `artifact`, `edge`, `embeddings`.

## Troubleshooting

- **Check status** — `rql diagnostics`
- **Embedded docs are queryable** — RepoQL ships its own documentation under `help://`:
  ```
  explore(uriGlob="help://**", keywords="your topic", question="how do I ...?")
  ```
- **Deeper help** — the `troubleshooting-repoql` skill walks through host and index problems.

## License

MIT
