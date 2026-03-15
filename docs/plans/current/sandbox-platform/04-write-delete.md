# Plan: Write and Delete Capabilities

Implements: [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Write + Delete, IWritableFileSystem

## Scope

**Covers:**
- `repoql.write()` and `repoql.delete()` methods added to the `repoql` global
- Wire to `IWritableFileSystem` via capability provider
- Write scope enforcement via `ISandboxScopeEnforcer`
- Delete scope enforcement (matches write scopes)
- Default `.repoql/tmp/` scratch directory creation
- Tests for write, delete, scope enforcement, error cases

**Does not cover:**
- Read capabilities (Plan: 02-capability-injection — already done)
- Module registry (Plan: 05-module-registry)
- Write to non-file:// schemes (design explicitly excludes this)

## Enables

Once write and delete exist:
- **Scratch output** — agents can generate reports, CSV files, transformed data in `.repoql/tmp/`
- **Orchestration workflows** — read files, process in JS, write results — all within one sandbox invocation
- **Plan: 05-module-registry** — module registration can write to `.repoql/modules/` (with appropriate write scope)
- **Pipeline integration** — files written to the repo tree (with explicit scope) can be picked up by the indexing pipeline

## Prerequisites

- **Plan: 01-foundation-contracts** completed — `IWritableFileSystem` available
- **Plan: 02-capability-injection** completed — `repoql` global injection working, scope enforcement working
- Existing `FileUriPathResolver` for URI to path resolution

## North Star

`repoql.write("file:///.repoql/tmp/report.csv", csvContent)` works on first try with no configuration. Write to `.repoql/tmp/` is always safe. Write anywhere else requires explicit opt-in. Delete follows the same rules as write. Errors tell the agent exactly what scope to configure.

## Done Criteria

### repoql.write()

- The `write()` method shall accept a URI string and a string content argument
- The `write()` method shall call `ISandboxScopeEnforcer.EnforceWrite()` before writing
  - When scope enforcement fails, throw a JS `Error` with the scope details and config command
- The `write()` method shall call `IWritableFileSystem.Write()` with the resolved URI and content
  - When the target file already exists, the write shall overwrite it (replace, not fail)
  - When the scheme is not writable (`CanWrite()` returns false), throw a JS `Error`: `"Write not supported for scheme '<scheme>'. Only file:// supports writes."`
  - When the filesystem write fails, throw a JS `Error` with the path and cause
- The `write()` method shall return `undefined` (void operation)
- The `write()` method shall increment the execution context's capability call count

### repoql.delete()

- The `delete()` method shall accept a URI string
- The `delete()` method shall call `ISandboxScopeEnforcer.EnforceDelete()` before deleting
  - When scope enforcement fails, throw a JS `Error` with the scope details and config command
- The `delete()` method shall call `IWritableFileSystem.Delete()` with the resolved URI
  - When the file doesn't exist, throw a JS `Error`: `"File not found: '<path>'"`
  - When the filesystem delete fails, throw a JS `Error` with the path and cause
- The `delete()` method shall return `undefined` (void operation)
- The `delete()` method shall increment the execution context's capability call count

### Statement Counting

- Write and delete calls shall pause and resume the statement counter identically to read calls (one statement per call)

### Default Scratch Directory

- When the `.repoql/tmp/` directory does not exist, the first write to it shall create it
- The default write scope shall be an absolute repo-rooted URI glob resolved at startup (e.g., `file:///C:/Source/MyRepo/.repoql/tmp/**`), consistent with Plan 01's scope derivation

### Output Footer Update

- The output formatter shall include write and delete counts in the capability summary
  - When writes and deletes occurred: `3 reads, 1 write, 1 delete`
  - When only writes occurred: `1 write`

### Updated repoql Global

- The `repoql` global shall now include: `read`, `write`, `delete`, `log`, `warn`, `error`
- The object shall remain frozen after construction

## Constraints

- **file:// only** — writes and deletes are only supported for the `file://` scheme. Other schemes throw an actionable error. (Design: IWritableFileSystem)
- **No graph writes** — writing a file does not automatically add it to the DuckDB graph. The indexing pipeline handles that if the file is in scope. (Design: Alternatives Considered)
- **Scope defaults are narrow** — `.repoql/tmp/**` only. Broader scopes require `::config.set`. (Design: Default Scopes)
- **Atomic writes** — the `file://` implementation should write to a temp file and rename, preventing partial writes on crash. (Design: IWritableFileSystem)

## References

- [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Capability Provider, IWritableFileSystem, scope defaults
- [Sandbox Execution Flow](../flows/future/sandbox/sandbox-execution.md) — capability call sub-flow (scope → pause → execute → resume)
- `src/RepoQL.FileSystem/Physical/FileUriPathResolver.cs` — URI to path resolution
- `src/RepoQL.Contracts/RepoLocator.cs` — `EnsureRepoqlDirectory()` pattern for directory creation
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Write and delete errors become catchable JS exceptions. The script can handle them or let them bubble.

- **Scope violation** → JS `Error` with denied URI, allowed scopes, and `::config.set` command
- **Unsupported scheme** → JS `Error` naming the scheme and explaining only `file://` supports writes
- **Filesystem error** (permissions, disk full, not found) → JS `Error` with path and cause
- **All errors are catchable** — scripts can `try/catch` around writes and handle failures gracefully
