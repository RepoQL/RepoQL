#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/dist"
QUICKJS_DIR="$SCRIPT_DIR/quickjs"
WASI_SDK_PATH="${WASI_SDK_PATH:-/opt/wasi-sdk}"
CLANG="$WASI_SDK_PATH/bin/clang"
OUTPUT="$DIST_DIR/quickjs-evaluator.wasm"

require_file() {
    local path="$1"
    if [[ ! -f "$path" ]]; then
        echo "Missing required file: $path" >&2
        exit 1
    fi
}

if [[ ! -x "$CLANG" ]]; then
    echo "wasi-sdk clang was not found at: $CLANG" >&2
    echo "Set WASI_SDK_PATH to your wasi-sdk installation directory." >&2
    exit 1
fi

require_file "$SCRIPT_DIR/evaluator.c"
require_file "$QUICKJS_DIR/quickjs.h"
require_file "$QUICKJS_DIR/quickjs.c"
require_file "$QUICKJS_DIR/libregexp.c"
require_file "$QUICKJS_DIR/libunicode.c"
require_file "$QUICKJS_DIR/cutils.c"
require_file "$QUICKJS_DIR/quickjs-libc.c"

mkdir -p "$DIST_DIR"

"$CLANG" \
    --target=wasm32-wasip1 \
    -O2 \
    "-DCONFIG_VERSION=\"2024\"" \
    -I"$QUICKJS_DIR" \
    "$SCRIPT_DIR/evaluator.c" \
    "$QUICKJS_DIR/quickjs.c" \
    "$QUICKJS_DIR/libregexp.c" \
    "$QUICKJS_DIR/libunicode.c" \
    "$QUICKJS_DIR/cutils.c" \
    "$QUICKJS_DIR/quickjs-libc.c" \
    -o "$OUTPUT" \
    -mexec-model=reactor \
    -Wl,--export=wasm_alloc \
    -Wl,--export=wasm_dealloc \
    -Wl,--export=evaluate

echo "Built $OUTPUT"
