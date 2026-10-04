# RepoQL Uplink Plugin for Claude Code

Your organization's shared RepoQL indexes, from one remote connection. Nothing to install on this machine.

An uplink is a RepoQL index your organization hosts. This plugin connects Claude Code to the single endpoint that reaches every uplink your account can access, so Claude can search and read your team's repositories without cloning them.

## Which plugin

| You want | Install |
|---|---|
| Code intelligence for the repository you are working in, plus uplinks by name | [`repoql`](../repoql/README.md) |
| Only your organization's shared indexes, with no local binary — a cloud session, a locked-down machine, a repository you have not cloned | `repoql-uplink` |

Install one. The `repoql` plugin already reaches the same uplinks through its local tools.

## Installation

```
/plugin marketplace add RepoQL/RepoQL
/plugin install repoql-uplink@repoql-plugins
```

Then run `/mcp`, select the RepoQL server, and sign in through the browser with the RepoQL account that has access to your team's uplinks. At sign-in you choose an organization and either a default uplink or to let the assistant choose per task.

You need a RepoQL account with access to at least one uplink. An organization admin hosts one with `rql uplink`.

### A different endpoint

The plugin connects to `https://mcp.repoql.com/uplink`. If your RepoQL service gave you another shared URL, set it before starting Claude Code:

```bash
export REPOQL_UPLINK_URL=https://your-service.example/uplink
```

The URL must be a shared connector URL, whose path ends in `/uplink`. A URL for one specific uplink is pinned to that uplink and is not what this plugin expects.

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

The connection is read-only. Importing repositories, capturing concepts, and host administration need the [`repoql`](../repoql/README.md) plugin or the uplink's admin connection.

### Skills

Two are written for this connection. **using-uplinks** covers choosing a destination and routing each call. **troubleshooting-repoql** finds which layer a failure is in — sign-in, access, the uplink, its host, or its index — and who owns the fix.

The rest are the same skills the `repoql` plugin ships, loaded from the uplink at the version its host runs: **effective-repoql**, **research**, **effective-markdown**, **findings**, **north-star**, **flow**, **system-design**, **plan**, **odad**, **mermaid-diagrams**, and **skill-builder**.

### Agents

**dora-the-codebase-explorer** investigates a codebase in its own context. **researcher** gathers evidence on one direction for the research skill.

### No hooks, no binary

The plugin starts no process and downloads nothing. It runs wherever Claude Code can reach the endpoint.

## How to use it

Ask in terms of your team's code:

- "Show me the RepoQL uplinks I can access and help me choose one for this task."
- "Use the platform uplink to explain how authentication works."
- "Compare the retry policies in the platform and billing uplinks, and say which source supports each answer."

The index reflects pushed code. Your uncommitted edits are not in it.

## Troubleshooting

| What you see | What to do |
|---|---|
| The server needs authentication | `/mcp` → the RepoQL server → sign in. |
| The server fails to connect | Check the endpoint. Set `REPOQL_UPLINK_URL` if your service uses another shared URL. |
| No uplinks, or one is missing | Check the signed-in account and organization, then ask your RepoQL admin about access. |
| An uplink is offline | Ask its operator to restore it. |
| No default was selected | Name the uplink in your request, or sign in again to choose one. |

The `troubleshooting-repoql` skill carries the full table for the assistant.

## License

MIT
