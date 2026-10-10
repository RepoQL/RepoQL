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
2. Nothing else for the hooks: they run under bash on macOS and Linux, and on Windows under Git Bash when it is installed or Windows PowerShell when it is not.

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

Auto-activating: **effective-repoql**, **effective-markdown**, **mermaid-diagrams**, **skill-builder**, **statusline-builder**, **monitoring-repoql**, **using-uplinks**, and **troubleshooting-repoql**.

### Agent

**dora-the-codebase-explorer** — a deep codebase-investigation agent that drives RepoQL in its own context.

### Hooks

- **SessionStart** — bootstraps the `rql` binary if it's missing (see Installation), then injects a deliberately small orientation: the mounted `github://` repos (directly usable), accessible uplink names, and a pointer to the `concept://` invariants. Repo structure and docs are large and re-derivable, so the agent pulls them on demand (`read` / `explore`) rather than paying for them every session.
- **PreToolUse (Write/Edit)** — surfaces the `concept://` invariants relevant to the file being edited, once per session, as extra context just before the write.
- **PostToolUse (reads)** — defines known terms and aliases from returned text after native `Read`/`read_file` and RepoQL MCP `read` calls. Each definition appears once per session in the serving host; a host restart resets that memory. Scope comes from the read target.
- **Host display (Claude Code terminal and desktop Code tab)** — shows the person what the RepoQL host is doing, without a tool call. A dim `rql` label sits in the prompt footer: `rql` when the index is settled, `rql 84%` while it is being built, `rql no host` when no host is running. A long operation such as an import gets one line above the prompt. A RepoQL call that has run for ten seconds names itself beside the spinner. It reads `rql monitor --jsonl --progress`, never starts a host, and changes nothing the agent reads. With an `rql` older than the release that carries the monitor's signal fields, only the plain `rql` label is drawn. Turn it off with the plugin's **Show RepoQL host state** option.

Vocabulary hints require a host and CLI with `rql vocabulary hints` support. Each read can add up to five complete definitions within 2,000 characters. The hook considers the first 65,536 Unicode characters of text, skips errors and image-only results, and does not parse shell reads such as `cat` or `sed`. An older CLI reports a diagnostic and the read continues.

## How to use it

### Explore before read

```
# Wrong: guess a path and read blindly
read("file:///src/**/*Auth*.cs", 5000)

# Right: explore finds, read fetches just the slice
explore(uriGlob="file:///src/**", keywords="authentication", question="where is the JWT signature verified?")
read("file:///src/Auth.cs#symbol=ValidateToken => content", 800)
```

### Query a shared uplink

Ask “Use our team uplink to find the service that owns authentication” or “Help set up an uplink on AWS.” The `using-uplinks` skill loads its full guidance from the installed `rql` version. That guidance covers discovering shared indexes, keeping remote reads on the chosen host, and setting up an uplink.

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
