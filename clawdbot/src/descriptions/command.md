<WHY>
Your remote control for the host — start, stop, configure, diagnose, and manage imports. Commands follow the same shape as the CLI: command words, then explicit `--option value` arguments, so anything you can do at the terminal you can do here.
</WHY>

<SYNTAX>
`command(command="<command> <subcommand> --param value")`

- Command paths use words: `config set`, `diagnostics memory`, `import remove`.
- Arguments are explicit options: `config set --key search.rerankEnabled --value true`.
- `command(command="help")` lists available commands.
- `command(command="<cmd> --help")` gives usage for one command.
</SYNTAX>

<COMMANDS>
- `help` — list available management commands
- `account whoami` — current cloud identity
- `account login` — browser flow by default; pass `--mode device-code` for device-code login
- `account logout` — clear the local session
- `config list` / `config read --key <key>` / `config set --key <key> --value <value>`
- `import add --uri github://owner/repo`, `import list`, `import remove --uri github://owner/repo`
- `diagnostics memory` — host memory, DuckDB, graph, and embedding footprint
- `host status` — MCP-local readiness, phase, and file counts
- `host start` / `host stop` / `host restart` — MCP-local gRPC host lifecycle
- `dashboard` — open the live host dashboard in the default browser
</COMMANDS>

