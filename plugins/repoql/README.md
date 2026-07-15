# RepoQL Plugin for Claude Code

Queryable code intelligence — explore a codebase's structure without reading every file.

RepoQL indexes your repository into a graph database so Claude can feel the shape of a thousand files without opening one: headlines say what each file does, structure shows every signature, semantic search ranks by meaning, and the graph answers what-calls-what. Fewer tokens spent, faster answers, nothing missed.

## Prerequisites

1. **`rql` on your `PATH`** — the RepoQL binary:
   ```bash
   curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash      # macOS / Linux
   ```
   ```powershell
   irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex             # Windows
   ```
2. **Claude Code 2.0+**
3. **Bash** for the shell hooks (Git Bash on Windows).

There is no separate index step — the host indexes a repository automatically the first time it runs there, and watches for changes after.

## Installation

```
/plugin marketplace add RepoQL/RepoQL
/plugin install repoql@repoql-plugins
```

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

- **SessionStart** — injects a deliberately small orientation: the mounted `github://` repos (directly usable) and a pointer to the `concept://` invariants. Repo structure and docs are large and re-derivable, so the agent pulls them on demand (`read` / `explore`) rather than paying for them every session.

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
