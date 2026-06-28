# MCP bridge (tool calls failing, tools missing)

RepoQL can bridge *other* MCP servers so their tools are callable — directly and as generated `mcp_*` SQL macros. When "a tool errors" or "a tool I expected isn't there", this layer tells you why and what to do. It is part of the **Services** layer: the host is fine, a downstream server is not.

## Start with server health

```sql
SELECT name, transport, state, auth_status, auth_action, tool_count, last_error
FROM mcp_servers();
```

Check this **first** — it always has a row per server, even when nothing has errored yet. `state` and `auth_status` tell you whether the server is live and authenticated; `auth_action` names the fix when auth is the problem; `tool_count = 0` means no tools are callable (so every call fails); `last_error` is the most recent failure. The most common "my MCP tool stopped working" cause — an **expired or inherited credential** — surfaces here as `state = needsAuth` / `auth_status = …Expired`, with the exact recovery spelled out in `auth_action`.

If a server you expected is missing entirely, look at every discovered candidate — shadowed, disabled, conflicting, invalid:

```sql
SELECT * FROM mcp_server_sources();
```

## Per-call error detail

```sql
SELECT error_category, server, tool, retryable, next_action, redacted_message, correlation_id
FROM mcp_bridge_errors();
```

Each row carries a **`retryable`** flag and a **`next_action`** recovery hint (message redacted) — read `next_action` first. **Caveat:** this table only has rows when a call actually *errored*. A server that never connected, or whose credential expired, can leave it **empty** while calls still fail — in that case `mcp_servers()` above is your signal, not this table.

## Is a specific tool callable?

```sql
SELECT * FROM mcp_tools();        -- discovered tools, generated macro name, callability gate
SELECT * FROM mcp_tool_params();  -- params mapped to safe SQL argument names
```

Calls are **read-only by default**; writes require an explicit allowlist. A tool that exists but won't call is usually blocked by that gate, not broken — `mcp_tools()` shows per-tool callability and the reason.

## Recovery (from the terminal)

MCP-bridge recovery is **not** in the in-session `command()` tool — it's the `rql` CLI. Run it yourself with `!`:

```
! rql mcp list             # inspect the bridge
! rql mcp reload           # re-read config and rediscover servers
! rql mcp retry            # re-attempt failed connections
! rql mcp auth <server>    # complete remote auth for a server
! rql mcp enable|disable|allow|disallow <…>
```

Match the command to `next_action` from `mcp_bridge_errors()` / `auth_action` from `mcp_servers()`.

## Going deeper

The host serves a full operational guide and glossary:
`help:///operations/mcp-bridge.md`, `help:///mcp-bridge/auth-recovery.md`, `…/authority.md`, `…/commands.md`, `…/sql.md`, `…/glossary.md`, and the macro reference `help:///schema/functions/table/mcp-servers.md` (and siblings).

## Boundary

This is for *bridged third-party* MCP servers. If **RepoQL's own** tools (`query`, `read`, `explore`, `command`) error, that's the host/connection layer → `host-and-connection.md`, or a dead host → `host-wont-respond.md`.
