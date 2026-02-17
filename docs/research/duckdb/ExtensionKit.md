---
description: Research on DuckDB.ExtensionKit for building native DuckDB extensions in C# — informing whether/how RepoQL could use it
tags: [duckdb, extensions, nativeaot, udf, extensionkit]
audience: { human: 50, agent: 50 }
purpose: { research: 90, reference: 10 }
---

# DuckDB.ExtensionKit Research

Research for whether DuckDB.ExtensionKit could be used to package RepoQL UDFs (or a subset) as a native DuckDB extension, and what that would look like.

*Research date: 2026-02-17*

## Context

RepoQL currently implements ~25 UDFs via DuckDB.NET's `RegisterScalarFunction<>` API, using an attribute-based discovery framework (`[UdfClass]`, `[ScalarUdf]`, `[StructuredUdf]`). All parameters and return types are VARCHAR (the "all-VARCHAR strategy"), with type conversion in C#. Structured UDFs serialize results to JSON, expanded via `json_each()` macros.

DuckDB.ExtensionKit is a new toolkit by Giorgi Dalakishvili (also the DuckDB.NET maintainer) for building native `.duckdb_extension` files in C# using NativeAOT compilation. The question: could this approach address limitations in the current system, and at what cost?

## DuckDB.ExtensionKit

### What It Is

A toolkit producing standalone `.duckdb_extension` binaries from C# via NativeAOT. No .NET runtime dependency in the output. Three components:

| Component | Role |
|-----------|------|
| **DuckDB.ExtensionKit** | Runtime library — C API bindings, typed vector readers/writers, function registration |
| **DuckDB.ExtensionKit.Generators** | Roslyn source generator — emits the native entry point (`[UnmanagedCallersOnly]`) |
| **DuckDB.JWT** | Example extension — JWT validation and claim extraction |

> [GitHub — Giorgi/DuckDB.ExtensionKit](https://github.com/Giorgi/DuckDB.ExtensionKit)

### API Surface

**Entry point** — a `partial static` class with `[DuckDBExtension]`:

```csharp
[DuckDBExtension]
public static partial class MyExtension
{
    private static void RegisterFunctions(DuckDBConnection connection)
    {
        // Register scalar and table functions here
    }
}
```

The source generator produces `{extension}_init_c_api` with `[UnmanagedCallersOnly]`, which DuckDB calls at load time.

**Scalar functions** — generic extension methods on `DuckDBConnection`:

```csharp
connection.RegisterScalarFunction<string, int>("string_length",
    (readers, writer, rowCount) =>
    {
        for (ulong i = 0; i < rowCount; i++)
        {
            var s = readers[0].GetValue<string>(i);
            writer.WriteValue(s?.Length ?? 0, i);
        }
    });
```

Overloads for 0–4 input type parameters. The callback receives `IReadOnlyList<IDuckDBDataReader>`, `IDuckDBDataWriter`, and `ulong rowCount`.

> Source: `DuckDB.ExtensionKit/ScalarFunctions/ScalarFunctionExtensions.cs`

**Table functions** — bind callback returns schema + data, mapper callback writes rows:

```csharp
connection.RegisterTableFunction<string>("extract_claims",
    parameters =>
    {
        var token = parameters[0].GetValue<string>();
        return new TableFunction(
            new List<ColumnInfo> { new("name", typeof(string)), new("value", typeof(string)) },
            ParseClaims(token)
        );
    },
    (item, writers, rowIndex) =>
    {
        writers[0].WriteValue(((string, string))item.Item1, rowIndex);
        writers[1].WriteValue(((string, string))item.Item2, rowIndex);
    });
```

Generic overloads for 0–8 input parameters. Data is provided as `IEnumerable` — currently fully materialized, not streamed.

> [Issue #1 — Streaming for large datasets](https://github.com/Giorgi/DuckDB.ExtensionKit/issues/1) — open, filed 2026-02-14

**Type mappings** — native DuckDB types, not all-VARCHAR:

| CLR Type | DuckDB Type |
|----------|------------|
| `string` | VARCHAR |
| `int` | INTEGER |
| `long` | BIGINT |
| `double` | DOUBLE |
| `bool` | BOOLEAN |
| `DateTime` | TIMESTAMP |
| `DateOnly` | DATE |
| `Guid` | UUID |
| `decimal` | DECIMAL(38,18) |
| `BigInteger` | HUGEINT |

Full set includes all integer widths, float, TimeSpan, TimeOnly, DateTimeOffset.

> Source: `DuckDB.ExtensionKit/Extensions/TypeExtensions.cs`

### Build Process

```xml
<PropertyGroup>
  <ExtensionName>myextension</ExtensionName>
  <DuckDBVersion>v1.2.0</DuckDBVersion>
  <PublishAot>true</PublishAot>
  <IsAotCompatible>true</IsAotCompatible>
  <TargetFramework>net10.0</TargetFramework>
  <AllowUnsafeBlocks>True</AllowUnsafeBlocks>
</PropertyGroup>
```

Pipeline: source generation → `dotnet publish -r {rid}` (NativeAOT) → Python script appends DuckDB extension metadata (512-byte footer with version, platform, signature space). The Python script comes from DuckDB's `extension-ci-tools` git submodule.

Loading: `INSTALL 'path/to/ext.duckdb_extension'; LOAD myextension;` — requires `allow_unsigned_extensions = true` unless signed.

Cross-platform: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64. CI tests pass on all three OS families.

> Source: `DuckDB.JWT/DuckDB.JWT.csproj`, `.github/workflows/test.yml`

### Maturity

| Indicator | Value |
|-----------|-------|
| Created | 2025-12-26 |
| Commits | 17 |
| Stars | 40, Forks | 3 |
| Open issues | 1 (streaming) |
| NuGet packages | None |
| Tagged releases | None |
| License | MIT |
| Known production users | None found |

The project is ~8 weeks old. Not published to NuGet — consumed via project reference only.

> [GitHub repo](https://github.com/Giorgi/DuckDB.ExtensionKit)

### Author Context

Giorgi Dalakishvili maintains DuckDB.NET (the primary .NET ADO.NET provider for DuckDB — 5.8M+ NuGet downloads, v1.4.4). ExtensionKit shares no code with DuckDB.NET but comes from the same deep familiarity with DuckDB's C API.

> [NuGet — DuckDB.NET.Data](https://www.nuget.org/packages/DuckDB.NET.Data/) — v1.4.4, established

## DuckDB Extension Architecture

### The C Extension API (Stable)

DuckDB v1.2.0 stabilized a C extension API based on a struct of function pointers (inspired by SQLite). Extensions receive this struct at load time — no static linking against DuckDB internals.

Key properties:
- The struct **can only grow**, never be modified — forward compatibility guaranteed
- Extensions targeting `C_STRUCT` ABI work with the targeted version **and all newer versions**
- Extensions targeting `C_STRUCT_UNSTABLE` are pinned to the exact DuckDB version
- A demo C extension was 17KB vs megabytes for C++ extensions

> [PR #12682 — C API extensions](https://github.com/duckdb/duckdb/pull/12682) — original design
> [PR #14992 — Stabilization at v1.2.0](https://github.com/duckdb/duckdb/pull/14992)

### What Extensions Can Register

| Capability | Stable C API | ExtensionKit |
|-----------|:---:|:---:|
| Scalar functions | Yes | Yes |
| Table functions | Yes | Yes |
| Aggregate functions | Yes | **No** |
| Replacement scans | Yes | **No** |
| Custom types | Unverified | **No** |
| Copy functions | Unverified | **No** |
| Optimizer extensions | C++ only | **No** |
| Storage backends | C++ only | **No** |

> [DuckDB C API reference](https://duckdb.org/docs/stable/clients/c/api)

### Version Coupling

| ABI Type | Coupling | RepoQL Impact |
|----------|----------|---------------|
| **C_STRUCT** (stable) | Minimum version — forward compatible | Build once, works with future DuckDB versions |
| **C_STRUCT_UNSTABLE** | Exact version only | Must rebuild per DuckDB release |
| **CPP** | Exact version only | N/A (C++ extensions) |

RepoQL currently has **no version coupling** — DuckDB.NET bundles the DuckDB native library and UDFs register at runtime. A native extension would introduce coupling (even with `C_STRUCT`, the minimum version floor advances as the extension uses newer API features).

> [Versioning of Extensions — DuckDB](https://duckdb.org/docs/stable/extensions/versioning_of_extensions)

### Distribution

| Channel | Build | Signing | .NET Support |
|---------|-------|---------|:---:|
| Community repo | DuckDB CI (CMake) | DuckDB-signed | **No** — C++ only |
| Custom repo | Self-built | Self-signed or unsigned | Yes |
| Local path | Self-built | Unsigned | Yes |

The community extension CI only officially supports C++ (and experimentally Rust). A .NET extension would need custom distribution. Wasm targets are not possible — NativeAOT does not support Wasm.

> [Community Extensions FAQ](https://duckdb.org/community_extensions/faq)

## RepoQL's Current UDF System

### Architecture

Attribute-based discovery → DuckDB.NET registration → SQL macro generation:

1. `UdfRegistry` scans assemblies for `[UdfClass]` types at startup
2. Methods with `[ScalarUdf]` or `[StructuredUdf]` are registered via `conn.RegisterScalarFunction<string, ..., string>()`
3. SQL macros are auto-generated with COALESCE wrapping, defaults, and `json_each()` expansion for structured UDFs
4. Instance creation per chunk via `ActivatorUtilities.CreateInstance` (DI-scoped)

> Source: `src/RepoQL.Data.DuckDB/UdfFramework/UdfRegistry.cs`

### Current Limitations

| Limitation | Detail |
|-----------|--------|
| All-VARCHAR only | No native type support at the DuckDB level |
| Max 4 direct params | Beyond 4, remaining params packed as JSON |
| No aggregate UDFs | Framework doesn't support them |
| No native table functions | JSON serialization + `json_each()` overhead |
| Synchronous only | Async must block with `.GetAwaiter().GetResult()` |
| Instance per chunk | New DI scope per 2048-row batch |
| Exceptions can't propagate | Caught and serialized as `__udf_error__` JSON |

> Source: `src/RepoQL.Data.DuckDB/UdfFramework/UdfRegistry.cs`, `.claude/Skills/udf-author/references/constraints.md`

### The Two Worlds

RepoQL's UDFs fall into two architecturally distinct categories:

**Pure computation** (extension candidates):
Functions like `match_score`, `glob_match`, `language_from_media_type_or_uri`, `line_for_byte_offset`, `symbol_matches`, `cosine_similarity_json`. Stateless, depend only on inputs.

**Stateful / service-dependent** (NOT extension candidates):
Functions like `explore`, `search`, `snippet`, `git_diff`, `git_blame`, `git_status`, `mcp_call`, `llm`, `embed`, `vector_search`, `operations`, `uri_registry`, `diagnostics`. These reach into host process state: file systems, git repos, MCP client connections, LLM providers, ONNX models, URI registry, operation tracking.

A native extension runs inside the DuckDB process with **no access to the host's service container**. The stateful UDFs fundamentally cannot run in an isolated extension — they require the host process context.

### DuckDB.NET Version

RepoQL uses DuckDB.NET.Data.Full **1.4.1**.

> Source: `Directory.Packages.props:15`

## NativeAOT Constraints

| Constraint | Impact on RepoQL UDFs |
|-----------|----------------------|
| **Reflection** | `UdfRegistry` uses `AppDomain.CurrentDomain.GetAssemblies()`, `GetCustomAttribute<>`, `method.Invoke()`. NativeAOT's trimmer removes types it can't statically trace. RepoQL already has `ILLink.Descriptors.xml` but the dynamic discovery pattern conflicts with static compilation. |
| **DI container** | UDFs accept constructor-injected services. Extension process has no `IServiceProvider`. |
| **Dynamic assembly loading** | Not supported in NativeAOT. Everything statically linked at compile time. |
| **Target framework** | ExtensionKit requires .NET 10.0 (`net10.0`). |
| **Binary size** | Minimum ~1.5–4.5 MB for NativeAOT. RepoQL's UDFs + dependencies would be larger. |

> [Native AOT deployment — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
> [Native AOT reflection migration guide](https://blog.stackademic.com/surviving-native-aot-the-reflection-migration-guide-every-net-architect-needs-fa3760fbb41b)

## Stable C API Capabilities Missing from DuckDB.NET

The DuckDB C extension API (stabilized at v1.2.0) exposes capabilities that DuckDB.NET does not bind. These are architectural unlocks — things that are impossible or require workarounds through the current in-process approach. All are in the **stable** API (forward compatible).

### Replacement Scans

**C API**: `duckdb_add_replacement_scan`, `duckdb_replacement_scan_set_function_name`, `duckdb_replacement_scan_add_parameter` (4 functions, all stable)

**DuckDB.NET**: None. [Open feature request](https://github.com/Giorgi/DuckDB.NET/issues/239), no implementation.

When SQL references a table that doesn't exist in the catalog, DuckDB calls the replacement scan callback with the table name. The callback rewrites it to a table function call. This is how `httpfs` makes `SELECT * FROM 'https://...'` work.

For RepoQL, this would enable:

```sql
-- Instead of:
SELECT * FROM read('file:///src/Foo.cs')

-- This just works:
SELECT * FROM 'file:///src/Foo.cs'
SELECT * FROM 'github://owner/repo/src/Foo.cs'
```

URIs as table names in FROM clauses — the desire path for URI-addressed data.

> [DuckDB Replacement Scans docs](https://duckdb.org/docs/stable/clients/c/replacement_scans), [DuckDB.NET Issue #239](https://github.com/Giorgi/DuckDB.NET/issues/239)

### Table Function Optimization Hints

**C API**: `duckdb_bind_set_cardinality`, `duckdb_table_function_supports_projection_pushdown`, `duckdb_init_set_max_threads`, `duckdb_table_function_set_local_init` (all stable)

**DuckDB.NET**: Partial — has bind callbacks and `duckdb_bind_add_result_column`, but missing cardinality, projection pushdown, threading, and per-thread init.

What's missing matters for the optimizer:
- **Cardinality estimates** — without them, DuckDB can't make informed join ordering decisions for table function results
- **Projection pushdown** — without it, table functions produce all columns even when the query only uses one (wasted work for wide results)
- **Multi-threaded init** — without it, table functions scan single-threaded even when DuckDB could parallelize

These are the difference between table functions the optimizer can reason about and opaque black boxes.

> [DuckDB Table Functions C API](https://duckdb.org/docs/stable/clients/c/table_functions)

### Aggregate Functions

**C API**: 16 functions including `duckdb_create_aggregate_function`, `duckdb_aggregate_function_set_functions` (state_size/init/update/combine/finalize), `duckdb_register_aggregate_function`, plus function set variants for overloading. All stable.

**DuckDB.NET**: None. No aggregate function bindings exist.

Enables custom GROUP BY and window aggregates. The `combine` callback supports parallel aggregation across threads.

> [DuckDB C API reference](https://duckdb.org/docs/stable/clients/c/api), [capi_aggregate_functions.cpp test](https://github.com/duckdb/duckdb/blob/main/test/api/capi/capi_aggregate_functions.cpp)

### Function Sets (Overloads)

**C API**: `duckdb_create_scalar_function_set`, `duckdb_add_scalar_function_to_set`, `duckdb_register_scalar_function_set` + aggregate equivalents (8 functions, all stable)

**DuckDB.NET**: None. Only individual function registration.

True type-dispatched overloading — a single function name that resolves based on argument types and count. Without this, RepoQL uses macro defaults or distinct names. With it, `snippet(uri)` and `snippet(uri, context_lines)` are genuinely different function implementations.

> [DuckDB C API reference](https://duckdb.org/docs/stable/clients/c/api)

### Custom Types + Cast Functions

**C API**: `duckdb_register_logical_type` + 12 cast function APIs (`duckdb_create_cast_function`, `duckdb_cast_function_set_source_type`, `duckdb_cast_function_set_implicit_cast_cost`, `duckdb_register_cast_function`, etc.). All stable.

**DuckDB.NET**: Can create and inspect logical types, but cannot register them or define cast functions.

A `REPO_URI` type visible in schema introspection, usable in column definitions, with auto-coercion from VARCHAR and custom validation in the cast. Lower impact than replacement scans but enables domain modeling.

> [DuckDB Types C API](https://duckdb.org/docs/stable/clients/c/types)

### Unstable (Not Yet Safe to Build On)

| Capability | Functions | What it would enable |
|-----------|-----------|---------------------|
| **Copy functions** | 36 functions | Custom `COPY TO/FROM` format handlers |
| **Configuration options** | 9 functions | Extension settings via `SET`/`RESET` |

Both are in the C API header but not in the stable extension API struct — they can be removed in future versions.

> [DuckDB reference-extension-c](https://github.com/duckdb/reference-extension-c)

### Summary: Stable API Gaps in DuckDB.NET

| Capability | Stable C API | DuckDB.NET | Impact |
|-----------|:---:|:---:|--------|
| **Replacement scans** | 4 functions | None | URIs as table names |
| **Dynamic table function bind** | ~25 functions | Partial (missing optimization hints) | Optimizer-aware, dynamic schemas |
| **Aggregate functions** | 16 functions | None | Custom GROUP BY / window |
| **Function sets** | 8 functions | None | Type-dispatched overloading |
| **Custom types + casts** | 42 functions | None (create only, no register/cast) | Domain type modeling |

Accessing these doesn't strictly require building a native extension. Three paths exist:

1. **P/Invoke directly** — bypass DuckDB.NET for these features, call the DuckDB C API through the native library that DuckDB.NET already bundles. No NativeAOT, no extension packaging, no version coupling. ExtensionKit's `DuckDBExtApiV1` struct provides the function pointer bindings.
2. **Native extension** — build a `.duckdb_extension` via ExtensionKit (or vendored code). Gets all capabilities but adds NativeAOT, version coupling, and distribution complexity.
3. **Contribute upstream** — add the missing bindings to DuckDB.NET. Most aligned with the ecosystem but dependent on Giorgi's review timeline.

## Comparison

| Dimension | Current (in-process via DuckDB.NET) | Extension (via ExtensionKit) |
|-----------|-------------------------------------|------------------------------|
| **Type safety** | All-VARCHAR, conversion in C# | Native DuckDB types |
| **Table functions** | JSON serialization + `json_each()` | Native table functions |
| **Parameter limit** | 4 direct, JSON packing beyond | 4 scalar, 8 table |
| **Aggregate functions** | Not supported | Not supported (C API supports it, ExtensionKit doesn't yet) |
| **Host service access** | Full (DI, file system, git, MCP, LLM, embeddings) | None — isolated process |
| **Discovery** | Runtime reflection (dynamic) | Compile-time source generation (static) |
| **DuckDB version coupling** | None — registers at runtime | Minimum version floor (stable) or exact version (unstable) |
| **Distribution** | Ships with RepoQL binary | Separate `.duckdb_extension` artifact per platform |
| **Build complexity** | Standard `dotnet build` | NativeAOT publish + Python metadata script + git submodule |
| **Target framework** | net9.0 (current) | net10.0 (required by ExtensionKit) |
| **Error handling** | `__udf_error__` JSON serialization | `catch (Exception) { return 0; }` — no error message propagation |
| **Maturity** | Battle-tested in RepoQL | 8 weeks old, 17 commits, no releases, no NuGet |
| **Standalone use** | Requires RepoQL process | Works in any DuckDB instance |
| **Performance (structured)** | JSON serialization overhead | Native columnar output |
| **Performance (scalar)** | String conversion overhead | Native type processing |

## Non-C++ Extension Landscape

| Language | Template | Maturity | Community Repo |
|----------|----------|----------|:-:|
| **Rust** | [extension-template-rs](https://github.com/duckdb/extension-template-rs) | Experimental, official | Yes (`rusty_quack`) |
| **C#** | DuckDB.ExtensionKit | Very early, community | No |
| **Go** | In development by DuckDB team | Not available | No |
| **Zig** | Community blog posts | Experimental | No |

Rust is the most mature non-C++ option and has demonstrated community repo acceptance.

> [DuckDB C Extension Template](https://github.com/duckdb/extension-template-c) — experimental

## Under Code Ownership

ExtensionKit is MIT licensed. Vendoring the code changes the calculus — the question shifts from "is ExtensionKit ready?" to "is the extension architecture itself a good fit?"

### The Dynamic Schema Problem

The most significant limitation in the current system isn't performance — it's that **all columns must be known at compile time**. The `[StructuredUdf]` pattern reflects C# record properties into `json_each()` macro columns at registration time. For functions where the output schema depends on input data (like `parse()` or MCP tool calls), this is a fundamental blocker.

The current workaround: write data to a temp file, then use DuckDB's built-in `read_csv_auto()` / `read_json_auto()` which support dynamic schema detection natively. This is the temp file bridge pattern used by `parse()` and all MCP tool macros:

```sql
-- parse() macro
SELECT * FROM read_csv_auto(_write_temp_csv(text), header := true)

-- MCP tool macros (auto-generated)
SELECT * FROM read_json_auto(
    _write_temp_json(parse_structured(_mcp_call_internal('server', 'tool', params))),
    maximum_object_size := 67108864
)
```

Costs of this pattern:
- **Disk I/O** on every call (write temp file, DuckDB reads it back)
- **Full materialization** — entire dataset written before DuckDB sees any rows
- **Plumbing UDFs** — `WriteTempCsvUdf` and `WriteTempJsonUdf` exist solely as bridges
- **Complex macro chains** — 3-4 function calls deep

With native table functions (via the C extension API), the **bind callback** declares columns dynamically at query planning time — inspect the data, call `add_result_column()` per column, then stream rows via the `next` callback. No temp files, no materialization, dynamic schemas. This is how `read_csv_auto`, `read_json_auto`, and `read_parquet` work internally.

This elevates native table functions from "performance improvement" to **architectural unlock** — the ability to return dynamic schemas without the temp file workaround.

> Source: `src/RepoQL.Data.DuckDB/UdfImplementations/WriteTempCsvUdf.cs`, `WriteTempJsonUdf.cs`, `src/RepoQL.Data.DuckDB/Schema/Macros/parse.sql`, `src/RepoQL.Mcp.Client/McpMacroGenerator.cs`

### What Becomes Tractable

| Gap | Effort to Close |
|-----|----------------|
| **Aggregate functions** | DuckDB's C API exposes `duckdb_register_aggregate_function` with Init/Update/Combine/Finalize callbacks. The pattern is documented in `test/api/capi/capi_aggregate_functions.cpp` in the DuckDB repo. Adding C# wrappers follows the same pattern as existing scalar/table registration. |
| **Streaming table functions** | ExtensionKit currently materializes `IEnumerable` fully. DuckDB's table function C API supports chunked iteration natively — the bind/init/next pattern. Wrapping this is mechanical. |
| **Error propagation** | The generated init swallows exceptions. The C API init function returns a status code — returning meaningful error info requires using `duckdb_extension_set_error` (if available in the stable API) or a diagnostics table. Fixable. |
| **Blob/binary support** | Adding a `BlobVectorDataWriter` following the existing reader/writer pattern. |
| **Target framework** | The `net10.0` requirement appears to be a choice, not a hard constraint. NativeAOT has been available since .NET 7. Whether the specific APIs used require .NET 10 needs verification. |
| **Build tooling** | The Python metadata script dependency could be replaced with a C# MSBuild task (the footer format is 512 bytes of structured data). |

### What Doesn't Change

These are properties of the DuckDB extension architecture itself, not ExtensionKit limitations:

| Constraint | Why it's structural |
|-----------|-------------------|
| **No host service access** | Extensions load into the DuckDB process via `dlopen`. There is no mechanism for an extension to access the loading application's service container, file handles, or in-memory state. |
| **NativeAOT required** | DuckDB loads extensions as native shared libraries. The entry point must be `[UnmanagedCallersOnly]`. There is no alternative to NativeAOT for .NET. |
| **Version coupling** | Even with the stable C API (forward compatible), the minimum version floor advances as you use newer API features. Each DuckDB release is a compatibility surface to test against. |
| **Platform-specific binaries** | One build per target platform (win-x64, linux-x64, osx-arm64, etc.). |
| **Unsigned extension UX** | Users must set `allow_unsigned_extensions = true` before loading — unless the extension is signed through the community repo (which doesn't support .NET builds) or a custom signing process. |
| **The two worlds split** | Stateful UDFs (`explore`, `search`, `git_*`, `mcp_call`, `llm`, `embed`) need host process context. This is a property of what these functions *do*, not how they're registered. No amount of code ownership changes this. |

### The Bridge Pattern

One architectural option not yet explored: an extension that **bridges back to the host**. The extension loads in DuckDB, but its functions make gRPC (or socket) calls back to the RepoQL host process. This would:

- Make RepoQL's full SQL surface available in **any DuckDB client** (CLI, Python, Node, etc.)
- Preserve access to host state (file system, git, embeddings, etc.) via the existing gRPC service
- Add network round-trip latency per function call (amortized across vectorized batches)
- Create a dependency: the extension only works when the RepoQL host is running

This is how RepoQL's MCP client already works — it's a thin transport layer that forwards to the host via gRPC. An extension could follow the same pattern.

Whether the latency trade-off is acceptable depends on usage patterns. Pure-computation functions (`glob_match`, `cosine_similarity`) would pay unnecessary latency. Stateful functions (`explore`, `search`) already involve I/O and the round-trip would be negligible.

**Hybrid approach**: pure-computation UDFs run natively in the extension, stateful UDFs bridge to the host. The extension ships both.

> Note: This pattern is synthesis, not a verified architecture. Whether gRPC client code is NativeAOT-compatible, and whether the connection lifecycle can be managed within extension init/teardown, needs investigation.

### What ExtensionKit Provides As Starting Material

Regardless of how far the extension idea goes, the vendored code contains useful components:

| Component | Value |
|-----------|-------|
| `DuckDBExtApiV1` struct | Complete C extension API bindings — every function pointer in the stable API, hand-written as a sequential struct layout. This is the hard part of C API interop. |
| Reader/writer framework | Typed `IDuckDBDataReader` / `IDuckDBDataWriter` implementations for all DuckDB types. Handles validity masks, columnar access. |
| Source generator | Roslyn incremental generator that produces the `[UnmanagedCallersOnly]` entry point. Handles the boilerplate of version strings, ABI type selection, extension metadata. |
| Type mapping | Bidirectional CLR ↔ DuckDB type mapping with factory-based reader/writer selection. |
| Build targets | MSBuild integration for cross-platform NativeAOT publish and metadata footer appending. |

The `DuckDBExtApiV1` struct alone is significant — it's the .NET equivalent of DuckDB's C header files, and getting it wrong causes segfaults.

## Gaps

| Area | What couldn't be determined |
|------|---------------------------|
| **NativeAOT performance** | No published benchmarks for .NET NativeAOT DuckDB extensions. |
| **gRPC in NativeAOT** | Whether `Grpc.Net.Client` is fully NativeAOT-compatible (for the bridge pattern). gRPC has had NativeAOT issues historically. |
| **Community repo .NET support** | Whether DuckDB CI could be configured to build .NET extensions is unclear. |
| **allow_unsigned_extensions in DuckDB.NET** | Whether DuckDB.NET exposes this connection configuration option is undocumented. |
| **net10.0 hard requirement** | Whether ExtensionKit's specific API usage requires .NET 10 or could target .NET 8/9. |
| **Production usage** | No evidence of anyone besides Giorgi having built an extension with ExtensionKit. |
| **Extension init error API** | Whether the stable C API provides a mechanism to propagate error messages from extension init (not just success/failure). |
