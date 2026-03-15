---
description: Research into JavaScript sandbox runtimes and preloaded libraries for user-authored UDFs in RepoQL.
tags: [sandbox, javascript, wasm, wasi, udf, jint, extism, quickjs, libraries]
audience: { human: 40, agent: 60 }
purpose: { research: 90, design: 10 }
---

# JavaScript Sandbox Runtimes and Libraries

Research for selecting a sandbox runtime and preloaded library set for user-authored JavaScript UDFs in RepoQL.

*Research date: March 12, 2026*

## Context

RepoQL wants to allow user-authored JavaScript to run from SQL/UDF entry points. The JavaScript executes data transforms, validators, scorers, and analyzers against the code analysis graph. The critical use case is **per-row evaluation in SQL queries** — potentially thousands of calls per query.

Constraints:
- Must run on Windows, macOS, and Linux (developer laptop)
- Must not trust user code in the host process
- DuckDB UDFs are synchronous — the JS execution model must be synchronous
- Startup latency per call matters (per-row execution)
- Users and agents author the scripts
- Libraries should be discoverable via `repoql:<name>` import scheme

The decision: which runtime, which isolation model, which libraries earn a preload slot.

---

## Embedded JS Engines (In-Process)

### Jint

Pure managed C# JavaScript interpreter. Zero native dependencies.

| Spec | Value |
|------|-------|
| NuGet | `Jint` v4.6.3, released 2026-03-11 |
| Downloads | 26M total, ~41.7K/day |
| Stars | 4.6K |
| ES version | ES2025 (classes, arrow functions, destructuring, async/await, Proxy, BigInt, generators, Set methods, Iterator helpers) |
| License | BSD-2-Clause |
| Targets | .NET 8, .NET 10, .NET Standard 2.0/2.1 |

**Sandboxing**: Memory limit (`LimitMemory()`), wall-clock timeout (`TimeoutInterval()`), statement count (`MaxStatements()`), max array size (`MaxArraySize`), `CancellationToken` support, custom `Constraint` base class. No CLR access by default; must explicitly opt in via `AllowClr()`.

Known limitation: memory/timeout constraints are checked between statements, not during single operations. `MaxArraySize` (added in PR #923) mitigates the primary bypass vector. Both reported bypass issues (#683, #887) are **resource-limit DoS, not sandbox escapes**, and are closed/addressed.

> [Jint GitHub](https://github.com/sebastienros/jint) — repository and documentation
> [NuGet](https://www.nuget.org/packages/Jint) — package listing
> [Issue #683](https://github.com/sebastienros/jint/issues/683) — memory limit bypass (closed, fixed via MaxArraySize)
> [Issue #887](https://github.com/sebastienros/jint/issues/887) — nested array bypass (closed, fixed via PR #2282)

**Module system**: Native ESM `import`/`export`. In-memory module registration:

```csharp
engine.Modules.Add("repoql:zod", zodSourceCode);
engine.Modules.Add("repoql:utils", builder => builder.ExportValue("score", scoreFn));
```

Custom `IModuleLoader` interface for full control over import resolution. No need to enable file-based modules for in-memory-only usage.

> [Jint Modules documentation](https://github.com/sebastienros/jint#modules) — module API

**Performance** (from published benchmarks):

| Benchmark | Jint (prepared) | Jint (raw) | NiL.JS | YantraJS | Jurassic |
|-----------|----------------|------------|--------|----------|----------|
| EvaluationBenchmark | **15.13 us** | 31.25 us | 47.80 us | 175.92 us | 1,323.76 us |
| MinimalScriptBenchmark | **3.85 us** | 5.61 us | 4.37 us | 173.03 us | 240.95 us |

With `Engine.PrepareScript()`, Jint achieves ~3.85 us per minimal evaluation and ~15.13 us per expression evaluation. 10,000 rows at 15 us/eval = ~150ms JS overhead.

Engine reuse: creating a new Engine = ~37K ops/sec; reusing a cached engine = ~595K ops/sec (16x faster).

> [Benchmark gist](https://gist.github.com/lahma/d74d69521be0b47e9896b31506129629) — comparative benchmarks across .NET JS engines
> [Issue #615](https://github.com/sebastienros/jint/issues/615) — engine reuse performance data

**Production users**: RavenDB (per-document JS transforms — same pattern as per-row UDF), EventStore, OrchardCore, ELSA Workflows, docfx.

---

### ClearScript (Microsoft)

V8 engine embedded in .NET via native binaries.

| Spec | Value |
|------|-------|
| NuGet | `Microsoft.ClearScript.V8` v7.5.0, released 2025-03-07 |
| Downloads | 10.5M total |
| ES version | Full ES2024+ (V8 13.3) |
| License | MIT |
| Native deps | Yes — V8 binaries per platform (win-x64, linux-x64, osx-x64, osx-arm64) |

Cold start: ~300ms for engine instantiation. Total with module loading can reach 1.5-2 seconds.

Full ESM + CommonJS + custom `DocumentLoader` for import resolution. Thread safety via internal auto-serialization.

Limited built-in sandboxing. No filesystem/network restrictions out of the box. `--disallow-code-generation-from-strings` V8 flag disables `eval()`. Security depends on what you expose.

> [ClearScript GitHub](https://github.com/ClearFoundry/ClearScript) — repository
> [Discussion #567](https://github.com/microsoft/ClearScript/discussions/567) — cold start measurement

---

### NiL.JS

Pure C# ES6 interpreter. Zero dependencies.

| Spec | Value |
|------|-------|
| NuGet | `NiL.JS` v2.6.1721, released 2026-03-10 |
| Downloads | 607K total |
| Stars | 347 |
| ES version | ES6 (ES2015) |
| License | BSD-3-Clause |

Performance: MinimalScript = 4.37 us (comparable to Jint), ArrayStress = 6.42 ms (fastest). Timeout support via `Module.Run(timeLimit)`. No memory limits. Module system undocumented.

> [NiL.JS GitHub](https://github.com/nilproject/NiL.JS)

---

### YantraJS

Pure C# IL-compiled JS engine. ES6+ with async/await.

| Spec | Value |
|------|-------|
| NuGet | `YantraJS.Core` v1.2.314, released 2026-03-11 |
| Downloads | 368K total |
| ES version | ES6+ (classes, arrow, async/await, generators, optional chaining, ESM + CJS) |
| License | Apache-2.0 |

IL compilation overhead dominates for small expressions: ~173 us per MinimalScript (45x slower than Jint prepared). Fastest for sustained computation (StopwatchBenchmark = 78.63 ms vs Jint's 494 ms). No built-in timeout or memory limits.

> [YantraJS GitHub](https://github.com/yantrajs/yantra)

---

### Jurassic

Pure C# IL-compiled engine. ES5 with partial ES6.

| Spec | Value |
|------|-------|
| NuGet | `Jurassic` v3.2.9, released 2025-02-04 |
| Downloads | 2.8M total |
| ES version | ES5 complete, ES6 partial (classes yes, arrow functions NO, generators NO) |
| License | MIT |

No ESM support. No sandboxing features. EvaluationBenchmark = 1,323 us (88x slower than Jint prepared). Low maintenance activity. ES5-only means no modern JS ergonomics.

> [Jurassic GitHub](https://github.com/paulbartrum/jurassic)

---

### Topaz

Pure C# lock-free multithreaded JS engine.

| Spec | Value |
|------|-------|
| NuGet | `Topaz` v1.4.1, released 2024-11-14 |
| Downloads | 27.5K |
| Stars | 269 |
| ES version | Partial — no class support, no generators |
| License | MIT |

Unique multithreading story: functions callable from multiple threads in parallel. Claimed 3700 req/sec vs Jint's 1600 in server benchmarks. No class support means `import { z } from 'zod'` patterns won't work. No ESM, minimal sandboxing.

> [Topaz GitHub](https://github.com/koculu/Topaz)

---

## WASI Runtimes (Out-of-Process Isolation)

### wasmtime-dotnet

Official .NET bindings for Wasmtime (Bytecode Alliance).

| Spec | Value |
|------|-------|
| NuGet | `Wasmtime` v34.0.2, released 2025-08-05 |
| Downloads | ~1.1M total |
| Stars | 481 |
| License | Apache-2.0 w/ LLVM |
| Targets | .NET 8, 9, netstandard2.0/2.1 |
| Platforms | Win x64, Win arm64, macOS x64, macOS arm64, Linux x64, Linux arm64 |
| WASI | Preview 1 |

**Resource control**: Fuel consumption (`Store.Fuel`), epoch-based interruption (`Config.WithEpochInterruption` + `Store.SetEpochDeadline`), memory limits (`Store.SetLimits`), stack size (`Config.WithMaximumStackSize`).

**Pre-compilation**: `Module.Serialize()` → `byte[]` → `Module.Deserialize()`. Skips compilation on subsequent loads. `Config.WithCacheConfig()` for automatic caching.

**Concurrency**: `Engine` and `Module` are thread-safe and shareable. `Store` is single-threaded (one per execution). Pattern: one global Engine, cache compiled Modules, create Store per invocation.

**stdio limitation**: `WasiConfiguration` supports stdin/stdout/stderr only via **file paths**, not in-memory streams. Data must pass through temp files or through exported functions + linear memory.

> [wasmtime-dotnet GitHub](https://github.com/bytecodealliance/wasmtime-dotnet) — repository
> [NuGet](https://www.nuget.org/packages/Wasmtime) — package listing
> [API docs](https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.html) — .NET API reference

---

### Extism

High-level plugin framework built on Wasmtime. Bytes-in/bytes-out plugin ABI.

| Spec | Value |
|------|-------|
| NuGet | `Extism.Sdk` v1.10.0, released 2025-12-01 |
| Downloads | ~19K total |
| Stars | 47 (.NET SDK), ~1K (core runtime) |
| License | BSD-3-Clause |
| Platform gap | No `win-arm64` runtime package |

**Plugin model**: `plugin.Call("function_name", inputBytes)` → output bytes. No temp files. The host communicates via a shared virtual memory kernel with 4 memcpy operations per round-trip.

**JS plugin story**: The [Extism JS PDK](https://github.com/extism/js-pdk) compiles JavaScript to Wasm plugins using QuickJS-ng + Wizer snapshots. The `extism-js` compiler: bundles JS → loads QuickJS WASM module → pre-initializes with Wizer → emits self-contained `.wasm` file.

**Resource control**: `Manifest.Timeout` (TimeSpan), `PluginInitializationOptions.FuelLimit`, `Manifest.MemoryOptions` (page-based), `Manifest.AllowedHosts` (network allowlist), `Manifest.AllowedPaths` (filesystem allowlist), `CancelHandle` for thread-safe external cancellation.

**Pre-compilation**: `CompiledPlugin` class — compile once, `Instantiate()` many times. Their benchmarks: 266ms compiled vs 27.6s uncompiled instantiation.

**Concurrency**: Plugin instances are NOT thread-safe. Need one per concurrent execution. `CompiledPlugin` is shareable.

**Critical design constraint**: Extism requires a **build step** — JS must be compiled to `.wasm` via `extism-js` before execution. Cannot `eval()` a JS string at runtime.

> [Extism .NET SDK GitHub](https://github.com/extism/dotnet-sdk) — repository
> [Extism JS PDK](https://github.com/extism/js-pdk) — JavaScript plugin development kit
> [How Extism Works](https://dylibso.com/blog/how-does-extism-work/) — architecture deep-dive

---

### Other WASI Runtimes

**WasmerSharp**: Dead. Last release November 2019. No WASI, no resource limits.
> [WasmerSharp GitHub](https://github.com/migueldeicaza/WasmerSharp)

**WasmEdge**: No .NET bindings. Has C, Go, Rust, Python, Node.js.
> [WasmEdge GitHub](https://github.com/WasmEdge/WasmEdge)

**Wazero**: Go-only by design. No .NET bindings or C API.
> [Wazero](https://wazero.io/)

**WACS**: Pure C# WASM interpreter. 107 stars. 2-10% native throughput (~Python speed). No fuel, no memory limits, no pre-compilation. Only viable as exotic-platform fallback.
> [WACS GitHub](https://github.com/kelnishi/WACS)

---

## QuickJS

### Fork Situation

Two active codebases:

- **Bellard's original** ([bellard.org/quickjs](https://bellard.org/quickjs/)): Fabrice Bellard resumed active development. Latest version 2025-09-13.
- **QuickJS-NG** ([quickjs-ng/quickjs](https://github.com/quickjs-ng/quickjs)): Community fork, 40+ contributors, releases every ~2 months. CMake, MSVC first-class. Extra features: WeakRef, FinalizationRegistry, Iterator Helpers, polymorphic inline caching.

NG is ~3% slower than Bellard's with ~2% higher peak memory. Both share patches.

> [QuickJS-NG diff page](https://quickjs-ng.github.io/quickjs/diff/) — feature comparison

### ES Support

Nearly all of ES2023, passing nearly 100% of test262 for ES2023 features. Confirmed: async/await, generators, optional chaining, nullish coalescing, ESM import/export, Proxy, BigInt, private class fields, top-level await (NG). Missing: tail calls, ECMA-402 (Intl), regex `/v` flag.

> [bellard.org/quickjs/quickjs.html](https://bellard.org/quickjs/quickjs.html) — specification compliance

### Performance

~50x slower than V8 on compute benchmarks (interpreter, not JIT). ~2x faster than Hermes. Runtime instance lifecycle < 300 microseconds. 40-50% lower memory than V8.

> [JS engine benchmark 2025-07](https://dev.to/ahaoboy/js-engine-benchmark-2025-7-8-163b) — comparative benchmarks

### .NET Bindings

`QuickJS.NET` v0.0.3 — dormant since ~2021. Not production-ready. No maintained .NET bindings exist. The path to QuickJS from .NET is through WASI (wasmtime-dotnet or Extism), not native bindings.

> [QuickJS.NET GitHub](https://github.com/btx638/QuickJS.NET)

### WASM/WASI Packaging

**Javy** (Bytecode Alliance): Compiles JS → QuickJS bytecode → Wizer snapshot → WASM module. Dynamic linking mode: 220 bytes + bytecode size per user module, shared QuickJS provider. Shopify uses Javy for server-side Functions at checkout scale (5ms execution limit, 256KB module limit). JS via Javy is ~3x slower than equivalent Rust WASM.

> [Javy GitHub](https://github.com/bytecodealliance/javy) — repository
> [Shopify engineering blog](https://shopify.engineering/javascript-in-webassembly-for-shopify-functions) — production usage

**quickjs-emscripten**: QuickJS compiled to WASM via Emscripten. JS/TS bindings. Sync build ~1.3 MB.
> [quickjs-emscripten GitHub](https://github.com/justjake/quickjs-emscripten)

### Library Compatibility

QuickJS provides only ECMAScript builtins plus `std`/`os` modules. No `process`, `Buffer`, `require()`, `fetch`, `TextEncoder`/`TextDecoder`, `URL`, `setTimeout`, `console` (optional).

| Library | QuickJS feasible? | Notes |
|---------|-------------------|-------|
| zod / @zod/mini | Likely yes | Pure JS, zero deps. Edge runtime issues reported for v4 (#4248) but @zod/mini may avoid them |
| js-yaml | Likely yes | `Buffer` used only for binary YAML tags, degrades gracefully |
| papaparse | Likely yes | Core string parsing is pure JS. Worker/stream features won't work |
| fuse.js | Very likely | ES5-compatible, no DOM deps |
| picomatch | Yes | `/posix` variant avoids OS detection |

Polyfills potentially needed: `TextEncoder`/`TextDecoder`, `console`, `structuredClone`, `URL`.

Practical approach: bundle with esbuild into a single file targeting ES2023. This is exactly what Shopify does for Javy.

---

## Alternative Isolation Approaches

### Process-Level (ClickHouse Pattern)

ClickHouse uses `executable_pool` — maintains a pool of long-running processes that read from stdin and write to stdout. Any language works. Pool amortizes process startup.

Applied to RepoQL: maintain a pool of Deno processes with zero permission flags (`deno run --no-prompt`). Communicate via JSON over stdin/stdout. Cross-platform, strong OS-level isolation, no native dependencies in the .NET host.

> [ClickHouse UDF docs](https://clickhouse.com/docs/sql-reference/functions/udf) — executable UDF documentation

### Deno as Sandbox

Deny-by-default security model. Running `deno run --no-prompt script.js` with zero permission flags gives a fully locked-down runtime with no filesystem, network, env, or subprocess access. LMStudio uses this approach for LLM-generated code execution.

> [Deno Security docs](https://docs.deno.com/runtime/fundamentals/security/) — permission model
> [LMStudio JS sandbox](https://lmstudio.ai/lmstudio/js-code-sandbox) — production usage of Deno sandboxing

---

## Prior Art: JS UDFs in Databases

| Database | Engine | Isolation | Module Support | Notable Constraints |
|----------|--------|-----------|---------------|---------------------|
| PostgreSQL (plv8) | V8 in-process | Per-session JS context | Limited (no ES modules) | Trusted procedural language |
| Snowflake | V8 | Query/data isolation layers | `eval()` disabled, no external imports | 100KB source limit |
| BigQuery | Sandboxed environment | Restricted system calls | Limited | Limited memory per query |
| ClickHouse | External process (any lang) | Process-level | N/A | Pool mode for performance |
| DuckDB | None native for JS | N/A | N/A | C# UDFs via DuckDB.NET, Python UDFs via client |

> [PLV8 Documentation](https://plv8.github.io/) — PostgreSQL V8 extension
> [Snowflake JS UDF docs](https://docs.snowflake.com/en/developer-guide/udf/javascript/udf-javascript-introduction) — introduction
> [BigQuery UDF docs](https://cloud.google.com/bigquery/docs/reference/standard-sql/user-defined-functions) — JS UDF reference

---

## Preloaded Libraries

### Evaluation Criteria

- Bundle size (minified) — this is the in-memory cost in QuickJS/Jint, gzip is irrelevant
- Zero dependencies or fully bundleable
- Pure JS — no native/WASM/Node.js API requirements
- Value density for code-analysis data transforms
- ESM support

### Candidate Libraries by Category

#### Data Validation / Schema

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| superstruct | 10.5 KB | 0 | Functional composition API. Smallest full bundle among validators |
| @zod/mini | ~14 KB | 0 | Zod v4 mini variant. Familiar API |
| valibot | 81 KB (full) | 0 | Tree-shakeable but full bundle too large for constrained runtime |

> [Bundlephobia: superstruct](https://bundlephobia.com/package/superstruct)

#### String / Text Processing

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| mustache | 6.5 KB | 0 | Logic-less templates. Battle-tested |
| fastest-levenshtein | 1.6 KB | 0 | String distance. Optimized for speed |
| diff (jsdiff) | 17.6 KB | 0 | Text diff (lines, words, chars) |

#### Data Formats

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| js-yaml | 38 KB | 0 | YAML 1.2. Most popular YAML parser |
| smol-toml | 11 KB | 0 | TOML 1.1. Pure JS. ESM |
| txml | 5.8 KB | 0 | XML parser. Fastest in benchmarks |
| papaparse | 18.8 KB | 0 | CSV parser. Pure JS |
| json5 | 30.3 KB | 0 | JSON with comments/trailing commas |
| ini | 3.1 KB | 0 | INI parser/serializer |

#### Pattern Matching

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| picomatch | 20.1 KB | 0 | Full glob matching. Used by Jest, Rollup, Webpack, Chokidar (5M+ projects) |

#### Code Analysis Utilities

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| compare-versions | 2.3 KB | 0 | Semver comparison, ranges, wildcards |
| semver | 24.1 KB | 0 | Full npm semver (ranges, coerce, prerelease) |

#### Scoring / Fuzzy Search

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| fuse.js | 17.3 KB | 0 | Fuzzy search with configurable scoring. ESM |
| minisearch | 17.3 KB | 0 | Full-text search with TF-IDF, prefix, fuzzy |

#### Graph Utilities

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| toposort | 1.2 KB | 0 | Topological sort. Dependency ordering |
| graph-data-structure | 6.1 KB | 0 | Directed graph, topo sort, Dijkstra |

#### Date/Time

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| dayjs | 7.1 KB | 0 | Moment.js-compatible API. Immutable |

#### General Utilities

| Library | Min Size | Deps | Notes |
|---------|----------|------|-------|
| radash | 11.8 KB | 0 | Modern lodash: group, sort, unique, pick, omit, get, set, try, range |
| pathe | 8.2 KB | 0 | Cross-platform path manipulation. No Node.js dep |
| ohash | 6.0 KB | 0 | Object hashing, deep equality |

### Top 8 by Value per Byte

| # | Library | Min Size | Why it earns a slot |
|---|---------|----------|---------------------|
| 1 | radash | 11.8 KB | Swiss army knife for data transforms: group, sort, unique, pick, omit, objectify, range |
| 2 | picomatch | 20.1 KB | Glob matching on file paths — fundamental operation for code analysis |
| 3 | js-yaml | 38 KB | YAML is ubiquitous in repos (CI, k8s, configs) |
| 4 | fuse.js | 17.3 KB | Fuzzy matching/scoring on symbol names, paths, annotations |
| 5 | dayjs | 7.1 KB | Date parsing/formatting for git timestamps, annotation dates |
| 6 | compare-versions | 2.3 KB | Semver comparison for dependency analysis. Nearly free |
| 7 | toposort | 1.2 KB | Dependency ordering from edge data. Nearly free |
| 8 | papaparse | 18.8 KB | CSV parsing for data files in repos |

**Total estimated in-memory footprint: ~117 KB minified.**

### Honorable Mentions

| Library | Min Size | Why deferred |
|---------|----------|-------------|
| superstruct | 10.5 KB | Validation is useful but plain JS validators work fine |
| smol-toml | 11 KB | Only needed for Rust/Go-heavy repos |
| mustache | 6.5 KB | Templating is niche for SQL UDFs |
| fastest-levenshtein | 1.6 KB | So tiny it could join the top 8 for free |
| pathe | 8.2 KB | Path manipulation — `picomatch` covers the glob case, native string ops cover the rest |
| diff (jsdiff) | 17.6 KB | Text diff is valuable but less commonly needed per-row |

---

## Comparison

### Runtime Comparison

| Dimension | Jint | Extism (JS PDK) | wasmtime-dotnet + QuickJS | Deno subprocess pool |
|-----------|------|-----------------|---------------------------|---------------------|
| Isolation strength | Moderate (in-process, best-effort limits) | Strong (WASM sandbox) | Strong (WASM sandbox) | Strong (OS-level, deny-by-default) |
| Per-eval latency | ~15 us (prepared) | Unknown; build step required | Microseconds (instantiation) + QuickJS eval | Process IPC overhead per call |
| Cold start | Negligible | 266ms compiled instantiation | Single-digit ms (module compile), us (instantiate) | Process spawn (~50-100ms), amortized with pool |
| Native deps | None (pure C#) | Yes (libextism per platform) | Yes (wasmtime native per platform) | Deno binary must be installed |
| ES version | ES2025 | ES2020 (QuickJS-ng) | ES2023 (QuickJS) | Full V8 ES2024+ |
| Module system | ESM + custom IModuleLoader | Bundled at compile time | Bundled at compile time | Full Node/Deno module system |
| Can eval() a string? | Yes | No (requires build step) | Yes (pass to QuickJS stdin) | Yes |
| Platform coverage | Everywhere .NET runs | No win-arm64 | Full (all wasmtime platforms) | Everywhere Deno is installed |
| Production precedent | RavenDB, EventStore, OrchardCore | Shopify (via Javy), Dylibso ecosystem | Shopify Functions (Javy + wasmtime) | LMStudio |
| Complexity | Low | Medium (build pipeline) | High (linear memory, host functions) | Medium (process pool management) |

### Library Fit Comparison

| Library | Works in Jint? | Works in QuickJS? | Notes |
|---------|---------------|-------------------|-------|
| radash | Yes (ES2025) | Yes (ES2023) | Pure JS, ESM |
| picomatch | Yes | Yes (posix variant) | Pure JS |
| js-yaml | Yes | Likely (Buffer fallback) | May need TextEncoder polyfill in QuickJS |
| fuse.js | Yes | Yes (ES5-compatible) | Pure JS, no DOM |
| dayjs | Yes | Likely | Pure JS |
| compare-versions | Yes | Yes | Pure JS, ESM |
| toposort | Yes | Yes | Pure JS |
| papaparse | Yes (no streaming) | Yes (no streaming) | Core parsing is pure JS |

---

## Gaps

- **Jint vs QuickJS-via-WASM benchmarks for data transforms**: All published benchmarks use synthetic compute tests (Octane, SunSpider). No one has benchmarked JSON parsing, filtering, and mapping workloads — which is the actual RepoQL use case
- **Exact Extism JS PDK per-call latency from .NET**: No published benchmarks with .NET host specifically. The 266ms figure is instantiation, not per-call
- **wasmtime-dotnet pooling allocator**: Rust API supports instance pooling for fast instantiation. Whether the .NET bindings expose this is undocumented
- **Library smoke tests in QuickJS**: No published compatibility test suites for "does library X work in QuickJS." Each library needs actual testing
- **Jint `PrepareModule()` efficiency**: Whether pre-parsed modules perform as well as `PrepareScript()` for the per-row pattern is undocumented
- **QuickJS heap overhead per loaded library**: Minified size is a proxy but parsed AST + runtime objects add overhead. Requires `JS_GetMemoryUsage` measurement
- **Valibot effective size without tree-shaking**: Designed for bundler tree-shaking. Without it (as in QuickJS), the full 81 KB loads. Needs verification
- **smol-toml gzip size**: Bundlephobia reports 347 bytes gzipped for 11 KB minified — suspiciously low, may be artifact

---

## Leads Not Pursued

- **stdlib-js**: Massive modular standard library with statistics, math, data utilities. Individual packages could be cherry-picked for statistical scoring UDFs
- **Radashi**: Community fork/evolution of radash with more functions. May supersede it
- **jsonata**: JSON query/transform language. 74 KB — too large to preload but could be opt-in
- **SpiderMonkey.wasm**: Mozilla's engine compiled to WASM. Faster than QuickJS, larger binary. Used by Fastly
- **txiki.js**: QuickJS + libuv runtime by QuickJS-NG author. Adds setTimeout, fetch, TextEncoder. Could be compiled to WASM
- **WasmEdge QuickJS**: Fork adding Node.js API compatibility. Different WASM runtime
- **Component Model + WIT interfaces**: WASI 0.2 stable, could define typed UDF contracts. Worth investigating if going WASM route
- **Wizer pre-initialization**: Snapshots WASM module state after init. 1.35-6x faster instantiation. Already used by Extism JS PDK
- **JavaScriptEngineSwitcher** (NuGet): Unified interface across engines. Could enable A/B testing during development
- **Puerts.Core**: Wraps V8 and QuickJS for .NET. Designed for Unity but might work for general .NET
