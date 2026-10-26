# RepoQL for OpenCode

The RepoQL plugin for OpenCode 2 registers RepoQL's MCP server and its skills from inside OpenCode. Nothing is written to `opencode.json` beyond the plugin entry.

## Install

`rql install` adds it when it finds OpenCode 2 or later:

```sh
rql install --agent opencode --extension
```

Or add it yourself:

```sh
opencode plugin add github:RepoQL/RepoQL
```

The plugin starts `rql mcp`. It looks for `rql` on `PATH`, then in `~/.local/bin`, then in `%LOCALAPPDATA%\rql`, where the [installers](../../README.md#install) put it. If it finds none, it registers no server and logs where to get `rql`.

OpenChamber runs OpenCode, so the plugin works there too.

## Update and remove

```sh
opencode plugin update github:RepoQL/RepoQL
opencode plugin remove github:RepoQL/RepoQL
```

## What it contains

- `server.js` — the plugin. It uses OpenCode's Promise plugin API and imports nothing outside Node's standard library.
- `skills/` — the same skills as the [Cursor plugin](../repoql-cursor/README.md).

OpenCode 1 cannot load it: its plugin API is different, and it has no `opencode plugin` command. On OpenCode 1, `rql install --agent opencode` registers the MCP server in `opencode.json` instead.
