---
description: Plan for adding a sandboxed JavaScript execution surface to RepoQL via Jint.
tags: [plan, sandbox, javascript, jint, udf, tool]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: JavaScript Sandbox

Implements: Research findings in `docs/research/js-sandbox-runtimes.md` and `docs/research/js-sandbox-libraries.md`.

## Scope

**Covers:**
- Jint engine integration with hardened security configuration
- Engine pooling and library preloading
- `repoql:<name>` module resolution for bundled libraries
- Three UDFs: `js()`, `js_test()`, `js_each()`
- MCP `sandbox` tool for agent safe code execution
- `Sandbox` config section in `RepoQlConfig`
- `::sandbox.libs` command
- Bundled library set (Tiers 1-3 from library research)
- `help://` documentation for sandbox usage and library reference
- Tests for engine, UDFs, library compatibility, and tool

**Does not cover:**
- User-added library management (future: `::sandbox.lib.add[name, path]`, catalog, manifests)
- WASI/Extism runtime (future: if stronger isolation needed)
- `repoql.query()` / `repoql.read()` reentrancy from JS (future: v2 tool-context feature)
- Nested database calls from UDFs
- Hot library reload (restart required for config changes)

## Enables

Once the sandbox exists:
- **JS-in-SQL** — agents and users write per-row transforms, filters, and explode operations that SQL alone cannot express (YAML parsing, semver ranges, fuzzy search, text diffing, object hashing)
- **Safe agent code execution** — agents run data transforms without Bash permission prompts, because the sandbox provably cannot access filesystem, network, or processes
- **MCP + JS composition** — JS UDFs combine with MCP server results in SQL, providing predicates and transforms across the entire query surface (fuzzy joins, version comparison, format parsing on external data)
- **Extensible query vocabulary** — each bundled library adds operations to SQL that DuckDB lacks natively, without extending the C# UDF surface

## Prerequisites

- [Jint](https://github.com/sebastienros/jint) NuGet package `Jint` — add to `Directory.Packages.props`
- Existing UDF framework in `src/RepoQL.Data.DuckDB/UdfFramework/` (auto-discovery via `[UdfClass]`, `[ScalarUdf]`, `[StructuredUdf]`)
- Existing command framework in `src/RepoQL.Commands/` (auto-discovery via `[CommandClass]`, `[Command]`)
- Existing MCP tool pattern in `src/RepoQL.ConsoleApp/Tools/` (`[McpServerToolType]`, `[McpServerTool]`)
- Existing config pattern in `src/RepoQL.Contracts/Configuration/RepoQlConfig.cs` (`[Setting]` attributes, `SettingRegistry` auto-discovery)
- Library ESM bundles produced by esbuild (build-time step or pre-committed artifacts)

## North Star

An agent's first instinct works. `js('yaml.load(input).spec.replicas', content)` just works — no setup, no configuration, no imports to remember. The library is there. The sandbox is safe. The budget is respected. If the script fails, the error message tells the agent exactly what went wrong and how to fix it.

## Done Criteria

### JintEngine Service

- The JintEngine shall create Jint `Engine` instances with hardened configuration:
  - `LimitMemory(4_000_000)` — 4 MB heap
  - `TimeoutInterval(TimeSpan.FromSeconds(2))` — wall-clock timeout
  - `MaxStatements(10_000)` — statement count cap
  - `LimitRecursion(64)` — JS recursion depth
  - `MaxExecutionStackCount` set to `500` — prevents `StackOverflowException`
  - `MaxArraySize` set to `100_000` — prevents array bombs
  - `RegexTimeout` set to `TimeSpan.FromSeconds(2)` — ReDoS protection
  - `StringCompilationAllowed` set to `false` — blocks `eval()` and `Function()` constructor
  - `Strict()` mode enabled
  - `AllowClr()` never called
- The JintEngine shall pre-parse user scripts via `Engine.PrepareScript()` and cache the prepared result for reuse across rows within a single query invocation
- The JintEngine shall register all enabled bundled libraries as in-memory ESM modules via `engine.Modules.Add("repoql:<name>", source)`
- The JintEngine shall support `CancellationToken` for external abort
- The JintEngine shall be registered as a singleton service in DI
  - When constructing, the JintEngine shall load and validate all enabled library bundles from embedded resources
  - If a library bundle fails to parse, the JintEngine shall log a warning and continue without that library

### Module Resolution

- The module resolver shall resolve `repoql:<name>` imports to preloaded library bundles
- The module resolver shall reject all other import specifiers with an actionable error: `"Import '<specifier>' not found. Available libraries: <list>. Use repoql:<name> to import."`
  - When the specifier is a bare name matching a known library (e.g., `"yaml"` instead of `"repoql:yaml"`), the error shall suggest the correct form
- The module resolver shall resolve `import yaml from "repoql:yaml"` and `import { load } from "repoql:yaml"` equivalently

### js() Scalar UDF

- The `js` UDF shall accept `(expression VARCHAR, input VARCHAR)` and return `VARCHAR`
- The `js` UDF shall evaluate the expression with the input bound as `input` in the JS scope
  - When the expression returns a JS object or array, the result shall be JSON-serialized
  - When the expression returns a primitive, the result shall be the string representation
  - When the expression returns `null` or `undefined`, the result shall be SQL `NULL`
- The `js` UDF shall be non-pure (registered with `IsPure = false`)
- When the expression uses `import`, the UDF shall support ESM module syntax via prepared module evaluation
  - When the import specifier is not a `repoql:` scheme, the error shall be actionable

### js_test() Scalar UDF

- The `js_test` UDF shall accept `(expression VARCHAR, input VARCHAR)` and return `VARCHAR` representing a boolean
  - When the expression evaluates to a truthy value, return `'true'`
  - When the expression evaluates to a falsy value, return `'false'`
- The generated SQL macro shall cast the result to `BOOLEAN` so it integrates naturally in `WHERE` clauses

### js_each() Structured UDF

- The `js_each` UDF shall accept `(script VARCHAR, input VARCHAR)` and return rows
- The script shall return an array (or iterable) of objects
  - When the script returns a non-array, wrap it in a single-element array
  - When the script returns an empty array, return zero rows
- Each object in the returned array shall become a row with columns derived from object keys
- The column schema shall be determined from the first element of the returned array
  - When the array is empty, return a single `value` column with zero rows

### Sandbox MCP Tool

- The `sandbox` tool shall accept `code` (JS source) and optional `input` (JSON string) parameters
- The `sandbox` tool shall return the script's result as JSON
- The `sandbox` tool shall use the same JintEngine service and hardened configuration as the UDFs
- When the script exceeds time or memory limits, the tool shall return a structured error with kind, message, and suggestion
- The tool description shall list available libraries so agents know what they can import

### Configuration

- The `Sandbox` section shall be added to `RepoQlConfig` with these settings:
  - `sandbox.enabled` — `bool`, default `true`
  - `sandbox.timeout_ms` — `int`, default `2000`
  - `sandbox.memory_limit_bytes` — `int`, default `4000000`
  - `sandbox.max_statements` — `int`, default `10000`
- When `sandbox.enabled` is `false`, the UDFs and tool shall return an error: `"JS sandbox is disabled. Enable with ::config.set sandbox.enabled true"`

### Commands

- The `::sandbox.libs` command shall list all bundled libraries with name, version, size, one-line description, and enabled status
- The output shall include an example import for each library

### Library Bundling

- Each library shall be bundled as a single-file ESM module via esbuild (targeting ES2023, no external deps, no Node API shims)
- Bundled libraries shall be embedded as resources in the `RepoQL.Data.DuckDB` assembly (or a dedicated `RepoQL.Sandbox` project)
- The bundle build shall be a reproducible script (`scripts/bundle-sandbox-libs.sh` or equivalent)
- The following libraries shall be bundled and enabled by default:

**Tier 1 — DuckDB gap fillers:**

| Library | npm package | Registered as |
|---------|------------|---------------|
| js-yaml | `js-yaml` | `repoql:yaml` |
| smol-toml | `smol-toml` | `repoql:toml` |
| json5 | `json5` | `repoql:json5` |
| txml | `txml` | `repoql:xml` |
| ini | `ini` | `repoql:ini` |
| papaparse | `papaparse` | `repoql:csv` |
| semver | `semver` | `repoql:semver` |
| diff | `diff` | `repoql:diff` |
| microdiff | `microdiff` | `repoql:microdiff` |
| ohash | `ohash` | `repoql:hash` |
| fuse.js | `fuse.js` | `repoql:fuse` |
| ignore | `ignore` | `repoql:ignore` |

**Tier 2 — Platform API compensation:**

| Library | npm package | Registered as |
|---------|------------|---------------|
| js-base64 | `js-base64` | `repoql:base64` |
| dayjs | `dayjs` | `repoql:dayjs` |
| change-case | `change-case` | `repoql:case` |
| mustache | `mustache` | `repoql:mustache` |

**Tier 3 — Tool-context productivity:**

| Library | npm package | Registered as |
|---------|------------|---------------|
| radash | `radash` | `repoql:radash` |
| picomatch | `picomatch` | `repoql:glob` |
| toposort | `toposort` | `repoql:toposort` |
| front-matter | `front-matter` | `repoql:frontmatter` |
| parse-diff | `parse-diff` | `repoql:parsediff` |

- Each library bundle shall be smoke-tested in Jint during CI

### Documentation

- A `help://sandbox/` section shall document:
  - Quickstart with examples for `js()`, `js_test()`, `js_each()`, and the sandbox tool
  - Library reference with import names, key functions, and one example per library
  - Security model (what the sandbox can and cannot access)
  - Limits and configuration
  - Error messages and troubleshooting
- The `js()` UDF description (shown to agents via tool discovery) shall include 2-3 inline examples

### Tests

- Engine tests shall verify:
  - Hardened config rejects `eval()`, CLR access, excessive memory, excessive time
  - Library preloading succeeds for all bundled libraries
  - Module resolution works for `repoql:<name>` and rejects other specifiers
  - Prepared script reuse works correctly across multiple invocations
- UDF tests shall verify:
  - `js('input.length', '"hello"')` returns `'5'`
  - `js_test('input > 3', '5')` returns `'true'`
  - `js_each` with array-returning script produces correct rows
  - Error cases return structured error JSON, not exceptions
  - `NULL` input handling
- Library smoke tests shall verify each bundled library loads and executes a basic operation in Jint
- Integration tests shall verify UDFs work in SQL queries with joins, WHERE clauses, and subqueries

## Constraints

- **Single writer** — UDFs are read-side; the JintEngine never writes to DuckDB. All writes remain through `DuckDbDataStore`.
- **Schema frozen** — no new tables. Libraries and config are stored outside DuckDB (embedded resources, config files).
- **No `AllowClr()`** — the Jint engine must never expose .NET types. The security model depends on this. Research: Jint without `AllowClr()` has no known escape vector (zero CVEs, `AllowGetType` defaults false, `AllowSystemReflection` defaults false).
- **No `eval()` / `Function()`** — `StringCompilationAllowed = false`. Blocks dynamic code generation in user scripts.
- **Non-pure UDFs** — `js()`, `js_test()`, `js_each()` must be registered as non-pure (`IsPure = false`) because they depend on engine state (loaded libraries, config).
- **Synchronous execution** — Jint supports async/await but DuckDB UDFs are synchronous. User scripts must not rely on async operations.
- **Transport parity** — the sandbox tool must be available via MCP, CLI, and gRPC. UDFs are available wherever SQL is available (all transports).
- **Docs with features** — `help://` documentation ships with the implementation.

## References

- [Jint](https://github.com/sebastienros/jint) — `Jint` NuGet package, v4.6.3+ (BSD-2-Clause)
- `docs/research/js-sandbox-runtimes.md` — runtime selection research (Jint chosen over WASI/Extism)
- `docs/research/js-sandbox-libraries.md` — library selection research with DuckDB gap analysis
- `src/RepoQL.Data.DuckDB/UdfFramework/` — UDF registration pattern (`[UdfClass]`, `[ScalarUdf]`, `[StructuredUdf]`)
- `src/RepoQL.Data.DuckDB/UdfImplementations/` — existing UDF examples
- `src/RepoQL.Commands/` — command registration pattern (`[CommandClass]`, `[Command]`)
- `src/RepoQL.ConsoleApp/Tools/` — MCP tool pattern (`[McpServerToolType]`, `[McpServerTool]`)
- `src/RepoQL.Contracts/Configuration/RepoQlConfig.cs` — config section pattern
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Errors in JS execution must never propagate as exceptions into DuckDB. All failures return structured results:

**UDF errors** return a JSON error object as VARCHAR:
```json
{"error": {"kind": "timeout", "message": "Script exceeded 2000ms limit", "suggestion": "Simplify the expression or increase sandbox.timeout_ms"}}
```

Error kinds: `timeout`, `memory`, `syntax`, `runtime`, `import`, `disabled`.

**Tool errors** return the same structure via `ToolResult.Error()`.

**Library load failures** at startup are logged as warnings. The library is marked unavailable. UDFs that import it get an actionable `import` error: `"Library 'repoql:yaml' failed to load: <reason>. Check ::sandbox.libs for available libraries."`

This aligns with the promise: one bad file never breaks anything else. One bad library never breaks the sandbox.
