#!/bin/sh
# Rebuild _chromium_stubs.so for linux-x64.  Requires `cc` (gcc or clang).
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUT_DIR="${1:-$SCRIPT_DIR/../../Vellum/runtimes/linux-x64/native}"

mkdir -p "$OUT_DIR"
cc -shared -fPIC -O2 -o "$OUT_DIR/_chromium_stubs.so" "$SCRIPT_DIR/stubs.c"

echo "Wrote $OUT_DIR/_chromium_stubs.so"
