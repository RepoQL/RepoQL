# Plan: Module Registry

Implements: [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Module Registry, Module Loader, Module Commands, Help Integration

## Scope

**Covers:**
- `IModuleRegistry` interface and file-based implementation under `.repoql/modules/`
- Module manifest (`manifest.json`) with capability declarations
- Registration validation pipeline (parse, exports, docs, lint, capability inference, name check)
- Module loader extension to resolve `repoql:@prefix/name` specifiers
- `::module.register`, `::module.remove`, `::module.list`, `::module.check` commands
- Capability enforcement for modules (declared read-only modules cannot write)
- Concurrent access serialization through in-process lock
- Module documentation mount point for `help://` discoverability
- Tests for registration, validation, loading, capability enforcement, health checks, commands

**Does not cover:**
- Module sharing / community registry (future plan — extends accumulation beyond single repo)
- WASI runtime / WASM plugins (future — design is runtime-agnostic)

## Enables

Once the module registry exists:
- **Accumulation** — every agent session can leave behind a tool. A module written to debug a release process becomes the standard checker.
- **Discoverability** — agents find registered modules through `::module.list` and `help://modules/`
- **Trust** — capability declarations are visible and enforced before running
- **Future module sharing** can build on this foundation — publish/install adds transport, not new architecture

## Prerequisites

- **Plan: 02-capability-injection** completed — `repoql` global injection working
- **Plan: 04-write-delete** completed — modules can use `repoql.write()` and `repoql.delete()`
- Existing command framework in `src/RepoQL.Commands/` (`[CommandClass]`, `[Command]`)
- Existing module loader in `JintJavaScriptSandbox.RepoQlModuleLoader`

## North Star

An agent writes a 30-line JavaScript module, registers it with one command, and it's available in every future session. Another agent — with no memory of the first — imports it by name and it works. Registration tells you exactly what's wrong. The module listing tells you what's available and what's broken. No module silently rots.

## Done Criteria

### Module Registry Implementation

- The registry shall store modules under `.repoql/modules/` with subdirectories `src/` and `docs/`
- The registry shall maintain a `manifest.json` file as the durable store
- The in-memory registry state shall be the authority during a host session
  - When the host starts, the registry shall load `manifest.json` if it exists
  - When the host starts and no manifest exists, the registry shall initialize with an empty list

### Registration Validation

- When `Register()` is called, the following validation pipeline shall execute in order:
  1. **Parse** — load the source file and parse as ES module. When parsing fails, return error with line/column and parse error.
  2. **Exports** — verify the module exports at least one function. When no exports, return error: `"Module must export at least one function."`
  3. **Docs** — verify companion `.md` file exists at the expected path. When missing, return error naming the expected path.
  4. **Lint** — check for common problems:
     - No function calls at module scope (declarations, imports, exports only). When violated, return error naming the line and suggesting an exported init function.
     - No imports of other agent-authored modules (`repoql:@*`). When violated, return error explaining the constraint and suggesting bundled alternatives.
  5. **Capability inference** — scan source AST for `repoql.read`, `repoql.write`, `repoql.delete` member expressions. Infer `DeclaredCapabilities` from usage. The agent can override via `--capabilities read,write` flag on `::module.register`.
  6. **Hash computation** — compute SHA-256 of source file content and store as `sourceHash` in manifest. Used by health checks to detect drift (source changed after registration).
  7. **Name check** — verify identifier does not collide with a bundled module specifier. When collision, return error naming the conflict.
- When any step produces an error, registration shall fail and return all errors collected so far
- When all steps pass (possibly with warnings), the module shall be registered in the manifest

### Manifest Operations

- All manifest reads and writes shall be serialized through an in-process lock
  - When multiple agents call `Register()` concurrently through the shared gRPC host, operations shall execute sequentially
- When registering a module with an identifier that already exists, the existing entry shall be replaced (overwrite, not conflict)
- When removing a module, the manifest entry shall be deleted. Source and docs files shall be preserved unless the agent explicitly requests deletion.

### Module Loader Extension

- The module loader shall resolve `repoql:@prefix/name` specifiers by looking up the specifier in the registry
  - When the specifier is found and healthy, load the source from `.repoql/modules/src/`
  - When the specifier is found but unhealthy, return an import error with the health problem
  - When the specifier is not found, return the standard "not found" import error with available modules list
- Agent modules shall be able to import bundled modules (`repoql:yaml`, etc.)
- Agent modules shall **not** be able to import other agent modules
  - When an agent module imports another agent module, the loader shall reject with: `"Agent modules cannot import other agent modules. Import bundled modules (repoql:<name>) instead."`

### Module Caching

- Agent module source shall be cached after first load
- When a module is re-registered (overwrite), its cache entry shall be invalidated
- Cache invalidation during in-flight executions shall be safe — each execution has its own engine with modules resolved at construction time

### Capability Enforcement

- When executing in sandbox tool context, the capability provider shall enforce declared capabilities for agent modules
- **Mechanism:** When loading an agent module, the sandbox shall install a module-scoped `repoql` proxy that restricts methods based on `DeclaredCapabilities`. The proxy wraps the real global — allowed methods delegate, disallowed methods throw. This avoids needing to track which module is on the call stack at runtime.
- When a module declares `ReadOnly` and the script calls `repoql.write()` through the module's proxy:
  - Throw a JS `Error`: `"Module '@agent/changelog' declares read-only capabilities. Write is not allowed."`
- When a module declares `None` (no capabilities) and accesses any `repoql` method through the module's proxy:
  - Throw a JS `Error`: `"Module '@agent/changelog' declares no capabilities. Use the sandbox tool directly for capability access."`
- Enforcement applies per-module based on declarations, regardless of the script's configured scopes
- Bundled modules are not subject to capability enforcement (they are trusted)

### Commands

- `::module.register <identifier> [--capabilities read,write,delete]` shall:
  - Look for source at `.repoql/modules/src/<identifier>.mjs`
  - Look for docs at `.repoql/modules/docs/<identifier>.md`
  - Run the validation pipeline
  - When `--capabilities` is provided, use the specified capabilities instead of inferred ones
  - On success, output: `"Registered <identifier> (reads: yes, writes: no, deletes: no)"`
  - On failure, output each error with a specific fix suggestion
- `::module.remove <identifier>` shall:
  - Remove the module from the manifest
  - Output: `"Removed <identifier>. Source and docs preserved at .repoql/modules/"`
- `::module.list` shall:
  - List all modules: bundled (from `::sandbox.libs`) and agent-authored
  - For each agent module, show: identifier, declared capabilities, health status, registration date
  - Unhealthy modules shall be visually marked
- `::module.check` shall:
  - Run health checks on all agent-authored modules
  - Report: source exists, source parses, imports resolve, docs exist
  - Output per-module: healthy or problem description

### Health Checks

- `CheckHealth()` shall verify for each registered module:
  - Source file exists at the registered path
  - Source file hash matches `sourceHash` in manifest (detects out-of-band edits)
  - Source file parses without errors
  - All imports resolve (bundled modules still available)
  - Docs file exists at the registered path
- When any check fails, the module shall be marked unhealthy with the specific problem
- When source hash mismatches, the warning shall say: `"Source changed since registration. Re-register to update."`

### Help Integration

- Module documentation shall be discoverable via `help:///modules/@prefix/name`
- The `help://` filesystem shall mount `.repoql/modules/docs/` as an additional source alongside the embedded documentation
  - When `.repoql/modules/` exists at host startup, the mount shall be registered
  - When `.repoql/modules/` does not exist, no mount is added (no error)
- **Fallback:** if multi-mount on `help://` proves architecturally complex, module docs remain accessible via `file://` from `.repoql/modules/docs/` and via `::module.list` output. This is degraded (not queryable through `help://` search) but functional. The fallback does not satisfy the north-star promise — help:// multi-mount is the goal.

## Constraints

- **No agent-to-agent imports** — agent modules import bundled modules only. This eliminates dependency management, circular imports, and versioning. (Design: Module Loader)
- **No new DuckDB tables** — registry is file-based under `.repoql/modules/`. (Design: Constraints)
- **Concurrent access through host** — manifest operations are serialized via in-process lock. Multiple agents sharing a gRPC host cannot corrupt the manifest. (Design: Module Registry, Reviewer feedback)
- **Lint heuristic is conservative** — "no function calls at module scope" is an AST check, not a semantic analysis. False positives are fixable. (Design: Module Registry, Reviewer feedback)
- **Capability enforcement is per-module** — a module declaring read-only cannot write even if the top-level script has write scopes. (Design: Capability Enforcement)

## References

- [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Module Registry, Module Loader, Module Commands, Help Integration
- [Module Lifecycle Flow](../flows/future/sandbox/module-lifecycle.md) — registration, usage, sharing, retirement flows
- `src/RepoQL.Data.DuckDB/Sandbox/JintJavaScriptSandbox.cs` — current module loader, `RepoQlModuleLoader`
- `src/RepoQL.Commands/` — command registration pattern
- `src/RepoQL.Documentation/DocumentationFileSystem.cs` — current `help://` filesystem
- `src/RepoQL.Contracts/Configuration/RepoQlConfig.cs` — config pattern for future module settings
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Registration errors are specific and fixable. Runtime errors from modules are catchable JS exceptions.

- **Parse error** → error with line/column and parse message
- **No exports** → error explaining modules must export functions
- **Missing docs** → error naming the expected file path
- **Lint violation** → error naming the line and suggesting the fix
- **Name collision** → error naming the conflicting bundled module
- **Import of agent module** → error at registration (lint) and at runtime (loader guard)
- **Capability violation** → catchable JS `Error` naming the module and its declared capabilities
- **Unhealthy module import** → import error with the specific health problem
- **Manifest load failure** → log error (not warning — this is durable state loss), start with empty registry, emit a diagnostic annotation so `::module.check` surfaces it. Agents should be told registered modules are unavailable, not silently lose them.
