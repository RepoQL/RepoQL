---
name: build-wasm-sandbox
description: "Build the QuickJS-NG WASM evaluator module. Use when evaluator.c changes, upgrading QuickJS-NG, or the WASM binary is missing."
zones: { K: 40, P: 45, C: 10, W: 5 }
---

# Build WASM Sandbox

Compile QuickJS-NG + evaluator.c into `quickjs-evaluator.wasm` for the RepoQL execute tool.

The WASM binary is checked into the repo and rarely rebuilt. Rebuild when:
- `sandbox/wasm/evaluator.c` changes (new host imports, ABI changes)
- Upgrading QuickJS-NG (new engine version)
- The binary is missing from `sandbox/wasm/dist/`

## Prerequisites

- **wasi-sdk** extracted at `sandbox/wasm/toolchain/wasi-sdk-*-x86_64-windows/`
  - Download: `https://github.com/WebAssembly/wasi-sdk/releases` (pick your platform's tar.gz)
  - Extract into `sandbox/wasm/toolchain/`
- **QuickJS-NG source** at `sandbox/wasm/quickjs/`
  - Clone: `git clone --depth 1 https://github.com/quickjs-ng/quickjs sandbox/wasm/quickjs`

Both directories are gitignored — they're build-time dependencies, not source.

## Steps

### 1. Set WASI_SDK_PATH

Find the extracted wasi-sdk directory. It contains `bin/clang.exe`.

```bash
export WASI_SDK_PATH="sandbox/wasm/toolchain/wasi-sdk-31.0-x86_64-windows"
```

→ Verify: `ls "$WASI_SDK_PATH/bin/clang.exe"` succeeds

### 2. Verify QuickJS-NG source

The compiler needs these files from `sandbox/wasm/quickjs/`:

| File | Role |
|------|------|
| `quickjs.c` + `quickjs.h` | JS engine core |
| `libregexp.c` | Regular expression engine |
| `libunicode.c` | Unicode tables |
| `dtoa.c` | Number ↔ string conversion |

→ Verify: all four `.c` files exist
→ Note: older QuickJS had `cutils.c` — QuickJS-NG inlined it into `quickjs.c` (only `cutils.h` remains)

### 3. Compile

```bash
cd sandbox/wasm

"$WASI_SDK_PATH/bin/clang.exe" \
  --target=wasm32-wasip1 \
  -O2 \
  -DCONFIG_VERSION=\"2024\" \
  -I./quickjs \
  evaluator.c \
  quickjs/quickjs.c \
  quickjs/libregexp.c \
  quickjs/libunicode.c \
  quickjs/dtoa.c \
  -o dist/quickjs-evaluator.wasm \
  -mexec-model=reactor \
  -Wl,--export=wasm_alloc \
  -Wl,--export=wasm_dealloc \
  -Wl,--export=evaluate
```

→ Verify: exit code 0, no output (clean compilation)
→ If failed: check that evaluator.c `#include "quickjs.h"` resolves via `-I./quickjs`

## Capsule: CompilerFlags

**Invariant**: These flags are load-bearing. Changing them changes the ABI.

| Flag | Why it matters |
|------|---------------|
| `--target=wasm32-wasip1` | WASI Preview 1 — the stable ABI. Gives QuickJS a minimal libc. |
| `-mexec-model=reactor` | Library mode, not program. No `_start` — host calls our exports. Without this, wasmtime expects a main function. |
| `-Wl,--export=wasm_alloc` | Only these three functions are visible to the .NET host. Everything else stays private inside the module. |
| `-Wl,--export=wasm_dealloc` | |
| `-Wl,--export=evaluate` | |
| `-O2` | Size/speed optimization. The module is ~1.2MB at -O2. |

//BOUNDARY: `-mexec-model=reactor` is the most critical flag. Without it, the module won't load in wasmtime as a library.

### 4. Verify output

```bash
ls -la dist/quickjs-evaluator.wasm
```

→ Verify: file exists, ~1.2MB
→ Verify exports (optional): `llvm-nm --extern-only dist/quickjs-evaluator.wasm | grep -E "evaluate|wasm_alloc|wasm_dealloc|repoql_log"`
  - `T evaluate` (exported)
  - `T wasm_alloc` (exported)
  - `T wasm_dealloc` (exported)
  - `U repoql_log` (imported from host)

### 5. Embed in .NET project

The WASM binary is referenced as an embedded resource in `src/RepoQL.Sandbox/RepoQL.Sandbox.csproj`:

```xml
<ItemGroup>
    <EmbeddedResource Include="..\..\sandbox\wasm\dist\quickjs-evaluator.wasm"
                      LogicalName="quickjs-evaluator.wasm" />
</ItemGroup>
```

→ Verify: `dotnet build src/RepoQL.Sandbox/RepoQL.Sandbox.csproj` succeeds

## Completion

The WASM binary is ready when:
- `sandbox/wasm/dist/quickjs-evaluator.wasm` exists (~1.2MB)
- It exports `evaluate`, `wasm_alloc`, `wasm_dealloc`
- It imports `env.repoql_log`
- The .NET project builds with it embedded

## Capsule: ABI Contract

**Invariant**: The .NET host (`WasmtimeSandbox`) and the WASM module must agree on this contract.

**Exports** (WASM → Host):
- `wasm_alloc(i32 size) → i32 ptr` — allocate WASM memory for host to write into
- `wasm_dealloc(i32 ptr, i32 size)` — free WASM memory
- `evaluate(i32 src_ptr, i32 src_len, i32 input_ptr, i32 input_len) → i64` — returns packed `(result_ptr << 32) | result_len`

**Imports** (Host → WASM):
- `env.repoql_log(i32 level, i32 msg_ptr, i32 msg_len)` — console.log/warn/error. Level: 0=info, 1=warn, 2=error.

**Result format**: JSON string in WASM memory. Success: the JS result JSON-stringified. Error: `{"error":{"kind":"...","message":"...","suggestion":"..."}}`

//BOUNDARY: Changing any export signature requires updating both evaluator.c AND WasmtimeSandbox.cs.
