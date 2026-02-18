# Plan: Configuration Command

Implements: [Configuration Design — ::config Command](../designs/future/configuration.md#config-command)

## Scope

**Covers:**
- `ConfigCommand` implementing `::config` with all variants (list, read, set, reset)
- Atomic JSON file writes (write to `.tmp`, rename)
- In-memory reload after mutation
- Sensitive value masking in output
- Scope validation (sensitive keys blocked from repo scope)
- Value validation against property types
- `help://configuration` documentation
- Tests for all command variants, validation, write/reload cycle

**Does not cover:**
- `RepoQlConfig`, `SettingRegistry`, `ConfigurationLoader` (Plan: configuration-1-foundation)
- Consumer migration (Plan: configuration-3-migration)
- Contextual config surfacing in tool responses (future)
- Live reload notification system (future — most settings require restart anyway)

## Enables

- Agents can discover all settings via `::config`
- Agents can change settings without editing files or env vars
- Agents can see provenance — where each value comes from
- Config documentation is queryable via `help://configuration`

## Prerequisites

- Plan: configuration-1-foundation complete — `SettingRegistry`, `ResolvedConfig`, `ConfigurationLoader` must exist

## North Star

An agent types `::config` and immediately understands what's configurable, what each setting does, and where each value came from. Changing a setting is one command. Every error tells the agent exactly what went wrong and what to do instead.

## Done Criteria

### List All (`::config`)

- When invoked with no parameters, the command shall list every setting with: key, current value, source scope, and description
- The command shall mask sensitive values (show first 4 and last 2 characters, `****` in between)
- The command shall group settings by their section prefix

### Read One (`::config[key]`)

- When invoked with one parameter matching a key, the command shall show: value, source scope, default value, env var name, valid values (if specified), and whether restart is required
- When the key does not exist, the command shall return an error suggesting the closest match via Levenshtein distance

### Set (`::config[key, value]` and `::config[key, value, scope]`)

- When invoked with key and value, the command shall write to local scope by default
- When invoked with key, value, and scope, the command shall write to the named scope (`local`, `repo`, `user`)
- When the scope argument is not `local`, `repo`, or `user`, the command shall return an error listing valid scopes
- When the key is marked sensitive and scope is `repo`, the command shall refuse with an actionable error suggesting local, user, or env var
- When the value does not parse to the property's type, the command shall return an error showing valid values or expected type
- The command shall write the config file atomically (write `.tmp`, rename)
- The command shall reload `ResolvedConfig` in-memory after writing
- When the setting has `RequiresRestart = true`, the response shall note this and suggest `::host.restart`
- If the config file's parent directory does not exist, the command shall create it

### Reset (`::config[-key]` and `::config[-key, scope]`)

- When invoked with `-key`, the command shall remove the key from local scope
- When invoked with `-key` and scope, the command shall remove from the named scope
- When the key is not present in the target scope's file, the command shall succeed silently (idempotent)
- The command shall reload after removing

### File Operations

- When writing a config file, the command shall preserve existing keys in the file that are not being changed
- When the config file does not exist, the command shall create it with only the new key
- When writing fails (permissions, disk), the command shall return an error naming the file and the OS error

### Help Documentation

- A `help://configuration` page shall exist documenting all settings, their defaults, env var names, and scopes
- The page shall be generated from `SettingRegistry` so it cannot drift from the code

### Tests

- A test shall verify list output includes all registered settings
- A test shall verify sensitive values are masked
- A test shall verify set + read round-trip at each scope
- A test shall verify sensitive key rejection at repo scope
- A test shall verify invalid value rejection with error message
- A test shall verify reset removes the key and reloads
- A test shall verify atomic write (file exists and is valid after the operation, even if process is interrupted between write and rename — test the tmp file cleanup)
- A test shall verify closest-match suggestion for unknown keys

## Constraints

- **Command pattern unchanged** — `[CommandClass]` + `[Command("config")]`, constructor DI, returns `CommandResult`. No changes to the command framework.
- **Output is plain text** — `CommandResult` carries a string. Format for readability in a terminal/chat context. No structured data in the output.
- **No concurrent write protection beyond atomic rename** — last writer wins. Acceptable for config.

## References

- [Configuration Design](../designs/future/configuration.md) — command contract, output format
- [Commands North Star](../north-star/commands.md) — syntax, hierarchy, discoverability, recovery
- Existing pattern: `DiagnosticsCommand.cs`, `ReindexCommand.cs` in `CommandImplementations/` — command structure
- Existing pattern: `CommandParser.cs` — parameter parsing rules
- `help://` documentation: existing embedded docs in `RepoQL.Documentation/`

## Error Policy

Every error from `::config` must be actionable. Unknown key → suggest closest. Invalid value → show valid options. Wrong scope → list valid scopes. File write failure → name the file and the OS error. The agent should never need to look elsewhere to recover from a `::config` error.
