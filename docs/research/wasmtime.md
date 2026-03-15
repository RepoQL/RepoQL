---
description: Wasmtime ecosystem research for RepoQL's future WASM sandbox runtime — wasmtime-dotnet, Extism .NET SDK, WASI capabilities, Native AOT compatibility, JS-to-WASM paths, plugin architecture patterns.
tags: [wasmtime, wasm, wasi, extism, sandbox, native-aot, quickjs, jint]
audience: { human: 40, agent: 60 }
purpose: { research: 95, design: 5 }
---

# Wasmtime Ecosystem

Research for selecting a WASM runtime and migration path for RepoQL's sandbox platform. The current sandbox uses Jint (pure C# JS interpreter). The [sandbox platform design](../designs/future/sandbox-platform.md) is runtime-agnostic — "WASI is the future runtime for WASM plugin support."

*Research date: March 14, 2026*

## Context

RepoQL's sandbox lets agents run user-authored JavaScript for data transforms, analysis, and orchestration. Today this runs on Jint — fast, zero native deps, but soft isolation (advisory limits, no hardware boundary). The design says: build on Jint now, migrate to WASM later.

This document captures everything needed to evaluate that migration: the runtime APIs, the abstraction layers, the sandboxing primitives, the JS compilation paths, and the patterns used by production systems. It does not recommend a path — it presents what exists.

Constraints from the design:
- Must run on Windows, macOS, and Linux (developer laptop)
- Must support .NET Native AOT publishing
- Must provide genuine sandbox isolation (not advisory)
- DuckDB UDFs are synchronous — execution model must be synchronous
- Host function callbacks (`repoql.read()`, `repoql.write()`) are the primary interaction
- Module registry under `.repoql/modules/` with capability declarations

---

## wasmtime-dotnet

Official .NET bindings for Wasmtime (Bytecode Alliance). NuGet: `Wasmtime` v34.0.2 (August 2025). 1.1M total downloads. Targets .NET Standard 2.0/2.1, .NET 8, .NET 9.

> [wasmtime-dotnet GitHub](https://github.com/bytecodealliance/wasmtime-dotnet) — repository
> [NuGet](https://www.nuget.org/packages/Wasmtime) — package listing
> [API docs](https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.html) — .NET API reference

### Core Objects

Five `IDisposable` objects form the API:

| Class | Role | Thread safety |
|-------|------|---------------|
| `Engine` | Compilation engine. Created once per process or per configuration. | Thread-safe — share across threads |
| `Config` | Builder for engine configuration. Consumed when passed to `Engine`. | Single use |
| `Module` | Compiled WASM artifact. | Thread-safe — share across stores |
| `Store` | Execution state container. One per logical execution context. | Not concurrent — can be moved between threads |
| `Linker` | Wires imports before instantiation. Bound to an `Engine`. | Set up before use |
| `Instance` | Running module in a store. | Same as its store |

Minimal host pattern:

```csharp
using var engine = new Engine();
using var module = Module.FromBytes(engine, "plugin", wasmBytes);
using var linker = new Linker(engine);
using var store = new Store(engine);

linker.Define("host", "log",
    Function.FromCallback(store, (Caller caller, int addr, int len) => {
        var msg = caller.GetMemory("memory")!.ReadString(addr, len);
        Console.WriteLine(msg);
    }));

var instance = linker.Instantiate(store, module);
var analyze = instance.GetFunction<int, int>("analyze")!;
int result = analyze(inputOffset);
```

> [wasmtime-dotnet examples/hello](https://github.com/bytecodealliance/wasmtime-dotnet/tree/main/examples/hello) — minimal example

### Module Loading

| Method | Input |
|--------|-------|
| `Module.FromBytes(engine, name, ReadOnlySpan<byte>)` | Binary WASM |
| `Module.FromFile(engine, path)` | Binary WASM from file |
| `Module.FromStream(engine, name, Stream)` | Binary WASM from stream |
| `Module.FromText(engine, name, string)` | WAT text format |
| `Module.Validate(engine, ReadOnlySpan<byte>)` | Returns null (valid) or error string |
| `Module.Deserialize(engine, name, ReadOnlySpan<byte>)` | Pre-compiled artifact |
| `Module.DeserializeFile(engine, name, path)` | Pre-compiled from file |

Pre-compilation: `Module.Serialize()` → `byte[]` produces a cached artifact that avoids recompilation on subsequent loads. The `.cwasm` format is `mmap`-able — only executed code pages are paged in.

> [Wasmtime docs — caching](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html#method.cache_config_load) — compiled module cache

### Host Function Binding

**Typed callbacks** (up to 12 parameters, with or without `Caller`):

```csharp
// Simple — no caller context
Function.FromCallback(store, (int x, int y) => x + y);

// With Caller — access memory, fuel, store data
Function.FromCallback(store, (Caller caller, int addr, int len) => {
    var memory = caller.GetMemory("memory")!;
    var content = memory.ReadString(addr, len);
    // ... process content ...
    memory.WriteString(resultAddr, result);
});
```

**Untyped callbacks** for dynamic dispatch:

```csharp
Function.FromCallback(store,
    (Caller caller, ReadOnlySpan<ValueBox> args, Span<ValueBox> results) => { ... },
    parameterKinds, resultKinds);
```

**Type mappings:**

| WASM `ValueKind` | .NET type |
|------------------|-----------|
| `Int32` | `int` |
| `Int64` | `long` |
| `Float32` | `float` |
| `Float64` | `double` |
| `V128` | `V128` (struct) |
| `FuncRef` | `Function` |
| `ExternRef` | any reference type |

**Strings are not a native WASM type.** String passing uses the pointer+length convention: WASM passes `(int address, int length)`, host reads via `memory.ReadString(address, length)`. No automatic marshalling.

**Caller context** (`ref struct Caller`): Available when included as the first callback parameter. Provides `GetMemory(name)`, `GetFunction(name)`, `TryGetMemorySpan<T>()`, `Fuel` (get/set), `GetData()`/`SetData(object?)`.

**Store-attached user data**: Arbitrary `object?` attached at `Store` construction, readable/writable from callbacks via `Caller.GetData()`.

> [wasmtime-dotnet examples/storedata](https://github.com/bytecodealliance/wasmtime-dotnet/tree/main/examples/storedata) — store data pattern
> [wasmtime-dotnet examples/consumefuel](https://github.com/bytecodealliance/wasmtime-dotnet/tree/main/examples/consumefuel) — fuel from callbacks

### Memory Model

WASM linear memory is a contiguous byte buffer. Page size: 64 KiB (WASM spec).

```csharp
var mem = new Memory(store, minimum: 1, maximum: 10, is64Bit: false);
```

**Read/write API:**

```csharp
memory.ReadByte(addr)              // byte
memory.ReadInt32(addr)             // int (LE)
memory.ReadString(addr, len)       // UTF-8 default
memory.ReadNullTerminatedString(addr)
memory.Read<T>(addr)               // any unmanaged struct

memory.WriteByte(addr, value)
memory.WriteInt32(addr, value)
memory.WriteString(addr, value)    // returns bytes written
memory.Write<T>(addr, value)

Span<byte> span = memory.GetSpan(addr, len);
Span<T> typed = memory.GetSpan<T>(addr, len);
```

**Critical invariant:** Spans and pointers become invalid after any grow operation, including growth triggered by WASM execution. Do not cache across calls.

**64-bit memory:** `is64Bit: true` with `Config.WithMemory64(true)`.

### WASI Support

Configured via `WasiConfiguration` (fluent builder), activated by `linker.DefineWasi()`:

```csharp
linker.DefineWasi();
store.SetWasiConfiguration(
    new WasiConfiguration()
        .WithArgs("myapp", "--flag")
        .WithEnvironmentVariable("HOME", "/sandbox")
        .WithInheritedStandardOutput()
        .WithPreopenedDirectory("/host/data", "/data",
            WasiDirectoryPermissions.Read,
            WasiFilePermissions.Read));
```

| Method | Effect |
|--------|--------|
| `WithArg(s)` / `WithArgs(...)` | Set argv |
| `WithInheritedArgs()` | Inherit host process args |
| `WithEnvironmentVariable(k, v)` | Set specific env var |
| `WithInheritedEnvironment()` | Inherit all host env vars (breaks isolation) |
| `WithStandardInput(path)` / `WithInheritedStandardInput()` | stdin |
| `WithStandardOutput(path)` / `WithInheritedStandardOutput()` | stdout |
| `WithStandardError(path)` / `WithInheritedStandardError()` | stderr |
| `WithPreopenedDirectory(host, guest, dirPerms, filePerms)` | Capability-based FS |

Default (no configuration): no args, no env, no filesystem, stdout/stderr silently discarded.

**WASI `proc_exit`:** throws `WasmtimeException` with `ExitCode` set (distinguishable from traps).

See [WASI Sandboxing](#wasi-sandboxing) for the preopened directory security model.

### Resource Limits

**Fuel (instruction counting):**

```csharp
var engine = new Engine(new Config().WithFuelConsumption(true));
store.Fuel += 5000UL;
// After execution:
ulong consumed = store.GetConsumedFuel();
```

Most instructions cost 1 fuel unit. Control flow (`nop`, `drop`, `block`, `loop`) costs 0. Customizable via `Config.OperatorCost()`. When fuel reaches zero: `TrapException` with `TrapCode.OutOfFuel`. Fully deterministic — same module + same fuel = same interruption point.

Overhead: "notably more than epoch interruption" — per-instruction check.

> [Wasmtime docs — fuel](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html#method.consume_fuel) — fuel configuration

**Epoch interruption (time-based):**

```csharp
var engine = new Engine(new Config().WithEpochInterruption(true));
store.SetEpochDeadline(1);

// From a timer thread:
engine.IncrementEpoch();  // triggers trap in expired stores
```

Checks at function entry and loop back-edges. ~10% overhead (vs ~20-30% for fuel). Non-deterministic — wall-time-based. Three behaviors on deadline: trap (default), callback (can extend deadline), async yield.

**Store limits:**

```csharp
store.SetLimits(
    memorySize: 100 * 1024 * 1024,  // max linear memory bytes
    tableElements: 10_000,
    instances: 100,
    tables: 100,
    memories: 10);
```

**Stack:** `Config.WithMaximumStackSize(bytes)`.

**Compiler settings:** `WithOptimizationLevel(Speed / SpeedAndSize / None)`, `WithCompilerStrategy(Auto / Cranelift)`, `WithCacheConfig(path)` for disk-based module cache.

### Error Handling

```
Exception
  └── WasmtimeException       — base (compilation, instantiation, link, WASI exit)
        └── TrapException     — WASM runtime fault
```

**`WasmtimeException`** properties: `Message`, `Frames` (`IReadOnlyList<TrapFrame>?`), `ExitCode` (`int?` — non-null for WASI `proc_exit`).

When a .NET exception escapes a host callback, it becomes the inner exception via `Function.CallbackErrorCause`.

**`TrapCode` values:** `StackOverflow`, `MemoryOutOfBounds`, `TableOutOfBounds`, `IntegerOverflow`, `IntegerDivisionByZero`, `Unreachable`, `Interrupt` (epoch), `OutOfFuel`, `BadSignature`, `NullReference`.

**`TrapFrame`:** `FunctionName` (string?), `FunctionOffset`, `ModuleName`, `ModuleOffset`.

```csharp
try {
    run();
} catch (TrapException ex) when (ex.Type == TrapCode.OutOfFuel) {
    // quota exceeded
} catch (WasmtimeException ex) when (ex.ExitCode.HasValue) {
    // WASI proc_exit
} catch (WasmtimeException ex) {
    // compilation, link, or runtime error
}
```

### Performance Characteristics

**Compilation:** Cranelift (optimizing) or Winch (fast-compile baseline). Pre-compilation eliminates this from the hot path.

**Instantiation — three acceleration techniques:**

| Technique | What it does |
|-----------|-------------|
| Pooling allocator | Pre-allocates memory/tables/stacks. New instances take from pool. |
| Copy-on-write heap images | Initial memory mapped CoW; pages copied only on first write. Not supported on Windows. |
| `InstancePre` | Resolves all imports once. Only resource allocation remains. |

**Execution speed:** Cranelift-compiled WASM runs at ~80-95% of native speed for compute-bound workloads. Gap widens for host-call-heavy code (ABI transition per call).

**Memory overhead per instance:**
- Virtual: 4 GiB reserved by default for 32-bit linear memory (on 64-bit hosts)
- Physical: only touched pages (lazy mmap)
- Stack: 512 KiB default
- With pooling: near-zero incremental cost per additional instance

**Platform support:** Windows x64/ARM64 (ARM64 added in v34.0.2), Linux x64, macOS x64/ARM64.

---

## Extism .NET SDK

High-level plugin framework built on Wasmtime. NuGet: `Extism.Sdk` v1.10.0 (December 2025) + `Extism.runtime.all` (native binaries). 19.4K total downloads. Apache 2.0.

> [Extism .NET SDK GitHub](https://github.com/extism/dotnet-sdk) — repository
> [NuGet](https://www.nuget.org/packages/Extism.Sdk) — package listing
> [Extism docs](https://extism.org/docs/concepts/manifest) — manifest reference

### Plugin Model

Plugins are loaded from a `Manifest` — a typed C# object describing WASM sources, permissions, and limits:

```csharp
var manifest = new Manifest(
    new PathWasmSource("plugin.wasm", "main"))
{
    AllowedHosts = new[] { "api.example.com" },
    AllowedPaths = new Dictionary<string, string> { ["./data"] = "/data" },
    MemoryOptions = new MemoryOptions { MaxPages = 100 },
    Timeout = TimeSpan.FromSeconds(5),
    Config = new Dictionary<string, string> { ["key"] = "value" }
};

using var plugin = new Plugin(manifest, hostFunctions, withWasi: true);
string result = plugin.Call("analyze", inputJson);
```

Three `WasmSource` types: `PathWasmSource` (local file), `UrlWasmSource` (HTTP/S with optional headers), `ByteArrayWasmSource` (raw bytes). All support `Name` and `Hash` (SHA-256 verification).

**Calling functions** — all boundaries are `bytes in → bytes out`:

```csharp
// Raw bytes
ReadOnlySpan<byte> result = plugin.Call("func", inputBytes);

// String convenience
string result = plugin.Call("func", inputString);

// AOT-safe typed (source-generated JsonTypeInfo)
var output = plugin.Call<MyInput, MyOutput>("func", input,
    MyContext.Default.MyInput, MyContext.Default.MyOutput);

// Reflection-based typed (NOT AOT-safe — marked [RequiresUnreferencedCode])
var output = plugin.Call<MyInput, MyOutput>("func", input);
```

### Host Functions

Two layers — low-level constructor and high-level factory:

```csharp
// High-level: infer types from delegate
var hostFn = HostFunction.FromMethod("kv_read", kvStore,
    (CurrentPlugin plugin, long keyOffset) => {
        var key = plugin.ReadString(keyOffset);
        var value = kvStore[key];
        return plugin.WriteBytes(value);  // returns offset
    });

// Per-call host context (distinct from per-registration userData)
plugin.CallWithHostContext("func", input, new MyCallContext());
// Inside host function:
var ctx = plugin.GetCallHostContext<MyCallContext>();
```

### Memory Management

Extism manages its own allocation space within the plugin's linear memory. Engineers never touch raw pointers:

| Method | Direction | Notes |
|--------|-----------|-------|
| `plugin.ReadString(offset)` | Plugin→Host | UTF-8 from Extism memory |
| `plugin.ReadBytes(offset)` | Plugin→Host | `Span<byte>` |
| `plugin.WriteString(string)` → `long` | Host→Plugin | Allocates, writes, returns offset |
| `plugin.WriteBytes(Span<byte>)` → `long` | Host→Plugin | Allocates, writes, returns offset |
| `plugin.AllocateBlock(length)` → `long` | Manual | Explicit allocation |
| `plugin.FreeBlock(offset)` | Manual | Early free |

Per-call lifecycle: Extism resets memory on each call. Input copied in, output readable until next call. Allocations within host functions survive until call completes.

### Plugin Lifecycle

**`CompiledPlugin` for performance:** Compile once, instantiate many:

```csharp
using var compiled = new CompiledPlugin(manifest, functions, withWasi: true);
using var plugin = compiled.Instantiate();  // ~266ms vs ~27,500ms cold
```

`CompiledPlugin` is the right pattern for multi-tenant or repeated execution scenarios.

**State:** Plugins are stateful within an instance — globals and Extism vars persist across calls. Memory is reset per-call. Not thread-safe — create separate instances for concurrent use.

**Cancellation:** `CancellationToken` on all `Call` overloads. Internally calls `extism_plugin_cancel`.

**Fuel:** `PluginIntializationOptions.FuelLimit = N` caps instruction count. Exceeding throws `ExtismException`.

### Native AOT Support

`IsAotCompatible = true` since March 2024 (PR #77). The solution:

1. `ManifestJsonContext : JsonSerializerContext` with `[JsonSerializable]` for all manifest types — compile-time source generation
2. Reflection-based `Call<TInput, TOutput>` overloads marked `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`
3. AOT-safe overloads added accepting `JsonTypeInfo<T>` from caller's source-generated context

| Overload | AOT Safe? |
|----------|-----------|
| `Call(string, ReadOnlySpan<byte>)` | Yes |
| `Call(string, string)` | Yes |
| `Call<TIn, TOut>(string, TIn, JsonTypeInfo<TIn>, JsonTypeInfo<TOut?>)` | Yes |
| `Call<TIn, TOut>(string, TIn, JsonSerializerOptions?)` | No |

> [Extism PR #77](https://github.com/extism/dotnet-sdk/pull/77) — AOT compatibility changes

### Plugin Development Kits (PDKs)

Plugins can be written in: Rust, Go, C/C++, Zig, Haskell, AssemblyScript, Python, JavaScript/TypeScript, C#/F# (.NET). The host sees only WASM — language is irrelevant.

**JavaScript PDK** — see [JS-to-WASM Paths](#js-to-wasm-paths).

**C#/.NET PDK** (`extism/dotnet-pdk`) — experimental. Requires `dotnet workload install wasi-experimental` + WASI SDK. Exports use `[UnmanagedCallersOnly(EntryPoint = "name")]`. JSON requires source generation (trimming). WASI must always be enabled by host (known limitation).

> [Extism JS PDK](https://github.com/extism/js-pdk) — JavaScript plugin development
> [Extism .NET PDK](https://github.com/extism/dotnet-pdk) — C# plugin development (experimental)

### Extism vs Raw wasmtime-dotnet

| Dimension | Extism adds | Extism removes |
|-----------|-------------|----------------|
| Memory | Offset-based abstraction, no raw pointers | Direct linear memory access |
| Loading | Manifest with hash verification, URL sources | Raw module compilation control |
| HTTP | Built-in, controlled by `AllowedHosts` | (wasmtime-dotnet has no HTTP) |
| Fuel | Exposed via options | Epoch interruption, fine-grained fuel callbacks |
| Types | Bytes in / bytes out convention | Multi-value returns, arbitrary WASM types |
| State | Vars API across calls within instance | WASM threads, shared memory |
| PDKs | 10+ language PDKs with consistent ABI | Component Model, WIT interfaces |
| AOT | Working today | (wasmtime-dotnet PR #348 pending) |

Native binary cost: ~30MB per platform (Windows x64, Linux x64/arm64/musl-arm64, macOS x64/arm64). No Windows ARM64 — buildable from source (Rust cross-compile `aarch64-pc-windows-msvc`).

---

## WASI Sandboxing

WASI provides the security model for sandboxed execution. Two versions exist.

### WASIp1 (Preview 1)

Monolithic module (`wasi_snapshot_preview1`). POSIX-inspired, based on CloudABI/Capsicum capability model. Widely deployed, still the default in most toolchains.

**Has:** Filesystem (via preopened dirs), args, env, clock, random, poll.
**Does not have:** Real networking (stub functions exist but are unimplemented in most runtimes).

> [WASI GitHub](https://github.com/WebAssembly/WASI) — specification repository

### WASIp2 (Preview 2)

Modular — each capability is a separate WIT interface. Requires the Component Model. Stable since early 2024.

**Has:** Everything in p1, plus TCP/UDP sockets (`wasi:sockets`), HTTP (`wasi:http`), DNS resolution, key-value storage, SQL, blob storage, structured logging.

**wasmtime-dotnet status:** WASIp1 only. Issue #324 tracks Component Model support.

### Preopened Directories

The core filesystem sandboxing primitive. Understanding is essential for RepoQL's `.repoql/modules/` and `.repoql/tmp/` access patterns.

**Mechanism:**
1. Host opens specific directories and passes them as file descriptors to the WASM module
2. WASM libc builds an internal path→fd mapping from preopened fds
3. All `open()` calls resolve relative to a preopened fd — `path_open(preopened_fd, 0, "file.txt", ...)`
4. Path traversal (`../../etc/passwd`) beyond the preopened root returns `ENOTCAPABLE` at the syscall level
5. No preopen = no filesystem access at all

```csharp
config.WithPreopenedDirectory(
    "/host/path/.repoql/tmp", "/tmp",
    WasiDirectoryPermissions.Read | WasiDirectoryPermissions.Write,
    WasiFilePermissions.Read | WasiFilePermissions.Write);

config.WithPreopenedDirectory(
    "/host/path/src", "/src",
    WasiDirectoryPermissions.Read,
    WasiFilePermissions.Read);
```

**Permissions** are `[Flags]` enums:
- `WasiDirectoryPermissions`: `Read` (list entries), `Write` (create files)
- `WasiFilePermissions`: `Read` (read content), `Write` (write content)

**Security guarantee:** A module with only `/tmp` preopened cannot access anything outside `/host/path/.repoql/tmp` on the host, regardless of path construction. This is a hard syscall-level boundary.

### Default Configuration

| Capability | Default | Effect |
|------------|---------|--------|
| Filesystem | No preopens | No file access |
| Args | Empty | No command-line arguments |
| Env vars | Empty | No environment variables |
| stdin | Disconnected | Reads return EOF |
| stdout/stderr | Disconnected | Writes silently discarded |
| Network | None (WASIp1) | No networking available |

> [Wasmtime WASI docs](https://docs.wasmtime.dev/wasi-tutorial.html) — WASI tutorial

---

## Native AOT Compatibility

### wasmtime-dotnet — PR #348 (Open)

PR #348 ("Support for native AOT & static linking") by andrewmd5. Adds:
- MSBuild targets for static linking of native Wasmtime library
- `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` annotations on reflection paths
- `[UnconditionalSuppressMessage]` suppressions where correctness is verified
- Working Native AOT Hello World example
- Dependency bump to Wasmtime 38.0.3

> [wasmtime-dotnet PR #348](https://github.com/bytecodealliance/wasmtime-dotnet/pull/348) — Native AOT support
> [wasmtime-dotnet Issue #293](https://github.com/bytecodealliance/wasmtime-dotnet/issues/293) — AOT compatibility tracking

**Reflection patterns that break under AOT:**

| Pattern | Location | AOT violation |
|---------|----------|---------------|
| `typeof(T<,>).MakeGenericType(...)` + `Activator.CreateInstance(...)` | `ReturnTypeFactory.cs` | IL3050 — runtime type construction |
| `GetMethod().MakeGenericMethod(args).CreateDelegate(...)` | `ReturnTypeFactory.cs` | IL3050 — runtime generic method |
| Tuple factory with runtime-determined arity | `ReturnTypeFactory.cs` | IL3050 — unknowable generic instantiations |
| `GetGenericTypeDefinition()` interface scanning | Result type resolution | IL2026 — trimmed type metadata |

The PR's approach: annotation + suppression. The T4-generated callback overloads (up to 12 parameters) pre-instantiate all needed generic types, making the suppressions safe in practice. The author recommends dropping .NET Standard 2.1 to use static abstract interface members (eliminating `Activator.CreateInstance`). A reviewer pushed back citing Unity compatibility.

**Practical status:** A commenter confirmed wasmtime-dotnet works in AWS Lambda with .NET 8 Native AOT for their use case. The reflection in `ReturnTypeFactory.cs` is safe because the T4-generated code ensures all generic instantiations exist at compile time.

### Extism .NET SDK — Working Today

`IsAotCompatible = true` since PR #77 (March 2024). See [Extism Native AOT Support](#native-aot-support).

### .NET AOT Features Reference

| Feature | Since | What it does |
|---------|-------|-------------|
| `LibraryImport` | .NET 7 | Source-generates P/Invoke marshalling (fully AOT safe) |
| `JsonSerializerContext` | .NET 6 | Source-generates JSON serialization |
| `[RequiresDynamicCode]` | .NET 6 | Documents runtime code generation dependency |
| `[RequiresUnreferencedCode]` | .NET 5 | Documents trimmed member dependency |
| `[UnconditionalSuppressMessage]` | .NET 5 | Only valid way to suppress trimmer warnings |
| `[DynamicallyAccessedMembers]` | .NET 5 | Tells trimmer what reflection members to keep |
| Static abstract interface members | .NET 7 | Replaces runtime factory dispatch with generic constraints |
| `IsAotCompatible` in csproj | .NET 8 | Enables all AOT analyzers for a library |
| `JsonStringEnumConverter<TEnum>` | .NET 7 | AOT-safe enum string conversion (non-generic version fails) |
| `<DirectPInvoke>` / `<NativeLibrary>` | .NET 8 | Configure static linking for P/Invoke |

**P/Invoke under AOT:** `DllImport` works but emits IL stubs at runtime. `LibraryImport` (.NET 7+) is fully source-generated. Extism currently uses `DllImport` (targets .NET Standard 2.0). Direct P/Invoke binding via `<DirectPInvoke Include="extism" />` enables static linking and removes lazy binding overhead.

---

## JS-to-WASM Paths

If RepoQL migrates from Jint to a WASM sandbox, user-authored JavaScript must run inside WASM. There are structurally different approaches.

### Approach 1: Pre-compiled JS (Extism JS PDK)

The Extism JS PDK compiles JavaScript source into a self-contained `.wasm` file at build time.

**Pipeline:**
1. Parse `.d.ts` interface file to identify exports/imports
2. Inject host-function metadata into user JS
3. Run **Wizer** — instantiates QuickJS-ng with user JS pre-loaded, takes a memory snapshot
4. Generate WebAssembly shim bridging exported/imported function names
5. Merge with `wasm-merge` (Binaryen), optionally `wasm-opt`

**Engine:** QuickJS-ng (via `rquickjs` Rust binding). ES2020 features: nullish coalescing, optional chaining, BigInt, `Promise.allSettled`.

**Available APIs:** `Host.inputString/outputString`, `Config.get(key)`, `Var.getString/set`, `Memory.fromString/find`, `Http.request()`, `fetch()` (synchronous), `console`, `crypto.subtle.digest`, `TextEncoder/Decoder`, `URL`, typed arrays, `Date`, `Map`, `Set`.

**Not available:** Event loop, `setTimeout/setInterval`, Node.js APIs, DOM, WebSocket, dynamic `import()`.

**Module size:** ~1-3 MB (QuickJS-ng engine + bytecode baked in, estimated).

**Key constraint:** JS must be pre-compiled. Dynamic user-supplied scripts require a different approach (load a "JS evaluator" plugin and pass JS source as input bytes).

> [Extism JS PDK](https://github.com/extism/js-pdk) — build tool and docs
> [bytecodealliance/wizer](https://github.com/bytecodealliance/wizer) — pre-initialization tool

### Approach 2: Pre-compiled JS (Javy)

Bytecode Alliance JS-to-WASM toolchain (originally Shopify). Takes `.js`, produces `.wasm`.

**Engine:** QuickJS (original Bellard). ES2020.

**Two linking modes:**

| Mode | Module size | How it works |
|------|-------------|-------------|
| Static | ≥ 869 KB | QuickJS engine bundled inside the `.wasm` |
| Dynamic | 1–16 KB | Module imports from shared `javy_quickjs_provider_vN.wasm` |

Dynamic linking: the host preloads the provider (engine) once, then instantiates tiny user modules that reference it. Shopify's use case: 256 KB size limit → dynamic linking reduced user modules to "220 bytes plus bytecode size."

**I/O model:** Strictly stdin/stdout (WASI). Not a function-call model — the host writes bytes to stdin, invokes `_start`, reads stdout. More awkward than Extism's `Call` API.

**Performance:** Shopify measured JS-via-Javy at ~3x slower than equivalent Rust WASM. Still under their 5ms budget for real-world functions.

> [Javy GitHub](https://github.com/bytecodealliance/javy) — repository
> [Shopify Engineering blog](https://shopify.engineering/javascript-in-webassembly-for-shopify-functions) — architecture and motivation

### Approach 3: JS Interpreter in WASM (Dynamic Eval)

Compile QuickJS itself to WASM and interpret JS dynamically at runtime — no pre-compilation of user JS.

**How:** Build QuickJS with `--target wasm32-wasi`, producing a single `quickjs.wasm`. Load it once via Wasmtime. Pass JS source as WASI stdin or linear memory argument. QuickJS interprets it.

**Cold start:** Must parse + compile JS to bytecode on each invocation (unless a bytecode caching layer is added). Wizer benchmarks show 6x cold-start improvement for pre-initialized modules.

**This most closely mirrors how Jint works today** — dynamic eval of arbitrary JS strings, just inside a WASM sandbox. No build step required for user scripts.

**QuickJS baseline:** ~400 KB compiled WASM (bellard.org states 367 KiB x86 machine code for hello world).

> [bellard.org/quickjs](https://bellard.org/quickjs/) — QuickJS homepage

### Approach 4: StarlingMonkey (SpiderMonkey in WASM)

SpiderMonkey-based runtime targeting WASI 0.2 and the Component Model. Production-deployed by Fastly and Fermyon Spin.

Considerably larger than QuickJS-based approaches (SpiderMonkey is a full optimizing engine). Better runtime throughput for compute-heavy workloads. Oriented toward edge-function patterns, not general-purpose plugin sandboxing.

> [StarlingMonkey GitHub](https://github.com/nicolo-ribaudo/nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr-nicr/nicr?actorId=nicr) — Bytecode Alliance

### Comparison

| Approach | Engine | Compiled when? | Module size | .NET integration | Cold start | Dynamic eval? |
|----------|--------|----------------|-------------|------------------|------------|---------------|
| Extism JS PDK | QuickJS-ng | Build time (Wizer) | ~1-3 MB | Extism.Sdk (clean) | Fast (pre-initialized) | No |
| Javy static | QuickJS | Build time | ≥ 869 KB | wasmtime (stdin/stdout) | Medium | No |
| Javy dynamic | QuickJS | Build time | 1-16 KB user | wasmtime (stdin/stdout) | Fast (shared engine) | No |
| QuickJS-in-WASM | QuickJS | Runtime | ~400-600 KB engine | wasmtime (host functions) | Medium (parse each call) | Yes |
| StarlingMonkey | SpiderMonkey | Optional pre-bake | Large | wasmtime (Components) | Slow | Yes |
| Jint (current) | CLR interpreter | Runtime | N/A (in-process) | Native | Fast | Yes |

**The migration question:** RepoQL currently uses Jint for dynamic eval — agents write JS and execute it immediately. Pre-compilation approaches (Extism JS PDK, Javy) require a build step between writing JS and running it. Dynamic eval approaches (QuickJS-in-WASM) preserve the current workflow but add WASM overhead. The module registry design (`.repoql/modules/`) could use pre-compilation (register compiles JS → WASM), while the sandbox tool could use dynamic eval for ad-hoc scripts.

---

## Plugin Architecture Patterns

How production systems use Wasmtime for sandboxed execution.

### Pattern 1: Event Subscription (Zellij)

Zellij uses Wasmtime directly. Plugins subscribe to events (keyboard, tab changes, pane changes) and react via exported handler functions. Communication: protobuf-serialized messages written into plugin linear memory. Over 200 host commands exposed (tab/pane lifecycle, file ops, inter-plugin messaging, layout management). Plugins declare required permissions; users grant at load time.

> [Zellij plugin API proto](https://github.com/zellij-org/zellij/blob/main/zellij-utils/src/plugin_api/plugin_command.proto) — host command schema

### Pattern 2: Pure Function (Shopify Functions, OPA)

Stateless input→output transformation. No host callbacks beyond I/O. No persistent state.

Shopify: JSON payload in (commerce context) → mutations payload out. 5ms execution limit. Pre-compiled at deploy time. Simplest sandbox — the WASM spec itself is the security boundary.

OPA: Rego policy compiled to WASM. Exports `eval(input, data) → result`. Sub-millisecond evaluation. Host provides built-in functions that aren't compiled into the module. Memory: 512KB–4MB per instance.

### Pattern 3: Callback-Driven with Host Query API (Envoy / proxy-wasm)

Plugin exports lifecycle callbacks. Host calls them on events. Plugin calls back into host functions to query/modify state. Async I/O modeled as: plugin calls `proxy_http_call` → host processes → host calls `proxy_on_http_call_response`.

**This pattern most closely matches RepoQL's model** — the host (DuckDB + graph) is the authority, plugins query it via `repoql.read()` / `repoql.write()` callbacks.

> [proxy-wasm spec](https://github.com/proxy-wasm/spec) — ABI specification

### Pattern 4: Component Model with WIT (Fermyon Spin)

Type-safe, codegen-driven interfaces. Each plugin is a WASM Component implementing a WIT-defined interface. No manual memory offset management — the Canonical ABI handles string/list/record passing.

```wit
world analysis-plugin {
    import host: interface {
        read-file: func(uri: string, budget: u32) -> result<string, string>;
        emit-annotation: func(uri: string, line: u32, message: string);
    };
    export analyze: func(file-uri: string) -> list<annotation>;
}
```

Higher upfront cost (WIT authoring, bindgen toolchain). Big payoff in ergonomics — eliminates the entire category of "how do I pass a string across the WASM boundary."

### Synthesis for RepoQL

| Dimension | Options | Notes |
|-----------|---------|-------|
| Runtime | wasmtime-dotnet (low-level), Extism (batteries-included) | Extism adds convenience but removes fuel/epoch granularity |
| Execution model | Fuel (deterministic, ~20-30% overhead) vs Epoch (~10%, non-deterministic) | Fuel for auditable limits; epoch for performance |
| Module format | Core WASM vs Component Model | Component Model requires WIT but eliminates ABI friction |
| State across calls | Pooled instance (reuse, stateful) vs fresh (isolated, slower without CoW) | CoW not supported on Windows |
| Host API surface | Narrow (data only) vs broad (query graph) | Envoy-style: narrow default, explicit capability opt-in |

**Pre-compilation for developer laptops:** Compile plugin WASM once at load/register time, cache `.cwasm` artifact. Subsequent loads use `Module.DeserializeFile()` — no JIT overhead. Combined with `InstancePre`, instantiation per analysis run can be sub-millisecond.

---

## Gaps

- **wasmtime-dotnet pooling allocator from .NET**: Rust API supports instance pooling. Whether .NET bindings expose this is undocumented.
- **Copy-on-write on Windows**: Not supported. Instantiation cost per fresh instance may be higher on Windows than Linux/macOS.
- **Exact Extism JS PDK WASM output size**: No published measurement. Estimated ~1-3 MB based on QuickJS-ng size.
- **wasmtime-dotnet PR #348 merge timeline**: Open 4+ months. .NET Standard 2.1 debate unresolved.
- **QuickJS-in-WASM cold start from .NET**: No published benchmark with .NET host specifically. QuickJS claims "complete lifecycle in under 300 microseconds" natively; WASM adds compilation overhead.
- **Extism fuel limit granularity**: Whether Extism exposes per-operator cost customization (wasmtime-dotnet does via `Config.OperatorCost()`).
- **StarlingMonkey WASM module size**: Not published. SpiderMonkey compiled to WASM is "considerably larger" than QuickJS — no specific number found.
- **Library compatibility in QuickJS-ng**: No published test suites for whether specific npm libraries work. Each needs testing.
- **Component Model in wasmtime-dotnet**: Issue #324 tracks it. Current status and timeline unclear.
- **Extism Windows ARM64**: Not shipped. Buildable via Rust cross-compilation but untested in CI.
