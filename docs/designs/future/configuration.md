# Configuration Design

## North Star

One system for every setting — scoped, layered, discoverable, and configurable without leaving the tool. Zero configuration to start. Full control when you need it.

**Enables:** [Configuration Flow](../../flows/future/configuration.md)

**Guided by:** [Configuration North Star](../../north-star/configuration.md), [Commands North Star](../../north-star/commands.md)

## Context

Configuration is scattered across ~30 environment variables, read via `Environment.GetEnvironmentVariable()` at different moments (registration time, constructor time, call time) with duplicated parsing helpers and no validation surface. There are no config files, no scoping, no commands, no way for an agent to discover or change settings without editing shell profiles.

This design introduces a centralized configuration system that:
- Absorbs all existing env var reads into one loader
- Adds JSON file config at three scopes (user, repo, local)
- Exposes `::config` commands for discovery and mutation
- Preserves env var overrides as the top-priority layer
- Eliminates duplicated parsing logic (`GetEnvInt` appears verbatim in two files)

## Constraints

- **Zero config works** — every setting has a compiled default. RepoQL starts and runs without any config files or env vars.
- **Env vars always win** — for CI, containers, and contexts where files don't apply.
- **Single writer per file** — only `::config` and manual editing write config files. No concurrent file writes.
- **No new tables** — config metadata lives in code, not DuckDB. Runtime config state is in-memory.
- **Command system unchanged** — `::config` is a regular command via `[CommandClass]`/`[Command]`.
- **Sensitive values never in repo scope** — enforced at write time, not by convention.

---

## Components

```
┌──────────────────────────────────────────────────────────┐
│                     Consumers                             │
│  DuckDbStartupOptionsBuilder | RepoqlHost | Embeddings   │
│  IdleShutdownHostedService   | OnnxProvider | ...        │
└──────────────────────────────────────────────────────────┘
                              ▲
                              │ IOptions<T> / IOptionsMonitor<T>
                              │
┌──────────────────────────────────────────────────────────┐
│                   ResolvedConfig                     │
│  - Resolved settings (merged from all sources)            │
│  - Registered in DI, feeds IOptions<T> via .Configure()   │
│  - Change notifications for live-reloadable settings      │
└──────────────────────────────────────────────────────────┘
                              ▲
                              │ Load + Merge
                              │
┌──────────────────────────────────────────────────────────┐
│                  ConfigurationLoader                      │
│  - Discovers and reads config files                       │
│  - Reads env vars                                         │
│  - Merges: env > local > repo > user > defaults           │
│  - Validates all values against SettingRegistry            │
│  - Reports warnings (invalid values, unknown keys)        │
└──────────────────────────────────────────────────────────┘
                              ▲
                              │
              ┌───────────────┼───────────────┐
              │               │               │
     ┌────────────┐  ┌──────────────┐  ┌───────────┐
     │  Settings   │  │  Config      │  │  Env      │
     │  Registry   │  │  Files       │  │  Vars     │
     │             │  │              │  │           │
     │  Metadata   │  │  ~/.repoql/  │  │  REPOQL_* │
     │  per key    │  │  .repoql.json│  │           │
     │             │  │  .repoql/    │  │           │
     └────────────┘  └──────────────┘  └───────────┘

┌──────────────────────────────────────────────────────────┐
│                   ConfigCommand                           │
│  - ::config (list all)                                    │
│  - ::config[key] (read one)                               │
│  - ::config[key, value] (set local)                       │
│  - ::config[key, value, scope] (set at scope)             │
│  - ::config[-key] / ::config[-key, scope] (reset)         │
│  - Reads from SettingRegistry + ResolvedConfig       │
│  - Writes to config files, triggers reload                │
└──────────────────────────────────────────────────────────┘
```

---

## RepoQlConfig

A single monolithic class in `RepoQL.Contracts` that holds every setting. Nested classes group by concern. The config class is the source of truth — no scattered option types, no assembly scanning.

### The class

```csharp
// RepoQL.Contracts/Configuration/RepoQlConfig.cs

public sealed class RepoQlConfig
{
    public DuckDbSettings DuckDb { get; set; } = new();
    public EmbeddingSettings Embedding { get; set; } = new();
    public OrtSettings Ort { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public HostSettings Host { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public DotnetSettings Dotnet { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();

    public sealed class DuckDbSettings
    {
        [Setting("DuckDB memory cap", RequiresRestart = true, ValidValues = "e.g. 4GB, 512MB",
                 LegacyEnvVar = "DUCKDB_MEMORY_LIMIT")]
        public string? MemoryLimit { get; set; }

        [Setting("DuckDB thread count", RequiresRestart = true, LegacyEnvVar = "DUCKDB_THREADS")]
        public int? Threads { get; set; }

        [Setting("DuckDB temp file location", RequiresRestart = true, LegacyEnvVar = "DUCKDB_TEMP_DIRECTORY")]
        public string? TempDirectory { get; set; }

        [Setting("Read connection pool size (1-4)", RequiresRestart = true, DefaultValue = "2",
                 LegacyEnvVar = "DUCKDB_READ_POOL_SIZE")]
        public int? ReadPoolSize { get; set; }
    }

    public sealed class EmbeddingSettings
    {
        [Setting("Embedding generation mode", RequiresRestart = true,
                 ValidValues = "none|structure|full", DefaultValue = "full",
                 LegacyEnvVar = "REPOQL_EMBED_MODE")]
        public string? Mode { get; set; }

        [Setting("Path to ONNX model", RequiresRestart = true)]
        public string? ModelPath { get; set; }

        [Setting("Max tokens per embedding sample", RequiresRestart = true, DefaultValue = "256")]
        public int? MaxTokens { get; set; }

        [Setting("Embedding dimension for hashed provider", RequiresRestart = true, DefaultValue = "384")]
        public int? Dim { get; set; }

        [Setting("Batch size for embedding generation")]
        public int? BatchSize { get; set; }

        [Setting("Concurrency for vector indexing")]
        public int? Concurrency { get; set; }
    }

    public sealed class OrtSettings
    {
        [Setting("ONNX execution provider", RequiresRestart = true,
                 ValidValues = "CPU|CUDA|DML|COREML", DefaultValue = "CPU")]
        public string? Provider { get; set; }

        [Setting("ONNX intra-op thread count", RequiresRestart = true, DefaultValue = "0")]
        public int? IntraThreads { get; set; }

        [Setting("ONNX inter-op thread count", RequiresRestart = true, DefaultValue = "1")]
        public int? InterThreads { get; set; }
    }

    public sealed class LlmSettings
    {
        [Setting("LLM API key", Sensitive = true, RequiresRestart = true,
                 LegacyEnvVar = "OPENROUTER_API_KEY")]
        public string? ApiKey { get; set; }

        [Setting("Max concurrent LLM API calls", RequiresRestart = true, DefaultValue = "4")]
        public int? Concurrency { get; set; }
    }

    public sealed class HostSettings
    {
        [Setting("Seconds before idle host shuts down", DefaultValue = "45")]
        public int? IdleGraceSeconds { get; set; }

        [Setting("Client lease TTL in seconds", DefaultValue = "30")]
        public int? LeaseTtlSeconds { get; set; }

        [Setting("Watchdog timeout after shutdown in seconds", DefaultValue = "15")]
        public int? ShutdownWatchdogSeconds { get; set; }

        [Setting("Host startup timeout in milliseconds", DefaultValue = "120000")]
        public int? StartTimeoutMs { get; set; }

        [Setting("Lease establishment timeout in milliseconds", DefaultValue = "5000")]
        public int? LeaseStartTimeoutMs { get; set; }
    }

    public sealed class McpSettings
    {
        [Setting("Load global agent MCP configs", DefaultValue = "true")]
        public bool? IncludeGlobals { get; set; }

        [Setting("Comma-separated list of enabled agent types")]
        public string? EnabledAgents { get; set; }
    }

    public sealed class DotnetSettings
    {
        [Setting("Enable deep Roslyn analysis (expensive)", DefaultValue = "false")]
        public bool? Analysis { get; set; }
    }

    public sealed class CacheSettings
    {
        [Setting("Shared memory cache size limit", RequiresRestart = true, DefaultValue = "128")]
        public long? SizeLimit { get; set; }
    }
}
```

Section properties use `set` (not `init`) so the singleton can be replaced on reload. All setting properties are `T?` — null still means "use consumer's default." `DefaultValue` on the attribute is the display string for `::config` output; it does not set the property value.

### Setting attribute

```csharp
// RepoQL.Contracts/Configuration/SettingAttribute.cs

[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string description) : Attribute
{
    public string Description { get; } = description;
    public bool Sensitive { get; init; }
    public bool RequiresRestart { get; init; }
    public string? ValidValues { get; init; }   // display hint for closed sets
    public string? DefaultValue { get; init; }  // display string for ::config output
    public string? LegacyEnvVar { get; init; }  // one-release compat bridge, then removed
}
```

### Key derivation

Keys are derived from the property path, not specified manually:

```
RepoQlConfig.DuckDb.MemoryLimit  →  key: "duckdb.memory_limit"
RepoQlConfig.Embedding.Mode     →  key: "embedding.mode"
RepoQlConfig.Llm.ApiKey         →  key: "llm.api_key"

Rule: section name + "." + property name, both lowered and snake_cased
```

No manual key strings. The class structure *is* the key hierarchy.

### Setting registry

The registry is built by reflecting over `RepoQlConfig` at startup — not by scanning assemblies:

```csharp
public sealed class SettingRegistry
{
    // Built from RepoQlConfig's nested types and [Setting] attributes
    public static SettingRegistry Build() => Build(new RepoQlConfig());

    public IReadOnlyDictionary<string, SettingDefinition> Settings { get; }
    public SettingDefinition? TryGet(string key);
    public IEnumerable<SettingDefinition> All { get; }
}

public sealed record SettingDefinition(
    string Key,              // "duckdb.memory_limit"
    string EnvVar,           // "REPOQL_DUCKDB_MEMORY_LIMIT" (derived from key)
    string? LegacyEnvVar,    // "DUCKDB_MEMORY_LIMIT" (compat bridge, temporary)
    string Description,
    Type PropertyType,
    string? DefaultValue,    // display string from [Setting], e.g. "2"
    bool Sensitive,
    bool RequiresRestart,
    string? ValidValues,
    PropertyInfo SectionProperty,   // RepoQlConfig.DuckDb
    PropertyInfo SettingProperty);  // DuckDbSettings.MemoryLimit
```

One class to reflect over, not the entire `AppDomain`.

---

## Config File Format

Nested JSON matching the dot-separated key structure:

```json
{
  "duckdb": {
    "memory_limit": "4GB",
    "threads": 4
  },
  "embedding": {
    "mode": "full"
  }
}
```

Parsed with `JsonDocumentOptions { CommentHandling = Skip, AllowTrailingCommas = true }` — matching the existing pattern in `ClaudeCodeConfigSource`.

Flattened to dot-separated keys during load: `duckdb.memory_limit` = `"4GB"`.

---

## ConfigurationLoader

### Load sequence

```
1. Compile defaults from SettingRegistry (DefaultValue strings)
2. If ~/.repoql/config.json exists → parse, flatten, overlay
3. If <repo>/.repoql.json exists → parse, flatten, overlay
4. If <repo>/.repoql/config.json exists → parse, flatten, overlay
5. For each key in SettingRegistry → check env var (new name) → overlay if present
6. Compatibility bridge: for each key, if no new env var was found,
   check the legacy env var name (if one exists). If found, use it and
   log a deprecation warning naming both old and new env var.
7. Validate all resolved values against SettingDefinition types
8. Log warnings for: malformed files (skip entire file), unknown keys (skip key),
   invalid values (use default, log which key and what was wrong)
9. Build ResolvedConfig with resolved values + provenance per key
```

The compatibility bridge (step 6) is a one-release transition. Settings that had a different env var name carry a `LegacyEnvVar` on their `[Setting]` attribute. After one release, the legacy names and the bridge are removed.

### Provenance tracking

```csharp
public enum ConfigScope { Default, User, Repo, Local, Environment }

public sealed record ResolvedSetting(
    string Key,
    object? Value,
    ConfigScope Source);        // where the winning value came from
```

`ResolvedConfig` holds `IReadOnlyDictionary<string, ResolvedSetting>` and typed accessors (e.g., `DuckDb.MemoryLimit`).

### Env var naming

```
Key:     duckdb.memory_limit
Env var: REPOQL_DUCKDB_MEMORY_LIMIT

Rule: "REPOQL_" + key.Replace(".", "_").ToUpperInvariant()
```

One rule, no exceptions, derived automatically from the key.

---

## DI Integration

### Registration

```csharp
// Called early, before AddRepoIndexer()
public static IServiceCollection AddResolvedConfig(
    this IServiceCollection services, string repoRoot)
{
    var registry = SettingRegistry.Build();
    var resolved = ConfigurationLoader.Load(registry, repoRoot);

    services.AddSingleton(registry);
    services.AddSingleton(resolved);          // ResolvedConfig (resolved + provenance)
    services.AddSingleton(resolved.Settings); // RepoQlConfig (the typed values)

    return services;
}
```

`ResolvedConfig` wraps both the resolved `RepoQlConfig` instance and the provenance map. Components that just need values take `RepoQlConfig`. Components that need provenance (like `::config`) take `ResolvedConfig`.

### Consumer migration

Before:
```csharp
var raw = Environment.GetEnvironmentVariable("DUCKDB_MEMORY_LIMIT");
if (!string.IsNullOrWhiteSpace(raw) && MemoryLimitPattern.IsMatch(raw)) ...
```

After:
```csharp
public DuckDbStartupOptionsBuilder(RepoQlConfig config)
{
    var memoryLimit = config.DuckDb.MemoryLimit ?? DefaultMemory;
}
```

Components take `RepoQlConfig` directly — one constructor parameter, strongly typed, null means "use your default". No parsing, no env var names, no duplicated `GetEnvInt` helpers.

---

## ::config Command

### Implementation

```csharp
[CommandClass]
internal sealed class ConfigCommand(SettingRegistry registry, ResolvedConfig resolved,
                                    RepositoryConfiguration repo)
{
    [Command("config", Description = "View or change configuration settings")]
    public Task<CommandResult> Execute(
        [CommandParam("key, or key+value, or -key to reset")] string? param1,
        [CommandParam("value or scope")] string? param2,
        [CommandParam("scope (local, repo, user)")] string? param3,
        CancellationToken cancel)
    {
        // Dispatch based on parameter shape:
        // No params          → list all
        // [key]              → show one
        // [-key]             → reset local
        // [-key, scope]      → reset at scope
        // [key, value]       → set local
        // [key, value, scope] → set at scope
    }
}
```

### List all (`::config`)

```
duckdb.memory_limit    4GB       (local)    DuckDB memory cap
duckdb.threads         auto      (default)  DuckDB thread count
embedding.mode         full      (repo)     none | structure | full
llm.api_key            sk-12***  (env)      OpenRouter API key
```

Format: `key  value  (scope)  description`. Sensitive values masked. Grouped by prefix.

### Show one (`::config[embedding.mode]`)

```
embedding.mode
  Value:    full
  Source:   repo (.repoql.json)
  Default:  full
  Env var:  REPOQL_EMBEDDING_MODE
  Options:  none | structure | full
  Restart:  required
```

### Set (`::config[embedding.mode, structure]`)

1. Validate key exists in registry
2. Validate value parses to the property type
3. Check sensitive + scope constraints
4. Read target file → merge → write atomically (write to `.tmp`, rename)
5. Reload config in-memory
6. Report success, note if restart required

### Reset (`::config[-embedding.mode]`)

1. Read target file
2. Remove the key
3. Write atomically
4. Reload

### Atomic write

```csharp
static void WriteConfigFile(string path, JsonObject content)
{
    var tmp = path + ".tmp";
    File.WriteAllText(tmp, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.Move(tmp, path, overwrite: true);
}
```

---

## Live Reload

Most settings are consumed at startup and locked. `RepoQlConfig` is a singleton — its values don't change during the process lifetime. For the few settings that can change at runtime, `ResolvedConfig.Reload()` rebuilds the config and updates the singleton's mutable properties in place.

This is intentionally simple. `::config` writes to disk and calls `Reload()`. Components that hold a reference to `RepoQlConfig` see the new values on their next access. Components that captured a value in their constructor (most of them) don't — they need a restart.

### Setting lifecycle

| Category | `RequiresRestart` | Behavior | Examples |
|----------|-------------------|----------|----------|
| Startup-locked | `true` | Captured at construction, restart needed | `duckdb.*`, `embedding.mode`, `ort.*`, `llm.api_key` |
| Live | `false` | Read from `RepoQlConfig` on each use | `dotnet.analysis` |

Most settings are startup-locked. This is the simple, correct default. `RequiresRestart = true` is the default assumption — marking a setting as live-reloadable is an explicit decision.

---

## Cross-Process Propagation

The client copies `REPOQL_*` env vars to the host process (existing behavior in `RepoQlClient.LaunchHost()`). This is unchanged — env vars flow across the process boundary naturally.

Both processes read the same config files (same repo root, same user home). They converge on the same resolved config without explicit serialization.

The `::config` command runs on the host (commands execute host-side). After writing a file, the host reloads immediately. The client doesn't need to reload — it forwards commands to the host, and the host owns all stateful config.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Monolithic `RepoQlConfig` in Contracts | Distributed option classes per project | One type, one file, all settings visible together. Keys derived from structure, not manually specified. Registry built from one class, not assembly scan. |
| Nested JSON files | Flat key=value | Natural grouping; hand-editable; matches the dot-separated key convention |
| Env vars derived from key | Backwards-compatible env names | One rule, no exceptions. Old env names were inconsistent (`REPOQL_EMBED_MODE` vs `REPOQL_EMBEDDING_MODE`). One-release compatibility bridge via `LegacyEnvVar` on `[Setting]` — warns and works, then removed. |
| Direct `RepoQlConfig` injection | `IOptions<T>` per section | Simpler — one constructor parameter, strongly typed, no `IOptions` ceremony. Config is loaded once at startup; live-reloadable settings are the rare exception. |
| Atomic file write (tmp + rename) | Direct overwrite | Safe against crashes mid-write |
| Three file scopes | Two (repo + local) | User scope covers cross-repo preferences (LLM provider, editor settings) without repeating per-repo |
| Provenance tracking | Value only | Agents need to know *why* a value is what it is, not just *what* it is |

## Alternatives Considered

**`IConfiguration` + `IOptions<T>` pipeline.** Wire JSON files and env vars through `ConfigurationBuilder.AddJsonFile().AddEnvironmentVariables()` and bind to `IOptions<T>` per section — the standard .NET approach. Rejected: no provenance tracking (can't tell which source a value came from), no setting metadata (description, sensitive flag, valid values), no `::config` integration, settings scattered across projects. Would need a parallel system for the command surface anyway.

**Distributed option classes.** One options class per project (`DuckDbOptions` in Data.DuckDB, `EmbeddingOptions` in Embeddings, etc.). Rejected: settings scattered across the solution, no single place to see everything, assembly scanning needed for registry, key names would need manual specification.

**TOML or YAML config files.** Rejected: JSON is already used everywhere in the ecosystem (Claude settings, MCP config, `package.json`). Adding a dependency for a different format adds friction. JSON with comments-allowed covers the use case.

**Database-backed config.** Store settings in DuckDB. Rejected: config must be readable before the database opens (DuckDB options are config). Circular dependency.

**Single config file.** One `.repoql/config.json` instead of three scopes. Rejected: no way to share team settings via version control, no way to have personal defaults across repos.

## Risks

| Risk | Mitigation |
|------|------------|
| Breaking change for existing env vars | One-release compatibility bridge: `LegacyEnvVar` on `[Setting]` checks old name, uses it, logs deprecation warning. Removed in the following release. |
| Config file conflicts (multiple agents) | Atomic write (tmp + rename) prevents corruption. Last writer wins — acceptable for config. |
| Large options surface | Start with the ~30 existing env vars. Registry makes the full set discoverable. |
| Malformed config blocks startup | Never. Malformed files are skipped with warnings. Defaults always work. |
| Live reload complexity | Most settings are startup-locked. Live reload is opt-in per property, not the default. |

## Extension Points

- **New settings** — add a property with `[Setting]` to the appropriate nested class in `RepoQlConfig`. Registry discovers it on next build.
- **New scopes** — the loader is a pipeline of sources. A new scope (e.g., workspace, project) is a new source in the merge chain.
- **Config validation** — `SettingDefinition` can carry a validator delegate for complex constraints beyond type parsing.
- **`help://configuration`** — the setting registry feeds auto-generated docs. Every setting is documented by existing in the registry.
- **SQL surface** — `::config` is a command, but a `_config()` UDF could expose settings in SQL for diagnostic queries.
