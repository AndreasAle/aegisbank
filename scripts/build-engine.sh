#!/usr/bin/env bash
# Builds the native engine, runs its unit tests and copies the shared library
# to artifacts/native/, where the .NET projects pick it up.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${AEGIS_ENGINE_BUILD_DIR:-$root/build/engine}"
out_dir="$root/artifacts/native"

generator=()
if command -v ninja >/dev/null 2>&1; then generator=(-G Ninja); fi

cmake -S "$root/src/AegisBank.Engine" -B "$build_dir" "${generator[@]}" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build_dir" --config Release --parallel

if [[ "${1:-}" != "--skip-tests" ]]; then
  ctest --test-dir "$build_dir" --output-on-failure -C Release
fi

mkdir -p "$out_dir"
find "$build_dir" -maxdepth 2 \( -name 'libaegis_engine.so' -o -name 'libaegis_engine.dylib' -o -name 'aegis_engine.dll' \) \
  -exec cp -f {} "$out_dir/" \;
ls -l "$out_dir"
