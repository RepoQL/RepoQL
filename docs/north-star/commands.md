# Commands: What Great Looks Like

> Imperative actions through the query surface — administration and diagnostics without leaving SQL mode.

An agent is debugging a search that returns stale results. It doesn't switch tools, open a terminal, or call a different API. It types `::diagnostics` into the same query parameter it was just using for SQL — and gets a structured health report showing that three files failed to index. It types `::reindex[file:///src/Auth/**]` and the problem files re-enter the pipeline. Thirty seconds later, the original SQL query returns fresh results. The agent never left the query tool. It never learned a second interface. The same text box that accepts `SELECT * FROM Files` also accepts `::diagnostics` — and both feel equally natural.

---

## Syntax

- An agent should be able to distinguish a command from SQL at a glance — the `::` prefix is unambiguously not-SQL
- An agent should be able to invoke a command with no parameters by name alone — `::diagnostics`, not `::diagnostics[]`
- An agent should be able to pass parameters in brackets when needed — `::config[embedding_model]`
- An agent should be able to pass multiple parameters separated by commas — `::config[key, value]`
- An agent should be able to negate or remove with a `-` prefix on any parameter — `::config[-key]`
- An agent should be able to use the same URI patterns it uses in SQL — `::reindex[file:///src/**/*.cs]`

```
::command                        -- no parameters
::command[param]                 -- one parameter
::command[param1, param2]        -- multiple parameters
::command[-param]                -- negation/removal
::group.subcommand[param]        -- hierarchical naming via dots
```

---

## Hierarchy

- An agent should be able to organize related commands under a shared prefix — `::mcp.newrelic`, `::mcp.newrelic.auth`
- An agent should be able to type a prefix and see all commands underneath it — `::mcp` lists all `mcp.*` subcommands
- An agent should be able to navigate the hierarchy without knowing the full command name — start broad, narrow down
- An agent should be able to use dot-separated names that read naturally as qualified identifiers

```
::mcp
→ Available subcommands:
  ::mcp.newrelic       — NewRelic integration
  ::mcp.newrelic.auth  — Configure authentication

::mcp.newrelic[params]
→ dispatches to handler
```

---

## Self-Description

- An agent should be able to ask any command what it does and how to use it — `::diagnostics --help`
- An agent should be able to see parameter names, whether they're required or optional, and what each one means
- An agent should be able to ask a prefix group for help and get a list of its subcommands
- An agent should not need to leave the query tool to understand a command

```
::diagnostics --help
→ ::diagnostics — Run full system health diagnostics
  Usage: ::diagnostics

::diagnostics.fast --help
→ ::diagnostics.fast — Run quick system health checks
  Usage: ::diagnostics.fast

::mcp --help
→ Available subcommands:
  ::mcp.newrelic       — NewRelic integration
  ::mcp.newrelic.auth  — Configure authentication
```

---

## Discoverability

- An agent should encounter commands in context — when a query fails, when a setting could help, when something needs attention — not by reading a command list
- An agent should be able to act on a suggested command immediately — if a tool response says `try ::reindex[file:///src/Auth/**]`, that's copy-paste ready
- An agent should be able to find command reference documentation through `help://` when it needs depth — but `help://` is the reference, not the primary discovery path
- An agent should not need to know commands exist before encountering them — the tool surfaces the right command at the right moment

```
-- Query returns stale results
→ 3 files in scope failed indexing. Try: ::reindex[file:///src/Auth/**]

-- Semantic search unavailable
→ Embedding provider not configured. See current settings: ::config
  To set a provider: ::config[embedding_provider, ollama]

-- explore returns partial results
→ 12 of 45 files pending indexing. To check progress: ::diagnostics.fast
```

---

## Diagnostics

- An agent should be able to check system health with a single command
- An agent should be able to see what's healthy, what's degraded, and what's broken — not just pass/fail
- An agent should be able to run diagnostics at different depths — fast for a quick check, full for investigation
- An agent should be able to trust that diagnostics never modify state — they observe, never act

```
::diagnostics                    -- full health report
::diagnostics.fast               -- quick check, minimal overhead
```

---

## Configuration

- An agent should be able to see all configuration settings and their current values
- An agent should be able to read a single setting by name
- An agent should be able to change a setting and have it take effect immediately
- An agent should be able to reset a setting to its default without knowing what the default is
- An agent should be able to see which settings are defaults and which have been overridden
- An agent should be able to trust that invalid values are rejected with a clear explanation of what's valid

```
::config                         -- list all settings
::config[embedding_model]        -- read one setting
::config[embedding_model, value] -- set a value
::config[-embedding_model]       -- reset to default
```

---

## Indexing Control

- An agent should be able to trigger reindexing for specific files without reindexing everything
- An agent should be able to use glob patterns to target what gets reindexed
- An agent should be able to see indexing progress after triggering a reindex
- An agent should be able to force reindexing of files that already appear up-to-date
- An agent should be able to trust that reindexing doesn't disrupt concurrent queries

```
::reindex[file:///src/**/*.csproj]   -- reindex matching files
::reindex                            -- reindex everything
```

---

## Recovery

- An agent should be able to act on every error a command returns — errors are signposts, not dead ends
- An agent should be able to see what went wrong, what was expected, and what to try instead
- An agent should be able to fix a typo in a command name without re-reading documentation — the error suggests the closest valid command
- An agent should be able to fix a wrong parameter without guessing — the error shows the expected shape

```
::confg[key]
→ Unknown command 'confg'. Did you mean: ::config

::config[key, value, extra]
→ ::config accepts 1-2 parameters: ::config[key] or ::config[key, value]

::reindex[not-a-valid-pattern]
→ Invalid URI pattern. Expected glob like: ::reindex[file:///src/**/*.cs]
```

---

## Boundaries

- Commands are imperative, not composable — an agent should never be able to SELECT from a command or use one in a CTE
- Commands are few — every command earns its place by being unreachable through SQL alone
- Commands are stable — the set grows slowly; each addition is a commitment
- Commands never overlap with SQL — if it can be expressed as a query, it should be a query

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| An agent should be able to administer the system without leaving the query tool | No context switch, no second interface to learn |
| An agent should encounter commands in context, not by reading a list | The tool surfaces the right command at the moment it's relevant |
| An agent should be able to distinguish commands from SQL instantly | `::` prefix removes all ambiguity |
| An agent should be able to fix any command error from the error message alone | Every failure is a guide back to the path |
| An agent should be able to use familiar URI patterns in command parameters | One pattern language across the entire tool |
| An agent should be able to navigate command hierarchy by typing a prefix | Start broad, narrow down — no memorization required |
| An agent should be able to ask any command what it does with `--help` | The tool describes itself; no external docs needed for basic usage |
| Commands never duplicate what SQL can do | SQL is the primary surface; commands exist only for what SQL cannot express |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Add a command for something SQL can do | An agent should use SQL for queries; commands exist only for imperative actions |
| Return opaque errors from commands | An agent should be able to act on every error without re-reading documentation |
| Expect agents to discover commands by reading docs | Commands should be surfaced contextually when they're relevant |
| Make commands composable with SQL | An agent should understand that commands and queries are different modes |
| Grow the command set casually | An agent should be able to learn all commands quickly — fewer is better |
| Put unrelated commands in the same prefix group | Groups should reflect real relationships, not just reduce top-level count |
| Require `--help` to use a command | `--help` is a safety net; contextual surfacing and good errors are the primary path |

---

*Same text box. Different mode. The `::` prefix is a door to administration — and the agent already has the key.*
