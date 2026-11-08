# RepoQL Uplink Plugin for Claude

Your organization's shared RepoQL indexes, from one remote connection. Nothing to install on this machine.

An uplink is a RepoQL index your organization hosts. This plugin connects Claude to the single endpoint that reaches every uplink your account can access, so Claude can search and read your team's repositories without cloning them. It works in Claude Code, the Claude desktop app, and claude.ai.

## Which plugin

| You want | Install |
|---|---|
| Code intelligence for the repository you are working in, plus uplinks by name | [`repoql`](https://github.com/RepoQL/RepoQL/tree/main/plugins/repoql) |
| Only your organization's shared indexes, with no local binary — a cloud session, a locked-down machine, a repository you have not cloned | `repoql-uplink` |

Install one. The `repoql` plugin already reaches the same uplinks through its local tools.

## Installation

You need a RepoQL account with access to at least one uplink. An organization admin hosts one with `rql uplink`.

### Claude Code

```
/plugin marketplace add RepoQL/RepoQL
/plugin install repoql-uplink@repoql-plugins
```

Then run `/mcp`, select the `repoql-uplink` server, and sign in through the browser.

### The Claude desktop app and claude.ai

Install `repoql-uplink` from **Settings → Customize → Plugins**. On the plugin's **Connectors** tab, add the `repoql-uplink` connector. The setup dialog detects the right settings: keep **Sign in now** and **Use Claude's published identity**, then click **Add**. Click **Connect** and sign in through the browser.

On a Team or Enterprise plan, an Owner can add the connector once under **Organization settings → Connectors**. Members then only click **Connect**.

### At sign-in

Sign in with the RepoQL account that has access to your team's uplinks. You choose an organization, and either a default uplink or to let the assistant choose per task.

### A different endpoint

The plugin connects to `https://mcp.repoql.com/uplink`. The URL is written out in full, because the Claude desktop app and claude.ai take a connector URL literally and do not expand environment variables.

If your RepoQL service gave you another shared URL, add it as your own connector instead of installing this plugin. In Claude Code:

```bash
claude mcp add --transport http repoql-uplink https://your-service.example/uplink
```

In the desktop app or claude.ai, add it as a custom connector. The URL must be a shared connector URL, whose path ends in `/uplink`. A URL for one specific uplink is pinned to that uplink and is not what this plugin expects.

## What you get

### Tools (MCP)

| Tool | What it does |
|------|--------------|
| `uplinks` | The uplinks you can reach, which are online, and your sign-in default. |
| `explore` | The landscape, ranked by meaning — give it `uriGlob`, `keywords`, and a `question`. |
| `read` | Exactly the slice you need — `=> structure` for signatures, `#symbol=` / `#line=` for precision. |
| `query` | SQL over the graph, git, and parsed data. |
| `explain` | A synthesized, cited answer drawn from source. |
| `discover_vocabulary` | Your words, translated into the repository's real names. |

Each read tool takes an optional `uplink`. Omit it to use your sign-in default; pass a name to send that one call elsewhere.

The connection is read-only. Importing repositories, capturing concepts, and host administration need the [`repoql`](https://github.com/RepoQL/RepoQL/tree/main/plugins/repoql) plugin or the uplink's admin connection.

### Skills

Two are written for this connection. **using-uplinks** covers choosing a destination and routing each call. **troubleshooting-repoql** finds which layer a failure is in — sign-in, access, the uplink, its host, or its index — and who owns the fix.

The rest are the same skills the `repoql` plugin ships, loaded from the uplink at the version its host runs: **effective-repoql**, **research**, **effective-markdown**, **findings**, **north-star**, **flow**, **system-design**, **plan**, **odad**, **mermaid-diagrams**, and **skill-builder**.

### Agents

**dora-the-codebase-explorer** investigates your team's code through the uplinks in its own context, using only this connection's read tools. **researcher** gathers evidence on one direction for the research skill.

### No hooks, no binary

The plugin starts no process and downloads nothing. It runs wherever Claude can reach the endpoint.

## What leaves your machine

The plugin is one remote MCP connection to `https://mcp.repoql.com/uplink`. Nothing runs locally.

- **Sign-in:** the connection signs in through your browser with OAuth, using your RepoQL account. Claude stores the resulting token; the plugin holds no credential of its own.
- **Each tool call:** its parameters — globs, keywords, questions, SQL — go to `mcp.repoql.com`. The service relays the call to the uplink you choose, a RepoQL host that your organization runs, and returns that host's answer to Claude.
- **What the answers contain:** code and documents from the repositories that uplink indexes, limited to the uplinks your account can access.
RepoQL handles both under its privacy policy. See [Your data](https://repoql.com/docs/your-data/), the [privacy policy](https://repoql.com/privacy/) and the [subprocessors](https://repoql.com/subprocessors/).

## How to use it

Ask in terms of your team's code:

- "Show me the RepoQL uplinks I can access and help me choose one for this task."
- "Use the platform uplink to explain how authentication works."
- "Compare the retry policies in the platform and billing uplinks, and say which source supports each answer."

The index reflects pushed code. Your uncommitted edits are not in it.

## Troubleshooting

| What you see | What to do |
|---|---|
| The server needs authentication | Claude Code: `/mcp` → the `repoql-uplink` server → sign in. Desktop app or claude.ai: the `repoql-uplink` connector → **Connect**. |
| The server fails to connect | Check the endpoint. If your service uses another shared URL, add that URL as your own connector. |
| No uplinks, or one is missing | Check the signed-in account and organization, then ask your RepoQL admin about access. |
| An uplink is offline | Ask its operator to restore it. |
| No default was selected | Name the uplink in your request, or sign in again to choose one. |

The `troubleshooting-repoql` skill carries the full table for the assistant.

## License

MIT — see [LICENSE](LICENSE). Copyright Clanker Gear Limited.
