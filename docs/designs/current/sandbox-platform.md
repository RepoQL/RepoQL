# Sandbox Platform Design

## North Star

An agent should be able to build its own tools — in JavaScript, inside the sandbox — and have them remembered. Read, write, delete by URI. Register once, available forever. Same isolation whether the script reads a file or adds two numbers.

## Context

The sandbox today executes untrusted JavaScript with strict limits (memory, statements, timeout) and 22 bundled libraries. It serves two surfaces: the `sandbox` MCP tool for agents, and `js()`/`js_test()`/`js_each()` SQL functions for inline computation. Both share a single `JintJavaScriptSandbox` implementation backed by the Jint interpreter.

This design extends the sandbox from a computation engine into a programmable platform — adding capabilities (read/write/delete by URI), agent-authored modules with a registration lifecycle, and formatted output that matches RepoQL's other tools.

**Enables:**
- [Sandbox Execution Flow](../flows/future/sandbox/sandbox-execution.md)
- [Module Lifecycle Flow](../flows/future/sandbox/module-lifecycle.md)

**Built on:**
- [Sandbox North Star](../north-star/sandbox.md) — what great looks like
- Current `JintJavaScriptSandbox` — the existing implementation this extends

## Constraints

- **Jint is the runtime** — pure C#, no native dependencies. WASI is the future runtime for WASM plugin support, but this design works on Jint today and is runtime-agnostic.
- **Two surfaces are permanent** — SQL `js()` never gains capabilities. The sandbox tool gets capabilities. This boundary is architectural, not accidental.
- **Frozen schema** — no new DuckDB tables. Module registry is file-based.
- **Sandbox isolation unchanged** — memory limits, statement budgets, and timeout guarantees apply identically whether a script uses capabilities or not.
- **Single writer** — capability writes go through the filesystem layer, not DuckDB. Reads go through a data-level content API, not the tool-level read orchestrator.
- **Budget is contract** — sandbox output must respect the same token discipline as every other tool.
- **Runs on a laptop** — no cloud dependencies for any sandbox feature.
- **Repo-rooted access** — read access is bounded to the repository and its indexed content, not the entire machine.

---

## Components

```
┌──────────────────────────────────────────────────────────────────┐
│                         Callers                                   │
│  SandboxTool (MCP)  |  JavaScriptSandboxUdf (SQL)  |  gRPC       │
└──────────────────────────────────────────────────────────────────┘
             │                              │
             │ code + input + scopes        │ code + input (no scopes)
             ▼                              ▼
┌──────────────────────────────────────────────────────────────────┐
│                    JintJavaScriptSandbox                          │
│  - Parse + cache AST                                              │
│  - Resolve imports (bundled + agent-authored)                     │
│  - Build per-call engine on dedicated thread (no sync context)    │
│  - Inject repoql global (sandbox tool only)                       │
│  - Execute with limits                                            │
│  - Collect result + diagnostics                                   │
└──────────────────────────────────────────────────────────────────┘
        │              │              │              │
        ▼              ▼              ▼              ▼
┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐
│ Capability │ │   Module   │ │   Module   │ │   Output   │
│  Provider  │ │  Registry  │ │   Loader   │ │ Formatter  │
│            │ │            │ │            │ │            │
│ - read()   │ │ - register │ │ - resolve  │ │ - result   │
│ - write()  │ │ - list     │ │ - bundled  │ │ - diags    │
│ - delete() │ │ - remove   │ │ - agent    │ │ - footer   │
│ - scoping  │ │ - validate │ │ - cache    │ │ - errors   │
└────────────┘ └────────────┘ └────────────┘ └────────────┘
        │                            │
        ▼                            ▼
┌────────────┐              ┌────────────────┐
│   Scope    │              │   .repoql/     │
│  Enforcer  │              │   modules/     │
│            │              │                │
│ - read     │              │ - manifest.json│
│ - write    │              │ - src/         │
│ - delete   │              │ - docs/        │
└────────────┘              └────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────┐
│              Existing Infrastructure                │
│  ISandboxContentReader  |  IWritableFileSystem      │
│  (new, data-level)        (new, write+delete)       │
│  IReadContentProvider   |  IMultiFileSystem          │
│  (existing, read-only)    (existing, read-only)      │
└────────────────────────────────────────────────────┘
```

---

## Contracts

### ISandboxContentReader

The capability provider reads through a **data-level content API**, not the tool-level `ReadOrchestrator`. The read orchestrator appends footers, handles budget-overflow consent, and can invoke LLM inference — none of which belong in a programmatic read from JS.

```csharp
/// <summary>
/// Purpose: Provide data-level content access for sandbox capability calls.
/// Complexity: Wraps IReadContentProvider for document fetching and representation
/// selection without tool-level formatting, consent flows, or inference.
/// </summary>
public interface ISandboxContentReader
{
    /// <summary>
    /// Read content at a URI. Supports globs and fragments.
    /// Returns raw content with representation metadata.
    /// Budget controls representation level (full/structure/headline).
    /// </summary>
    SandboxReadResult Read(string uri, int tokenBudget);

    /// <summary>
    /// Read with a modifier (tree, structure, history, blame, etc).
    /// Returns rendered modifier output as text.
    /// </summary>
    SandboxReadResult ReadWithModifier(string uri, string modifier, string? parameter, int tokenBudget);
}

public sealed record SandboxReadResult(
    bool Success,
    string? Content,
    string? Representation,  // "full", "structure", "headline", or modifier name
    int TokensUsed,
    string? Error);
```

This delegates to `IReadContentProvider.FetchGlobAsync` for document fetching and uses `SelectRepresentation`-style logic for budget decisions — but never invokes LLM inference, never appends footers, and never triggers consent flows.

### IWritableFileSystem

The current `IVirtualFileSystem` is read-only. Writes and deletes need a separate contract.

```csharp
/// <summary>
/// Purpose: Write and delete operations for sandbox capability calls.
/// Complexity: Scheme-specific write semantics. Only file:// supports writes.
/// Parent directory creation on write. Atomic write via temp-file-then-rename.
/// </summary>
public interface IWritableFileSystem
{
    /// <summary>Write UTF-8 content to a URI. Creates parent directories.</summary>
    void Write(RepoUri uri, string content);

    /// <summary>Delete content at a URI.</summary>
    void Delete(RepoUri uri);

    /// <summary>Whether this filesystem supports writes for the given scheme.</summary>
    bool CanWrite(string scheme);
}
```

Only the `file://` scheme gets an `IWritableFileSystem` implementation. Other schemes (help://, github://) do not support writes — attempts produce an actionable error naming the constraint.

### ISandboxCapabilityProvider

```csharp
/// <summary>
/// Purpose: Provide read/write/delete capabilities to sandbox scripts.
/// Complexity: Mediates between the JS engine and the content reader / writable
/// filesystem with URI scope enforcement.
/// </summary>
public interface ISandboxCapabilityProvider
{
    /// <summary>
    /// Read content at a URI. Supports modifiers via => syntax.
    /// Returns structured result for JS consumption.
    /// </summary>
    SandboxReadResult Read(string uri, int? tokenBudget);

    /// <summary>
    /// Write content to a URI within configured write scopes.
    /// </summary>
    void Write(string uri, string content);

    /// <summary>
    /// Delete content at a URI within configured delete scopes.
    /// </summary>
    void Delete(string uri);
}
```

### ISandboxScopeEnforcer

```csharp
/// <summary>
/// Purpose: Validate URIs against configured scopes before capability execution.
/// Complexity: Pattern matching against scope lists per operation type, using
/// existing UriPatternMatcher.
/// </summary>
public interface ISandboxScopeEnforcer
{
    void EnforceRead(string uri);
    void EnforceWrite(string uri);
    void EnforceDelete(string uri);
}

public sealed class SandboxScopeException(
    string operation,
    string uri,
    IReadOnlyList<string> allowedScopes) : Exception(
    $"{operation} denied for '{uri}'. Allowed scopes: {string.Join(", ", allowedScopes)}. "
    + $"To change: ::config.set sandbox.{operation.ToLower()}_scopes <pattern>");
```

### IModuleRegistry

```csharp
/// <summary>
/// Purpose: Manage agent-authored module registration, validation, and discovery.
/// Complexity: File-based manifest under .repoql/modules/. Validation pipeline
/// at registration time. Concurrent access serialized through the host.
/// </summary>
public interface IModuleRegistry
{
    ModuleRegistrationResult Register(string identifier, string sourcePath, string docsPath);
    bool Remove(string identifier);
    IReadOnlyList<RegisteredModule> List();
    string? LoadSource(string specifier);
    IReadOnlyList<ModuleHealthResult> CheckHealth();
}

public sealed record RegisteredModule(
    string Identifier,
    string Specifier,
    string SourcePath,
    string DocsPath,
    string SourceHash,
    DeclaredCapabilities Capabilities,
    DateTimeOffset RegisteredAt,
    bool IsHealthy);

public sealed record DeclaredCapabilities(
    bool Reads,
    bool Writes,
    bool Deletes)
{
    public static readonly DeclaredCapabilities None = new(false, false, false);
    public static readonly DeclaredCapabilities ReadOnly = new(true, false, false);
}

public sealed record ModuleRegistrationResult(
    bool Success,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record ModuleHealthResult(
    string Identifier,
    bool IsHealthy,
    string? Problem);
```

Capability declarations are required at registration. The registration command inspects which `repoql.*` methods the module source references and infers declarations. The agent can override. At runtime, the capability provider enforces declared capabilities — a module that declares read-only has `repoql.write()` and `repoql.delete()` throw even if the script has write scopes.

### SandboxExecutionContext

```csharp
/// <summary>
/// Purpose: Carry per-execution state through a sandbox invocation.
/// Complexity: Bundles scopes, capability provider, diagnostics, and execution
/// metadata into a single object threaded through the pipeline.
/// </summary>
public sealed class SandboxExecutionContext
{
    public ISandboxCapabilityProvider? Capabilities { get; init; }
    public ISandboxScopeEnforcer? ScopeEnforcer { get; init; }
    public SandboxScopes Scopes { get; init; }
    public List<SandboxDiagnostic> Diagnostics { get; } = [];
    public int CapabilityCallCount { get; set; }
    public int TokensConsumed { get; set; }
}

public sealed record SandboxScopes(
    IReadOnlyList<string> ReadScopes,
    IReadOnlyList<string> WriteScopes,
    IReadOnlyList<string> DeleteScopes);

public sealed record SandboxDiagnostic(
    string Level,    // "info", "warn", "error"
    string Message);
```

### Expanded SandboxSettings

```csharp
public sealed class SandboxSettings
{
    // Existing
    public bool? Enabled { get; set; }
    public int? TimeoutMs { get; set; }
    public int? MemoryLimitBytes { get; set; }
    public int? MaxStatements { get; set; }

    // New: capability scopes
    public List<string>? ReadScopes { get; set; }
    public List<string>? WriteScopes { get; set; }
    public List<string>? DeleteScopes { get; set; }
}
```

**Default scopes** (repo-rooted):
- Read: all mounted filesystems — the repo's `file://` tree, `help://`, and any `github://` imports. Bounded to indexed content, not the entire machine.
- Write: `[".repoql/tmp/**"]` — scratch space only.
- Delete: matches write scopes.

The repo root is known at host startup (`RepoLocator.FindRepoRoot`). Read scopes are derived from registered VFS mounts rather than hardcoded globs, ensuring they track what's actually indexed.

---

## Design

### Capability Injection

The `repoql` global is a frozen JavaScript object installed on the engine before script execution. It exists in all code running through the sandbox tool — top-level scripts and imported modules alike. It is absent in SQL `js()`.

```javascript
// Available in sandbox tool scripts AND in modules imported by them:
const content = repoql.read("file:///src/Foo.cs");
const tree = repoql.read("file:///src/** => tree: headlines", { budget: 3000 });

repoql.write("file:///.repoql/tmp/report.csv", csvContent);
repoql.delete("file:///.repoql/tmp/old-report.csv");

// Diagnostics
repoql.log("Processing 42 files...");
repoql.warn("Skipped 3 files with parse errors");
repoql.error("Failed to resolve dependency graph");
```

**Why global, not parameter:** The north star says "desire paths, not tutorials." An agent's first instinct when importing a capability-using module is to call the function without passing anything extra. If `repoql` is a parameter, every import becomes boilerplate: `analyze(repoql, input)`. With a global, the agent writes `analyze(input)` and it works — the desire path.

Modules that want their pure functions to also work in SQL `js()` can guard access:

```javascript
// Works in both contexts:
export function parseConfig(text) { /* pure computation */ }

// Works only in sandbox (global present):
export function analyzeRepo(pattern) {
    const content = repoql.read(pattern);
    return parseConfig(content);
}
```

**Execution threading:** Sandbox execution runs on a dedicated thread without a synchronization context. This prevents deadlocks when capability calls block on async infrastructure (`ReadOrchestrator`, filesystem I/O). The calling thread (gRPC/MCP request handler) awaits a `Task` that the sandbox thread resolves on completion.

**Statement counting:** Before each capability call, the engine's statement constraint is suspended and resumed after completion with a single increment. The mechanism is runtime-specific — on Jint, this requires a custom `IConstraint` implementation that exposes pause/resume. The design is runtime-agnostic; the important semantic is that capability calls count as exactly one statement regardless of internal complexity.

**Boundary detection:** When SQL `js()` executes code that tries to access `repoql.read()`, it hits `TypeError: Cannot read properties of undefined`. The error handler detects this pattern and replaces it with: *"This module requires sandbox capabilities (read/write/delete). Use the sandbox tool instead of js() in SQL."*

### Scope Enforcement

Every capability call passes through the scope enforcer before execution.

Scopes are URI glob patterns. The enforcer uses the existing `UriPatternMatcher` (already in `RepoQL.Contracts`) for matching. Scope violations throw `SandboxScopeException`, which becomes a catchable JS exception in the script.

The exception message names the scope that would allow the operation — actionable, not just denied.

```
// Denied:
"Write denied for 'file:///src/Foo.cs'.
 Allowed write scopes: .repoql/tmp/**
 To change: ::config.set sandbox.write_scopes file:///src/**"
```

### Module Registry

Agent-authored modules live under `.repoql/modules/`:

```
.repoql/
  modules/
    manifest.json          # Registry: identifier → source/docs/hash/capabilities/timestamp
    src/
      @agent/changelog.mjs
      @agent/depcheck.mjs
    docs/
      @agent/changelog.md
      @agent/depcheck.md
```

**Specifier format:** `repoql:@prefix/name`. The `@` prefix distinguishes agent-authored from bundled. Bundled modules have no prefix (`repoql:yaml`). Agent modules cannot shadow bundled names — registration rejects conflicts.

**Manifest format:**

```json
[
  {
    "identifier": "@agent/changelog",
    "specifier": "repoql:@agent/changelog",
    "sourcePath": "src/@agent/changelog.mjs",
    "docsPath": "docs/@agent/changelog.md",
    "sourceHash": "sha256:...",
    "capabilities": { "reads": true, "writes": false, "deletes": false },
    "registeredAt": "2026-03-14T10:00:00Z"
  }
]
```

**Concurrency:** Manifest reads and writes are serialized through the gRPC host. Multiple agents sharing a host cannot corrupt the manifest because all registry operations go through a single service instance with a lock. The manifest file is the durable store; the in-memory state is the authority during a host session.

**Registration validation pipeline:**
1. Parse source — must be valid ES module syntax, no errors
2. Check exports — must export at least one function
3. Check docs — companion `.md` must exist
4. Lint — no function calls at module scope (declarations, imports, and exports only), no imports of other agent modules
5. Capability inference — scan for `repoql.read`, `repoql.write`, `repoql.delete` references, infer `DeclaredCapabilities`
6. Name check — no collision with bundled modules

Each violation produces a specific error with the fix. Errors block registration. Warnings (e.g., accessing `repoql` but not declaring any specific methods) allow registration with advisory.

**Lint heuristic detail:** "No side effects at module level" means: no function calls, `new` expressions, or assignment to non-declared variables at the top level of the module. `const config = { ... }` is allowed. `const config = buildConfig()` is not. This is a conservative static check on the AST — it will have false positives (e.g., `Object.freeze({})`) but the cost of a false positive is a fixable error message, not a blocked workflow. Agents can restructure into an exported init function.

### Module Loader

The existing `RepoQlModuleLoader` is extended to resolve both bundled and agent-authored modules:

1. **Bundled** (`repoql:yaml`): Resolved from embedded resources (current behavior, unchanged)
2. **Agent-authored** (`repoql:@prefix/name`): Resolved from `.repoql/modules/src/` via the registry

Agent modules can import bundled modules. They cannot import other agent modules — this prevents dependency chains, versioning complexity, and circular imports. The constraint is enforced at registration (lint) and at resolution (runtime guard).

Module source is cached after first load. Cache invalidation happens on re-registration. Invalidation during in-flight executions is safe — each execution gets its own engine with modules resolved at construction time.

### Capability Provider

The capability provider mediates between the JS engine and RepoQL infrastructure:

**Read** routes through `ISandboxContentReader`, which wraps `IReadContentProvider` for document fetching and applies representation selection based on budget. It does **not** use `ReadOrchestrator` — the orchestrator is a tool-level surface with presentation concerns (footers, consent, inference) that don't belong in programmatic reads. The content reader returns raw structured data.

```javascript
// Returns { content: "...", representation: "full", tokensUsed: 150 }
const result = repoql.read("file:///src/Foo.cs");

// With modifier
const tree = repoql.read("file:///src/** => tree: headlines", { budget: 3000 });

// Read failure is a catchable JS error
try {
    repoql.read("file:///nonexistent.cs");
} catch (e) {
    repoql.warn("File not found: " + e.message);
}
```

**Memory accounting:** Read results are deserialized into the JS engine's heap. Due to .NET string encoding (UTF-16) and engine value wrapping, the actual memory cost is approximately 3-5x the content byte size. A 10KB file may consume 30-50KB of the engine's memory budget. Scripts that read many large files will hit memory limits sooner than naive content-size estimates suggest. The error message advises reading less content or increasing the memory limit.

**Write** routes through `IWritableFileSystem`. Only the `file://` scheme supports writes. The implementation resolves the URI to a filesystem path via `FileUriPathResolver`, creates parent directories if needed, and writes content as UTF-8 text via atomic temp-file-then-rename.

**Delete** routes through `IWritableFileSystem`. Same scheme restriction and path resolution as write.

**Capability enforcement for modules:** When executing a module with declared capabilities, the provider wraps itself in a restricting proxy. If a module declares `ReadOnly`, calls to `write()` or `delete()` through the `repoql` global throw a specific error: *"Module '@agent/changelog' declares read-only capabilities. Write is not allowed."* This enforcement happens regardless of the script's configured scopes.

### Help Integration for Module Docs

Module documentation becomes queryable through `help://` by mounting `.repoql/modules/docs/` as an additional source in the help filesystem.

The `help://` scheme is backed by `DocumentationFileSystem` (embedded resources). A second VFS mount point is added for `.repoql/modules/docs/`, making agent module docs appear alongside bundled docs. URIs take the form `help:///modules/@agent/changelog`.

This requires the `help://` filesystem to support multiple backing stores — one embedded (bundled docs), one file-based (module docs). The mount is registered at host startup when `.repoql/modules/` exists.

### Output Formatting

The sandbox tool produces output matching RepoQL's standard shape:

```
--- result ---
{ "packages": [...], "outdated": 3 }

⚠ Skipped 3 files with parse errors
ℹ Processed 42 files

[sandbox | 847ms | 3 reads, 1 write | budget: 5000 tok used]
```

Structure:
- **Result section** — serialized return value (JSON for objects, plain text for strings/numbers)
- **Diagnostics** — `repoql.log()`, `repoql.warn()`, `repoql.error()` messages, inline with severity markers
- **Footer** — execution metadata: timing, capability call counts, token usage

For errors, the existing structured error format is preserved (`{ "error": { "kind": "...", "message": "...", "suggestion": "..." } }`). This is already consistent with other tools.

SQL `js()` output is unchanged — raw values, no formatting. The formatting layer lives in the MCP tool handler / gRPC handler, not the sandbox engine.

### gRPC Contract

The sandbox gets a gRPC surface for transport parity:

```protobuf
message SandboxRequest {
  string code = 1;
  string input = 2;
  bool input_is_null = 3;
  repeated string read_scopes = 4;
  repeated string write_scopes = 5;
  repeated string delete_scopes = 6;
}

message SandboxResponse {
  bool success = 1;
  string error = 2;
  string rendered_output = 3;
  string result_json = 4;
  repeated SandboxDiagnostic diagnostics = 5;
  int32 capability_calls = 6;
  int32 tokens_consumed = 7;
  double elapsed_ms = 8;
}

message SandboxDiagnostic {
  string level = 1;
  string message = 2;
}
```

The gRPC contract is introduced alongside capability injection (step 1) so that every subsequent feature gets transport parity for free.

### Module Commands

Module lifecycle is managed through the command system:

```
::module.register @agent/changelog     Register a module from .repoql/modules/src/
::module.remove @agent/changelog       Deregister and optionally delete source
::module.list                          List all modules (bundled + agent-authored) with health
::module.check                         Run health checks on all agent modules
```

These complement `::sandbox.libs` (which lists bundled libraries) by adding agent module management. Both surfaces are queryable through `help://`.

---

## Cross-Cutting Concerns

### Diagnostics

Scripts emit diagnostics via `repoql.log()`, `repoql.warn()`, `repoql.error()`. These are collected in the `SandboxExecutionContext` during execution and included in the formatted output. Diagnostics are informational — they don't affect the result or error status.

Unhandled exceptions from capability calls bubble as JS errors. If caught by the script, they don't appear in diagnostics. If uncaught, they become the sandbox error. There is no separate tracking of handled exceptions — the script handles them or it doesn't.

### Memory Accounting

Capability results are deserialized into the JS engine's heap. Due to value representation overhead (UTF-16 strings, engine value wrappers, object structures), the actual memory cost is approximately **3-5x the raw content byte size**. This means:
- A `read()` returning 10KB of content costs ~30-50KB of memory budget
- Scripts that read many files accumulate heap pressure faster than content size suggests
- Memory limit violations produce the standard memory error with a suggestion to read less or increase the limit

The memory budget is shared across the entire execution — script allocations and capability results compete for the same pool.

### Timeout

Capability calls share the wall-clock timeout with script execution. A script with a 2-second timeout that spends 1.5 seconds on reads has 0.5 seconds for computation. The timeout is non-negotiable and applies uniformly.

### Module Caching

Bundled modules are parsed once at sandbox construction (current behavior). Agent modules are parsed on first use and cached until re-registration clears the cache entry. The module cache has the same 1024-entry limit as the program cache. Each execution gets its own engine — cache invalidation from concurrent re-registration cannot corrupt an in-flight execution.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Jint (current) | WASI now | Jint works today, covers JS-only modules. WASI is the right future runtime for WASM plugins — design is runtime-agnostic to allow migration. |
| `repoql` as global | Capability parameter passing | Desire path: `analyze(input)` not `analyze(repoql, input)`. Modules that want SQL `js()` compatibility guard access with `typeof repoql !== 'undefined'`. |
| Synchronous capabilities | Async/Promise-based | Jint is single-threaded. Sync is simpler, no Promise machinery needed. Dedicated execution thread prevents deadlocks. |
| `ISandboxContentReader` (data-level) | `ReadOrchestrator` (tool-level) | The orchestrator has presentation concerns (footers, consent, inference) that don't belong in programmatic reads from JS. A data-level reader returns raw content. |
| `IWritableFileSystem` (new contract) | Extending `IVirtualFileSystem` | Read and write are different concerns. Not all schemes support writes. Adding write to the read interface would force stubs on help://, github://, etc. |
| File-based registry | DuckDB table | Frozen schema constraint. File-based is portable and inspectable. |
| No agent-to-agent imports | Dependency graph | Eliminates versioning, circular deps, and diamond dependency problems. Bundled imports cover composition needs. |
| Shared timeout | Independent timeouts | Simpler reasoning. A script's total cost is bounded by one number. |
| URI scopes (glob patterns) | Fine-grained ACLs | Reuses existing `UriPatternMatcher`. Glob patterns are the lingua franca of RepoQL. |
| Repo-rooted read scopes | Machine-wide `file://**` | `file://**` escapes the repository. Scopes derived from mounted VFS boundaries match what's indexed. |
| Diagnostics as info-only | Diagnostic-affects-result | Diagnostics are for observability, not control flow. Keeps the result channel clean. |

## Alternatives Considered

**Capability as parameter, not global:** Modules accept capabilities as a function parameter instead of using a global. Rejected: the agent's first instinct when calling an imported function is to not pass extra arguments. With parameter passing, every capability-using import requires boilerplate the agent must learn. The global is the desire path. Modules that want dual-surface compatibility (sandbox + SQL `js()`) guard access with `typeof repoql !== 'undefined'`.

**Read via ReadOrchestrator:** Route `repoql.read()` through the existing tool-level read orchestrator. Rejected: the orchestrator appends footers, handles budget-overflow consent, and can invoke LLM inference. These are presentation and tool-interaction concerns. Programmatic reads from JS need raw data with structured success/failure — a data-level API below the orchestrator.

**Query capability (repoql.query):** A `query()` method that runs SQL from within JS. Rejected: re-entrant DuckDB calls from within a UDF invocation risk deadlocks and complicate the single-writer guarantee. Read covers the same data through URIs and views.

**Write to DuckDB:** Capability writes that insert into the graph. Rejected: violates single writer constraint. File writes to `.repoql/tmp/` are sufficient for output. If graph writes are needed, they should go through the indexing pipeline (write a file, let the pipeline index it).

**Module dependencies:** Allow agent modules to import other agent modules. Rejected: introduces version management, diamond dependencies, circular imports, and load-order complexity. The constraint (agent modules import bundled only) eliminates an entire class of problems. If two modules need shared logic, extract it into a bundled library contribution.

**Extend IVirtualFileSystem with write support:** Add `Write()` and `Delete()` to the existing read-only VFS interface. Rejected: forces every VFS implementation (help://, github://, embedded) to stub out write methods. A separate `IWritableFileSystem` keeps concerns clean and only the file:// implementation needs to provide it.

## Risks

| Risk | Mitigation |
|------|------------|
| Sync-over-async deadlock | Sandbox execution runs on a dedicated thread without synchronization context. Capability calls block safely. |
| Memory exhaustion from reads | 3-5x multiplier documented. Error message explains cause and suggests fixes. |
| Manifest corruption under concurrent agents | All registry operations serialized through gRPC host. File writes protected by in-process lock. |
| Scope misconfiguration allows unintended writes | Default write scope is `.repoql/tmp/**` only. Broader scopes require explicit `::config.set`. |
| Agent modules reference renamed/removed bundled modules after upgrade | Health check detects broken imports. Module listing shows unhealthy status. |
| Statement counter manipulation | Counter pause/resume is internal to the host runtime, not accessible from JS. Requires a custom engine constraint implementation (Jint: custom `IConstraint`). |
| Read scope escapes repository | Scopes derived from mounted VFS boundaries. No hardcoded `file://**` — only what's indexed is readable. |
| `help://` integration for module docs | Requires `help://` filesystem to support a second mount point. If deferred, module docs are still readable via `file://` from `.repoql/modules/docs/`. |

## Extension Points

- **ISandboxCapabilityProvider** — new capabilities can be added by extending the interface and constructing a richer `repoql` global with additional methods
- **ISandboxContentReader** — the read abstraction can evolve independently of the tool-level `ReadOrchestrator`
- **IWritableFileSystem** — new writable schemes can be added without changing the capability provider
- **IModuleRegistry** — registry implementation is behind an interface; could be backed by a service for team sharing
- **Module linting** — lint rules are a list; new rules can be added without changing the registration flow
- **Output formatting** — formatter is a separate concern from execution; can evolve independently
- **Scope patterns** — scope enforcement uses `UriPatternMatcher`; any URI scheme RepoQL gains becomes scopeable automatically
- **gRPC contract** — scope parameters in the request allow per-call scope override for different trust levels
- **DeclaredCapabilities** — capability enforcement for modules can grow as the capability surface grows

---

## Implementation Sequence

This design is implementable incrementally. Each step delivers standalone value.

**Prerequisites** — before building features, establish the foundational contracts:
- `ISandboxContentReader` (data-level read API, wrapping `IReadContentProvider`)
- `IWritableFileSystem` (write + delete for `file://` scheme)
- Repo-rooted scope model (scopes derived from VFS mounts)

1. **Capability injection + gRPC contract** — `repoql` global with `read()` only. Scope enforcement. Statement counter pause. Dedicated execution thread. gRPC `SandboxRequest`/`SandboxResponse`. This unlocks graph access from JS with transport parity from day one.

2. **Output formatting** — result + diagnostics + footer in the sandbox tool response. Aligns sandbox output with other tools.

3. **Write + delete** — `repoql.write()` and `repoql.delete()` with scope enforcement. Default `.repoql/tmp/**` scratch space. `IWritableFileSystem` implementation for `file://`.

4. **Module registry** — `.repoql/modules/`, manifest, registration validation, capability declarations, `::module.*` commands. Agent-authored modules become persistent.

5. **Module docs + help integration** — mount `.repoql/modules/docs/` as help:// source. Module docs become queryable.

6. **Module sharing** — publish/install, community registry. Extends accumulation beyond a single repo.

Each step can be planned, implemented, and shipped independently. Steps 1-3 are the capability foundation. Step 4 is the accumulation mechanism. Steps 5-6 are ecosystem.
