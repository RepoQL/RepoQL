# Plan: Configuration Consumer Migration

Implements: [Configuration Design — Consumer Migration](../designs/future/configuration.md#di-integration)

## Scope

**Covers:**
- Migrating all `Environment.GetEnvironmentVariable()` calls for `REPOQL_*` and `DUCKDB_*` settings to use `RepoQlConfig`
- Removing duplicated `GetEnvInt` helpers
- Updating `DuckDbStartupOptionsBuilder` to take `RepoQlConfig`
- Updating `RepoIndexerServiceCollectionExtensions.AddRepoIndexer()` to read from `RepoQlConfig` instead of env vars
- Updating `IdleShutdownHostedService` and `RepoQlServiceImpl` to use config
- Updating `OnnxEmbeddingProvider` to use config
- Updating `McpConfigOptions` to use config
- Updating env var propagation in `RepoQlClient.LaunchHost()`
- Tests verifying consumers receive config values correctly

**Does not cover:**
- `RepoQlConfig` class, registry, or loader (Plan: configuration-1-foundation)
- `::config` command (Plan: configuration-2-command)
- Internal/process-coordination env vars that aren't settings (`REPOQL_IMPLICIT`, `REPOQL_IMPLICIT_SOURCE`, `REPOQL_CWD`) — these remain direct env var reads because they're process signals, not configuration

## Enables

- Single source of truth for all config — no more scattered env var reads
- `::config` command shows actual values consumed by components (not a parallel truth)
- Config files at all scopes work end-to-end — a `.repoql.json` setting actually reaches the component that uses it

## Prerequisites

- Plan: configuration-1-foundation complete — `RepoQlConfig` and `AddResolvedConfig()` must be registered in DI
- Plan: configuration-2-command is NOT required — migration can happen before or after the command

## North Star

Zero direct `Environment.GetEnvironmentVariable()` calls for settings. Every configurable behavior reads from `RepoQlConfig`. The env var path still works — it just flows through the config system instead of bypassing it.

## Done Criteria

### DuckDB Configuration

- `DuckDbStartupOptionsBuilder` shall take `RepoQlConfig.DuckDbSettings` instead of reading `DUCKDB_MEMORY_LIMIT`, `DUCKDB_THREADS`, `DUCKDB_TEMP_DIRECTORY`, `DUCKDB_READ_POOL_SIZE` from env vars
- The `DuckDbEnvironmentIssue` validation pattern shall remain — config values are validated the same way env vars were

### Embedding Configuration

- `AddRepoIndexer()` shall read `RepoQlConfig.Embedding` instead of `REPOQL_EMBED_MODE`, `REPOQL_EMBED_ENABLED`, `REPOQL_EMBED_DIM`, `REPOQL_EMBED_MAX_TOKENS`, `REPOQL_EMBED_MODEL_PATH`
- `EmbeddingRefresher` shall read batch size from `RepoQlConfig.Embedding.BatchSize`
- `EmbeddingCoordinator` shall read concurrency from `RepoQlConfig.Embedding.Concurrency`
- The `REPOQL_EMBED_ENABLED` legacy env var shall be removed — `embedding.mode = none` replaces it

### ONNX Configuration

- `OnnxEmbeddingProvider` shall take `RepoQlConfig.OrtSettings` instead of reading `REPOQL_ORT_PROVIDER`, `REPOQL_ORT_INTRA_THREADS`, `REPOQL_ORT_INTER_THREADS`

### LLM Configuration

- `AddRepoIndexer()` shall read `RepoQlConfig.Llm.ApiKey` instead of `OPENROUTER_API_KEY`
- `OpenRouterEmbeddingProvider` shall read concurrency from `RepoQlConfig.Llm.Concurrency`

### Host Lifecycle Configuration

- `IdleShutdownHostedService` shall take `RepoQlConfig.HostSettings` instead of reading `REPOQL_LEASE_TTL_SECONDS`, `REPOQL_IDLE_GRACE_SECONDS`, `REPOQL_IMPLICIT_SHUTDOWN_WATCHDOG_SECONDS`
- `RepoQlServiceImpl` shall take `RepoQlConfig.HostSettings` instead of its own `GetEnvInt` copy
- Both duplicated `GetEnvInt` / `GetEnvIntAllowZero` helpers shall be deleted

### MCP Configuration

- `McpConfigOptions.FromEnvironment()` shall be replaced — `McpConfigOptions` reads from `RepoQlConfig.McpSettings` instead of `REPOQL_MCP_INCLUDE_GLOBALS`, `REPOQL_MCP_ENABLED_AGENTS`

### Client-Side Configuration

- `RepoQlClient` shall read `RepoQlConfig.Host.StartTimeoutMs` and `RepoQlConfig.Host.LeaseStartTimeoutMs` instead of env vars
- `RepoQlClient.LaunchHost()` env var propagation shall remain — env vars still cross the process boundary. The host loads config independently via `AddResolvedConfig()`.

### .NET Analysis

- `CSharpLoader` shall read `RepoQlConfig.Dotnet.Analysis` instead of `REPOQL_DOTNET_ANALYSIS`
- `CSharpLoader` also reads from `IConfiguration` (keys `REPOQL_DOTNET_ANALYSIS`, `RepoQL:DotNet:Analysis`, `repoql:dotnet:analysis`) — both the env var path and the `IConfiguration` path shall be removed, replaced by the single `RepoQlConfig` read
- `CSharpWorkspaceHost` shall read `REPOQL_CSHARP_WORKSPACE_SESSION_SLIDING_SECONDS`, `REPOQL_CSHARP_WORKSPACE_SESSION_ABSOLUTE_SECONDS`, and `REPOQL_CSHARP_WORKSPACE_SESSION_ENTRY_SIZE` from `RepoQlConfig.Dotnet` (add settings for these)

### Shared Cache

- `AddRepoIndexer()` shall read cache size from `RepoQlConfig.Cache.SizeLimit`

### Tests

- A test shall verify `DuckDbStartupOptionsBuilder` uses config values
- A test shall verify embedding mode is read from config
- A test shall verify the old env var names no longer have effect when config system is active
- A test shall verify host lifecycle settings flow from config

## Constraints

- **Process signals stay as env vars** — `REPOQL_IMPLICIT`, `REPOQL_IMPLICIT_SOURCE`, `REPOQL_CWD`, `REPOQL_SOCKET` are not settings — they're process coordination signals or infrastructure addresses read before DI. They remain direct env var reads.
- **Compatibility bridge** — settings with `LegacyEnvVar` on their `[Setting]` attribute will check the old env var name if the new one is absent, and log a deprecation warning. This is a one-release transition; the legacy names and bridge code are removed in the following release.
- **`REPOQL_EMBED_ENABLED` removed** — legacy toggle replaced by `embedding.mode = none`. No compatibility bridge for this one — it was already a boolean override of a richer setting.

## References

- [Configuration Design](../designs/future/configuration.md) — consumer migration pattern
- Key files to modify:
  - `src/RepoQL.Data.DuckDB/DuckDbStartupOptionsBuilder.cs`
  - `src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs`
  - `src/RepoQL.ConsoleApp/Host/IdleShutdownHostedService.cs`
  - `src/RepoQL.ConsoleApp/Services/RepoQlServiceImpl.cs`
  - `src/RepoQL.Embeddings/OnnxEmbeddingProvider.cs`
  - `src/RepoQL.LLM.Client/OpenRouterLlmProvider.cs`
  - `src/RepoQL.LLM.Client/OpenRouterEmbeddingProvider.cs`
  - `src/RepoQL.Mcp.Client/Configuration/McpConfigOptions.cs`
  - `src/RepoQL.Protocol/RepoQlClient.cs`
  - `src/Formats/RepoQL.Formats.DotNet/CSharpLoader.cs`
  - `src/Formats/RepoQL.Formats.DotNet/CSharpWorkspaceHost.cs`
  - `src/Indexing/RepoQL.Indexing/PostProcessing/EmbeddingCoordinator.cs`
  - `src/Indexing/RepoQL.Indexing/PostProcessing/EmbeddingRefresher.cs`

## Error Policy

Migration is mechanical — replace env var reads with config property reads. Where existing code has validation (e.g., `DuckDbStartupOptionsBuilder`'s `DuckDbEnvironmentIssue` accumulator), preserve the validation but source from config instead. Where existing code silently falls back to defaults on bad values, maintain that behavior — the config loader already validated and logged warnings during loading.
