# Plan: Capability Injection

Implements: [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — Capability Injection, gRPC Contract

## Scope

**Covers:**
- `ISandboxCapabilityProvider` implementation — mediates between JS global and reader/scoper
- `repoql` global object construction and injection into the Jint engine
- `repoql.read()` capability wired to `ISandboxContentReader` via capability provider
- URI modifier parsing (`=>` syntax) routing to `Read()` vs `ReadWithModifier()`
- `repoql.log()`, `repoql.warn()`, `repoql.error()` diagnostics collection
- Statement counter pause/resume during capability calls (custom `IConstraint`)
- Dedicated execution thread (no sync context) for sandbox invocations
- Boundary error detection (enhanced `TypeError` when `repoql` accessed in SQL `js()`)
- gRPC `SandboxRequest`/`SandboxResponse` proto and service implementation
- Updated `SandboxTool` MCP handler with `read_scopes`, `write_scopes`, `delete_scopes` params
- Tests for capability injection, scope enforcement, statement counting, boundary errors, gRPC

**Does not cover:**
- `repoql.write()` and `repoql.delete()` (Plan: 04-write-delete)
- Output formatting beyond raw result + error (Plan: 03-output-formatting)
- Module registry (Plan: 05-module-registry)

## Enables

Once capability injection exists:
- **Graph access from JS** — agents can read files, symbols, trees, history, and structure from within sandbox scripts
- **Transport parity** — sandbox is available via MCP, gRPC, and SQL (without capabilities) from day one
- **Plan: 03-output-formatting** can add diagnostics rendering and footer
- **Plan: 04-write-delete** can add `write()` and `delete()` to the existing global
- **Plan: 05-module-registry** can add agent-authored modules that use the global

This is the capability foundation. Everything else layers on top.

## Prerequisites

- **Plan: 01-foundation-contracts** completed — `ISandboxContentReader`, `ISandboxScopeEnforcer`, `SandboxExecutionContext` available
- Existing `JintJavaScriptSandbox` in `src/RepoQL.Data.DuckDB/Sandbox/`
- Existing `SandboxTool` in `src/RepoQL.ConsoleApp/Tools/SandboxTool.cs`
- Existing gRPC service pattern in `src/RepoQL.Protocol/`

## North Star

An agent writes `repoql.read("file:///src/** => tree: headlines", { budget: 3000 })` inside a sandbox script and gets back structured data. No boilerplate, no imports, no capability passing. The same script in SQL `js()` gets a clear error telling it to use the sandbox tool. Statement limits don't punish reads — a script with 20 reads and light computation stays well within budget.

## Done Criteria

### repoql Global Object

- The sandbox shall construct a `repoql` JavaScript object with methods: `read`, `log`, `warn`, `error`
  - When invoked through the sandbox MCP tool or gRPC, the `repoql` global shall be present in the engine
  - When invoked through SQL `js()` / `js_test()` / `js_each()`, the `repoql` global shall be absent
- The `repoql` object shall be frozen after construction (immutable at runtime)
- The `repoql` global shall be available in all code executing in the engine — top-level scripts and imported modules alike

### ISandboxCapabilityProvider

- The capability provider shall implement `ISandboxCapabilityProvider` and mediate between the JS `repoql` global and the foundation contracts
- The `Read()` method shall:
  - Parse modifier syntax from the URI string (split on ` => ` to extract modifier and parameter)
  - When no modifier is present, call `ISandboxContentReader.Read(uri, budget)`
  - When a modifier is present, call `ISandboxContentReader.ReadWithModifier(uri, modifier, parameter, budget)`
  - The `=> question:` modifier shall be rejected with an actionable error: `"The question: modifier requires LLM inference and is not available in sandbox reads. Use explore or read tools directly."`
- The capability provider shall call scope enforcement before every operation
- The capability provider shall increment per-operation counts (`ReadCount`, `WriteCount`, `DeleteCount`) on `SandboxExecutionContext` after each call
- The capability provider shall accumulate `TokensConsumed` from read results

### repoql.read()

- The `read()` method on the JS global shall accept a URI string and an optional options object with a `budget` property
  - When no budget is provided, use a default of 5000 tokens
- The `read()` method shall delegate to `ISandboxCapabilityProvider.Read()`
- The `read()` method shall return a JS object with properties: `content` (string), `representation` (string), `tokensUsed` (number)
  - When the read fails, throw a JS `Error` with the error message from the capability provider

### repoql.log(), warn(), error()

- Each method shall accept a string message
- Each method shall append a `SandboxDiagnostic` to the execution context's diagnostics list
  - `log()` with level `"info"`, `warn()` with level `"warn"`, `error()` with level `"error"`
- Diagnostics shall not affect the script's result or error status

### Statement Counter Pause

- The sandbox shall implement a custom engine constraint that supports pause and resume
  - When a capability call begins, the constraint shall stop incrementing the statement counter
  - When the capability call completes, the constraint shall resume counting and increment by exactly one
- The constraint shall still enforce wall-clock timeout during capability calls
  - When the timeout expires during a capability call, the call shall be canceled and a timeout error returned
- The custom constraint shall replace or wrap Jint's built-in `MaxStatements` constraint

### Dedicated Execution Thread

- Sandbox execution shall run on a thread without a synchronization context
  - When the MCP tool handler or gRPC handler calls the sandbox, execution shall be dispatched to a dedicated thread via `Task.Run` or equivalent
  - The calling thread shall await the result via `Task<T>`
- This prevents deadlocks when capability calls block on async infrastructure (`IReadContentProvider.FetchGlobAsync`)

### Boundary Error Detection

- When SQL `js()` executes code that accesses `repoql.read()` (or any `repoql` property), the engine throws `TypeError`
- The error handler shall detect `TypeError` messages containing `repoql` and replace the error with:
  - Kind: `"context"`
  - Message: `"This code uses repoql.read() which requires sandbox capabilities. Use the sandbox tool instead of js() in SQL."`
  - Suggestion: `"Call the sandbox MCP tool with this code instead of using js() in a query"`

### gRPC Contract

- The `repoql.proto` shall add `SandboxRequest` and `SandboxResponse` messages as specified in the design
- The `RepoQlService` shall add an `ExecuteSandbox` RPC method
- The gRPC handler shall construct `SandboxScopes` from request fields (or defaults when empty)
- The gRPC handler shall construct `SandboxExecutionContext` with capabilities and scopes
- The gRPC handler shall call the sandbox engine and populate the response with result, diagnostics, and metadata

### Updated SandboxTool

- The MCP `sandbox` tool shall accept optional `read_scopes`, `write_scopes`, and `delete_scopes` parameters
- The MCP tool handler shall construct `SandboxExecutionContext` with the capability provider and scope enforcer
- The MCP tool handler shall pass the context to the sandbox engine
- The existing `code` and `input` parameters shall continue to work unchanged

### IJavaScriptSandbox Interface

- The `IJavaScriptSandbox` interface shall add an overload of `ExecuteScalar` that accepts a `SandboxExecutionContext`
  - When context has capabilities, inject the `repoql` global
  - When context is null or has no capabilities, do not inject the global (current behavior)
- The existing overloads without context shall continue to work unchanged (SQL UDF path)

## Constraints

- **No write/delete yet** — the `repoql` global has `read`, `log`, `warn`, `error` only. `write` and `delete` are added in Plan 04. (Design: Implementation Sequence)
- **Synchronous capability calls** — `read()` blocks the JS thread. The dedicated execution thread prevents this from blocking the host. (Design: Capability Injection)
- **Frozen global** — the `repoql` object is frozen. Scripts cannot add, remove, or modify methods. (Design: Safety)
- **No LLM inference from reads** — `ISandboxContentReader` never invokes inference. The `=> question:` modifier is not supported in sandbox reads. (Design: ISandboxContentReader)
- **Transport parity from day one** — gRPC contract ships with capability injection, not as a later addition. (Reviewer feedback)

## References

- [Sandbox Platform Design](../../designs/future/sandbox-platform.md) — capability injection, scope enforcement, statement counting, gRPC contract
- [Sandbox Execution Flow](../flows/future/sandbox/sandbox-execution.md) — stages 1-6, capability call sub-flow
- `src/RepoQL.Data.DuckDB/Sandbox/JintJavaScriptSandbox.cs` — current engine construction, `CreateEngine()`, `ExecuteCore()`
- `src/RepoQL.ConsoleApp/Tools/SandboxTool.cs` — current MCP tool handler
- `src/RepoQL.Protocol/Protos/repoql.proto` — existing gRPC service definition
- [Jint constraints](https://github.com/sebastienros/jint) — `IConstraint` interface for custom execution limits
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Errors from capability calls become catchable JS exceptions. Errors from the sandbox engine become structured error results.

- **Scope violation** → catchable JS `Error` with scope details and config command
- **Read failure** (no files matched, modifier error) → catchable JS `Error` with specific message
- **Timeout during capability call** → uncatchable timeout error (same as current timeout behavior)
- **Boundary detection** (repoql in js()) → structured error with kind `"context"` and actionable suggestion
- **Engine construction failure** → structured error with kind `"runtime"` (should not happen in practice)
