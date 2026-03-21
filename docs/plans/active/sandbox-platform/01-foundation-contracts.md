# Plan: Sandbox Foundation Contracts

Implements: [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — ISandboxContentReader, IWritableFileSystem, scope model

## Scope

**Covers:**
- `ISandboxContentReader` interface and implementation wrapping `IReadContentProvider`
- `IWritableFileSystem` interface and `file://` implementation
- `ISandboxScopeEnforcer` interface and implementation using `UriPatternMatcher`
- Repo-rooted default scope derivation from VFS mounts
- `SandboxScopes` and `SandboxExecutionContext` types
- Expanded `SandboxSettings` with scope configuration
- Tests for all contracts

**Does not cover:**
- `repoql` global injection into JS engine (Plan: 02-capability-injection)
- Output formatting (Plan: 03-output-formatting)
- Module registry (Plan: 05-module-registry)

## Enables

Once foundation contracts exist:
- **Plan: 02-capability-injection** can wire the `repoql` global to real infrastructure
- **Scope enforcement is testable** independently of the JS engine
- **Write operations** have a clean contract ready for Plan 04
- **Repo-rooted access** prevents sandbox reads from escaping the repository

This is the infrastructure layer. All subsequent plans depend on it.

## Prerequisites

- Existing `IReadContentProvider` in `src/RepoQL.Contracts/ReadContracts.cs` — document fetching and representation selection
- Existing `UriPatternMatcher` in `src/RepoQL.Contracts/UriPatternMatcher.cs` — glob pattern matching for URIs
- Existing `FileUriPathResolver` in `src/RepoQL.FileSystem/Physical/FileUriPathResolver.cs` — URI to filesystem path resolution
- Existing `RepoLocator` in `src/RepoQL.Contracts/RepoLocator.cs` — repo root discovery
- Existing `SandboxSettings` in `src/RepoQL.Contracts/Configuration/RepoQlConfig.cs`

## North Star

The capability provider should not know or care how content is fetched or files are written. It calls `Read()`, gets data. Calls `Write()`, content persists. Calls `EnforceRead()`, gets through or gets a specific exception. No presentation, no consent flows, no inference — just data operations with scope boundaries.

## Done Criteria

### ISandboxContentReader

- The `ISandboxContentReader` shall accept a URI string and token budget and return a `SandboxReadResult`
- The content reader shall delegate to `IReadContentProvider.FetchGlobAsync` for document fetching
- The content reader shall apply representation selection based on budget (full → structure → headline)
  - When content fits within budget, return full content with representation `"full"`
  - When content exceeds budget but structure fits, return structure with representation `"structure"`
  - When only headline fits, return headline with representation `"headline"`
- The content reader shall support modifier syntax (`=> tree`, `=> structure`, `=> blame`, etc.)
  - When a modifier is present, the content reader shall delegate to the appropriate `IModifierHandler`
  - When a modifier is present, return the modifier's rendered output with representation set to the modifier name
- The content reader shall return `SandboxReadResult` with `Success = false` and an error message when:
  - No files match the URI pattern
  - The modifier is not recognized
- The content reader shall **not** append footers, trigger consent flows, or invoke LLM inference

### IWritableFileSystem

- The `IWritableFileSystem` shall accept a `RepoUri` and string content for writes
- The `IWritableFileSystem` shall accept a `RepoUri` for deletes
- The `file://` implementation shall resolve URIs to filesystem paths via `FileUriPathResolver`
- The `file://` implementation shall create parent directories on write if they don't exist
- The `file://` implementation shall write content as UTF-8 text via atomic temp-file-then-rename
  - When write fails (permissions, disk full), throw `IOException` with the path and cause
- The `file://` implementation shall delete the file at the resolved path
  - When the file doesn't exist, throw `FileNotFoundException` with the path
- The `CanWrite()` method shall return `true` for `"file"` scheme and `false` for all others

### ISandboxScopeEnforcer

- The scope enforcer shall accept `SandboxScopes` at construction
- The `EnforceRead()` method shall check the URI against read scope patterns using `UriPatternMatcher`
  - When the URI matches any read scope pattern, allow (no exception)
  - When the URI matches no read scope pattern, throw `SandboxScopeException` naming all allowed read scopes
- The `EnforceWrite()` and `EnforceDelete()` methods shall behave identically for their respective scope lists
- The `SandboxScopeException` message shall include the operation, the denied URI, the allowed scopes, and the config command to change scopes

### Repo-Rooted Scope Derivation

- The default read scopes shall be derived from mounted VFS schemes at host startup
  - When `file://`, `help://`, and `github://` VFS mounts exist, the default read scopes shall include all three
  - The `file://` read scope shall be rooted to the repo root (e.g., `file:///C:/Source/MyRepo/**`)
- The default write scopes shall be absolute repo-rooted URI globs (e.g., `["file:///C:/Source/MyRepo/.repoql/tmp/**"]`), resolved from the repo root at startup
- The default delete scopes shall match the write scopes
- When the user configures custom scopes via `::config.set`, the configured scopes shall replace the defaults entirely

### SandboxSettings Expansion

- The `SandboxSettings` class shall add `ReadScopes`, `WriteScopes`, and `DeleteScopes` as `List<string>?` properties
- Each scope property shall have a `[Setting]` attribute with a description and no default value (defaults are derived at runtime from VFS mounts)
- When a scope property is `null`, the runtime-derived defaults shall be used
- When a scope property is set, the configured values shall be used verbatim

### SandboxExecutionContext

- The `SandboxExecutionContext` shall carry `ISandboxCapabilityProvider?`, `ISandboxScopeEnforcer?`, `SandboxScopes`, diagnostics list, per-operation capability counts, and tokens consumed
  - Per-operation counts: `ReadCount`, `WriteCount`, `DeleteCount` (separate integers, not aggregate)
  - `TokensConsumed`: total tokens consumed across all reads
  - `ElapsedMs`: populated by the caller (sandbox engine or handler) after execution completes
- The context shall be constructable with `null` capabilities (for SQL `js()` path — no capabilities)
- The diagnostics list shall be mutable during execution and readable after

## Constraints

- **No ReadOrchestrator dependency** — the content reader wraps `IReadContentProvider` and `IModifierHandler`, not `ReadOrchestrator`. The orchestrator has tool-level concerns (footers, consent, inference) that don't belong in data-level reads. (Design: ISandboxContentReader section)
- **No new DuckDB tables** — scope configuration lives in `RepoQlConfig`, not the database. (Design: Constraints)
- **Write is file:// only** — `IWritableFileSystem` implementations for other schemes are out of scope. `CanWrite()` returns `false` for non-file schemes. (Design: IWritableFileSystem)
- **Scope patterns use existing UriPatternMatcher** — no new pattern matching infrastructure. (Design: Scope Enforcement)

## References

- [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — contracts, scope model, component diagram
- `src/RepoQL.Contracts/ReadContracts.cs` — `IReadContentProvider`, `ReadDocument`, `RepresentationCosts`
- `src/RepoQL.Contracts/UriPatternMatcher.cs` — `Matches()`, `ParsePatterns()`
- `src/RepoQL.FileSystem/Physical/FileUriPathResolver.cs` — `Resolve()`, `ToAbsolutePath()`
- `src/RepoQL.Read/ReadOrchestrator.cs` — `SelectRepresentation()` logic to replicate (not call)
- `src/RepoQL.Read/ModifierDispatcher.cs` — modifier resolution and dispatch
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Errors in foundation contracts return structured results or throw typed exceptions — never raw strings or untyped exceptions.

- **Read failures** return `SandboxReadResult` with `Success = false` and a descriptive `Error` string
- **Write/delete failures** throw `IOException` or `FileNotFoundException` — callers (capability provider) catch and convert to JS exceptions
- **Scope violations** throw `SandboxScopeException` — callers catch and convert to catchable JS exceptions with actionable messages
