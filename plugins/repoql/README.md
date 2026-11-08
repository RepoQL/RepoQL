# RepoQL Plugin for Claude Code

Queryable code intelligence — explore a codebase's structure without reading every file.

RepoQL indexes your repository into a graph database so Claude can feel the shape of a thousand files without opening one: headlines say what each file does, structure shows every signature, semantic search ranks by meaning, and the graph answers what-calls-what. Fewer tokens spent, faster answers, nothing missed.

## Installation

```
/plugin marketplace add RepoQL/RepoQL
/plugin install repoql@repoql-plugins
```

That's the whole install. The plugin starts its MCP server through a small launcher (`scripts/rql-mcp`) that looks for `rql` in its canonical install directory — `~/.local/bin` on macOS/Linux, `%LOCALAPPDATA%\rql` on Windows — so the server does not depend on the PATH Claude Code was launched with.

If the `rql` host binary isn't on your machine yet, the launcher installs it by running the standard hosted installer for your platform. The result is identical to a manual install: one canonical binary that `rql update` and every other agent harness share; the plugin never keeps a private copy. The binary is about 180 MB, and the download runs detached, so closing the session does not abort it.

The session start hook tells you what happened in one line — `RepoQL installed rql to ~/.local/bin.`, that the download is still running, or why the install could not run and the command to do it by hand — and says nothing when `rql` was already there. The install log is `~/.local/state/repoql/bootstrap.log`.

What the first run looks like (measured with Claude Code 2.1.284):

- **macOS/Linux, interactive session** — the `repoql` server connects at once, including in the session where you ran `/plugin install`, and its tools appear in that same session when the download finishes. In a new session a prompt you send meanwhile waits behind `running SessionStart hook` until then, for up to 200 seconds.
- **macOS/Linux, headless `claude -p`** — if the download takes longer than about 15 seconds, that run has no RepoQL tools; the next run does.
- **Windows** — the launcher finds `rql.exe` without it being on PATH, and with no `rql.exe` it waits for the download before starting the server. So the tools arrive in that first session only when the download finishes inside Claude Code's MCP startup timeout (`MCP_TIMEOUT`, 30 seconds by default); otherwise the first session needs `/mcp` → Reconnect on `repoql` once the download is done. Claude Code does not retry a server that failed to connect, and new sessions skip it for the next 15 minutes; reconnecting from `/mcp` works at once.

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

Auto-activating, for RepoQL itself: **effective-repoql**, **monitoring-repoql**, **troubleshooting-repoql**, **using-uplinks**, **writing-connectors**, **installing-connectors**, and **statusline-builder**.

For the work you use it on: **research**, **findings**, **effective-markdown**, **north-star**, **flow**, **system-design**, **plan**, **odad**, **mermaid-diagrams**, and **skill-builder**.

### Agents

- **dora-the-codebase-explorer** — a deep codebase-investigation agent that drives RepoQL in its own context.
- **researcher** — gathers evidence on one direction for the research skill and returns findings with a source beside each claim.

### Hooks

- **SessionStart** — waits for the `rql` install if one is under way (see Installation; it starts one in a session where the MCP server was not spawned), then injects a deliberately small orientation: the mounted `github://` repos (directly usable), accessible uplink names, and a pointer to the `concept://` invariants. Repo structure and docs are large and re-derivable, so the agent pulls them on demand (`read` / `explore`) rather than paying for them every session.
- **PreToolUse (Write/Edit)** — surfaces the `concept://` invariants relevant to the file being edited, once per session, as extra context just before the write.
- **PostToolUse (reads)** — defines known terms and aliases from returned text after native `Read`/`read_file` and RepoQL MCP `read` calls. Each definition appears once per session in the serving host; a host restart resets that memory. Scope comes from the read target.
- **PostToolUse (edits)** — records which git worktree the session edits, in the background, so an edit is never slowed or blocked.
- **PostToolUse (RepoQL calls)** — tells the agent when RepoQL answered from a different git worktree than the one it is editing, so it does not trust a stale tree.
- **Host display (Claude Code terminal and desktop Code tab)** — shows the person what the RepoQL host is doing, without a tool call. It reads `rql monitor --jsonl --progress`, never starts a host, and changes nothing the agent reads.
  - **Desktop:** a small `rql` badge sits above the prompt, with the dashboard's eye in it and a **Details** button beside it. Before the button sits a small eye for each uplink your account can reach, lit while that uplink answers and grey while it does not. Pointing at one shows its name and state. With more than five uplinks, one eye and a count replace them, and pointing at it shows how many are online. While an import or the first indexing of a repository runs, the badge becomes a ring that fills, with the work named beside it: `importing github://sharkdp/bat`. With no host running the eye goes grey and the badge reads `no host`.
  - **Status pane:** **Details** opens a pane that says whether RepoQL is ready, working or not running, names the work under way with its percentage, has a card for each uplink with whether it answers and the sources it holds (counted by scheme, such as `github:// × 35`, when there are more than eight); with more than five uplinks, those online come first and the rest share one short card, and has the button that opens the dashboard. It shows the host's release, and how many files are ready to search once the host has said so; a host that was already settled when the session connected has not.
  - **Terminal:** a dim label in the prompt footer names RepoQL and its state (`rql · ready`, `rql · working 21%`, `rql · not running`), and ends in an underlined `dashboard` link once the host has given one. The same work is named in one line above the prompt. The terminal has no status pane.
  - **Both:** a RepoQL call that has run for ten seconds names itself beside the spinner. The host's own housekeeping and other sessions' worktrees are never shown.
  - It needs `rql` 1.7.13 or later; an older one shows only the resting state. Turn it off with the plugin's **Show RepoQL host state** option.

Vocabulary hints require a host and CLI with `rql vocabulary hints` support. Each read can add up to five complete definitions within 2,000 characters. The hook considers the first 65,536 Unicode characters of text, skips errors and image-only results, and does not parse shell reads such as `cat` or `sed`. An older CLI reports a diagnostic and the read continues.

## What it runs, and what leaves your machine

### What the plugin runs

- **The MCP launcher** (`scripts/rql-mcp`, or `scripts/rql-mcp.cmd` on Windows) runs `rql mcp` from `~/.local/bin` or `%LOCALAPPDATA%\rql`. When `rql` is missing it runs `scripts/bootstrap-rql.sh` (or `.ps1`), unless `REPOQL_NO_BOOTSTRAP=1` is set.
- **The bootstrap** downloads the installer script from `https://downloads.repoql.ai/latest/install-rql.sh` (or `install-rql.ps1`) and runs it. The installer reads the current version from `https://downloads.repoql.ai/latest/version.txt` and downloads that version's `rql` binary from `https://downloads.repoql.ai/<version>/<platform>/`, over HTTPS. Its log is `~/.local/state/repoql/bootstrap.log`.
- **The hooks** are shell scripts (PowerShell on Windows without Git Bash) that call the local `rql` binary and nothing else. They make no network request.
- **The host display** reads the output of the local `rql monitor` command. It makes no network request.

### What leaves your machine

RepoQL indexes, parses and queries your code on your machine. What can leave it depends on whether you sign in to a RepoQL account.

| When | Destination | What is sent | How to stop it |
|---|---|---|---|
| Installing `rql` (once) | `downloads.repoql.ai` | Download requests for the installer and the binary | `REPOQL_NO_BOOTSTRAP=1`, then install by hand |
| Always, signed in or not | `downloads.repoql.ai` | A request for the latest release pointer. A newer release is downloaded and applied after its signature and SHA-256 are verified. | Set `update.check` or `update.apply` to `false` in your user configuration (see `help:///commands/update.md`) |
| Signed in | `api.repoql.ai` | The text RepoQL embeds, reranks or explains: summaries and chunks of the files it indexes, and your queries | Stay signed out. Embeddings are then computed locally, and reranking and cloud explain are off. |
| Signed in | `api.repoql.ai` | One usage record per tool call: the tool, its outcome, timings, its parameter values as you typed them, the `rql` version, OS, architecture and a random installation ID. No file content. | `DO_NOT_TRACK=1`, or set `telemetry.enabled` to `false`. Set `telemetry.params` to `false` to keep the names and outcomes but drop the values. |
| When you run `rql feedback` | `api.repoql.ai` | Your message and host diagnostics, including the workspace path | Don't run it |
| When you import or connect a source | The source you name, such as GitHub or an S3 bucket | Requests to read that source | Don't import it |
| When you use an uplink | `mcp.repoql.com` and `portal.repoql.ai` | The tool calls you route to that uplink | Don't name an uplink |

Your signed-in account is the consent boundary: a signed-out host sends no usage records and no code. The full field list is in RepoQL's embedded documentation at `help:///hosting/telemetry.md`. See also [Your data](https://repoql.com/docs/your-data/), the [privacy policy](https://repoql.com/privacy/) and the [subprocessors](https://repoql.com/subprocessors/).

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

MIT — see [LICENSE](LICENSE). Copyright Clanker Gear Limited.
