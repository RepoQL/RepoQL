# QuickJS-NG WASM Evaluator

This directory contains the C wrapper and build script for a small WASI reactor module that embeds QuickJS-NG and exposes a single `evaluate` entry point for the .NET host.

## Contents

- `evaluator.c`: Thin QuickJS-NG wrapper compiled to `wasm32-wasip1`
- `build.sh`: Local build script for producing the evaluator module
- `quickjs/`: Expected location of the QuickJS-NG source tree
- `dist/`: Build output directory for the generated `.wasm`

## Prerequisites

You need both of these installed locally before building:

1. `wasi-sdk`
2. A QuickJS-NG source checkout copied into `sandbox/wasm/quickjs/`

Expected layout:

```text
sandbox/wasm/
  evaluator.c
  build.sh
  quickjs/
    quickjs.h
    quickjs.c
    libregexp.c
    libunicode.c
    cutils.c
    quickjs-libc.c
```

The build script looks for `wasi-sdk` at:

- `$WASI_SDK_PATH`
- `/opt/wasi-sdk` if `WASI_SDK_PATH` is not set

## Build

From the repository root:

```bash
cd sandbox/wasm
WASI_SDK_PATH=/path/to/wasi-sdk ./build.sh
```

Or, if `wasi-sdk` is already installed at `/opt/wasi-sdk`:

```bash
cd sandbox/wasm
./build.sh
```

## Output

Successful builds write the WASM module to:

```text
sandbox/wasm/dist/quickjs-evaluator.wasm
```

This module exports:

- `wasm_alloc`
- `wasm_dealloc`
- `evaluate`

It also expects the host to provide:

- `repoql_log(level, msg_ptr, msg_len)`

The .NET host is responsible for writing source and input JSON into linear memory, calling `evaluate`, then reading the returned JSON result using the packed pointer/length value.
