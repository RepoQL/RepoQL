# Plan: Configuration Foundation

Implements: [Configuration Design — RepoQlConfig, Setting Registry, ConfigurationLoader](../designs/future/configuration.md)

## Scope

**Covers:**
- `RepoQlConfig` class with nested setting types in `RepoQL.Contracts`
- `SettingAttribute` in `RepoQL.Contracts`
- `SettingRegistry` that reflects over `RepoQlConfig`
- `ConfigScope` enum and `ResolvedSetting` record
- `ConfigurationLoader` — reads defaults, JSON files at three scopes, env vars; merges with precedence; validates; tracks provenance
- `ResolvedConfig` wrapper — holds resolved config + provenance map
- DI registration via `AddResolvedConfig()`
- Tests for registry building, loader merging, precedence, validation, env var mapping, malformed file handling

**Does not cover:**
- `::config` command (Plan: configuration-2-command)
- Consumer migration away from `Environment.GetEnvironmentVariable()` (Plan: configuration-3-migration)
- Contextual config surfacing in tool responses (future)
- `help://configuration` documentation (ships with command plan)

## Enables

- `::config` command can be built on top of `SettingRegistry` + `ResolvedConfig`
- Consumer migration can begin — components can start taking `RepoQlConfig` instead of reading env vars
- Config files at all three scopes are loaded and merged, even before the command exists
- Setting metadata (description, sensitive, restart-required) is available for any surface that needs it

## Prerequisites

- None. This is the foundation — no dependencies on other plans.

## North Star

Adding a new setting to RepoQL is: add a property with `[Setting]` to the right nested class in `RepoQlConfig`. That's it. The key, env var name, default, and registry entry are all derived automatically. No manual registration, no second file to update.

## Done Criteria

### RepoQlConfig

- The `RepoQlConfig` class shall live in `RepoQL.Contracts/Configuration/`
- The `RepoQlConfig` class shall have a nested class for each setting group (`DuckDb`, `Embedding`, `Ort`, `Llm`, `Host`, `Mcp`, `Dotnet`, `Cache`)
- Section properties shall use `set` (not `init`) so the singleton can be replaced on reload
- Each setting property shall be nullable (`T?`) — null means "use consumer's default"
- Each setting property shall have a `[Setting]` attribute with at minimum a description
- Settings with known defaults shall specify `DefaultValue` as a display string on the attribute
- A default-constructed `RepoQlConfig` shall have all properties null (no opinions baked in — defaults live in `[Setting(DefaultValue)]` for display and in consumers for behavior)

### SettingAttribute

- The `SettingAttribute` shall accept a description string as its constructor parameter
- The `SettingAttribute` shall expose `Sensitive`, `RequiresRestart`, `ValidValues`, `DefaultValue`, and `LegacyEnvVar` as optional init properties
- `DefaultValue` is a display string for `::config` output — it does not set the property value
- `LegacyEnvVar` is the old env var name for the one-release compatibility bridge

### SettingRegistry

- The `SettingRegistry` shall build its entries by reflecting over `RepoQlConfig`'s nested types
- The `SettingRegistry` shall derive keys from the property path: section name + property name, lowered and snake_cased (`DuckDb.MemoryLimit` → `duckdb.memory_limit`)
- The `SettingRegistry` shall derive env var names from keys: `REPOQL_` + key with `.` → `_`, uppercased (`duckdb.memory_limit` → `REPOQL_DUCKDB_MEMORY_LIMIT`)
- When a property has `[Setting]`, the registry shall include it
- When a property lacks `[Setting]`, the registry shall ignore it
- The `SettingRegistry` shall provide lookup by key (case-insensitive)
- The `SettingRegistry` shall include `LegacyEnvVar` in `SettingDefinition` when present on the attribute

### ConfigurationLoader

- The loader shall merge sources in precedence order: env > local > repo > user > defaults
- When a JSON config file exists, the loader shall parse it with `CommentHandling = Skip` and `AllowTrailingCommas = true`
- When a JSON config file has invalid JSON, the loader shall skip the entire file and log a warning
- When a config file contains an unknown key, the loader shall skip that key and log a warning
- When an env var contains an unparseable value for its setting type, the loader shall skip it and log a warning
- The loader shall track provenance (`ConfigScope`) for each resolved value
- When a setting has `LegacyEnvVar` and the new env var is not set but the legacy one is, the loader shall use the legacy value and log a deprecation warning
- The loader shall read files from:
  - User: `~/.repoql/config.json`
  - Repo: `<repo>/.repoql.json`
  - Local: `<repo>/.repoql/config.json`

### ResolvedConfig

- `ResolvedConfig` shall expose the resolved `RepoQlConfig` via a `Settings` property
- `ResolvedConfig` shall expose provenance via `GetProvenance(string key)` returning `ResolvedSetting`
- `ResolvedConfig` shall expose `AllResolved` for iterating all settings with their provenance

### DI Registration

- `AddResolvedConfig()` shall register `SettingRegistry`, `ResolvedConfig`, and `RepoQlConfig` as singletons
- `AddResolvedConfig()` shall be callable before `AddRepoIndexer()`
- When no config files exist and no env vars are set, `RepoQlConfig` shall have all properties null (pure defaults)

### Tests

- A test shall verify key derivation from property paths
- A test shall verify env var name derivation from keys
- A test shall verify precedence: env overrides local overrides repo overrides user overrides default
- A test shall verify malformed JSON files are skipped with the rest of the config intact
- A test shall verify unknown keys in config files are ignored
- A test shall verify sensitive flag is read from `[Setting]`
- A test shall verify the registry discovers all `[Setting]`-annotated properties and ignores unannotated ones
- A test shall verify `DefaultValue` from `[Setting]` is available in `SettingDefinition`
- A test shall verify legacy env var is used when new env var is absent, and a deprecation warning is logged
- A test shall verify legacy env var is ignored when new env var is present

## Constraints

- **Contracts only for types** — `RepoQlConfig`, `SettingAttribute`, `ConfigScope`, `ResolvedSetting` live in `RepoQL.Contracts`. `SettingRegistry`, `ConfigurationLoader`, `ResolvedConfig` live in `RepoQL.Core` (they have logic and dependencies).
- **No new NuGet packages** — `System.Text.Json` is already available. No config framework needed.
- **No consumer changes yet** — existing `Environment.GetEnvironmentVariable()` calls remain. Migration is a separate plan.
- **Frozen schema** — nothing touches DuckDB tables.

## References

- [Configuration Design](../designs/future/configuration.md) — architecture, contracts, trade-offs
- [Configuration Flow](../flows/future/configuration.md) — loading sequence, cross-process propagation
- [Configuration North Star](../north-star/configuration.md) — what great looks like
- [Testing guidelines](../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy
- Existing pattern: `CommandRegistry.cs` in `RepoQL.Commands` — similar reflection-based discovery
- Existing pattern: `ClaudeCodeConfigSource.cs` — JSON parsing with comments/trailing commas

## Error Policy

Config loading must never prevent startup. Every failure mode (malformed file, bad env var value, unknown key) is a warning, not an exception. The loader falls back to defaults for any setting it can't resolve. A complete failure to load config results in a `RepoQlConfig` with all nulls — which is valid, because null means "use your default."
