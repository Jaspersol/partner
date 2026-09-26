#!/usr/bin/env bash
# Phase 0 gate: build -> unit tests -> smoke -> capability regression check -> frontend -> shell.
# Any capability that disappears or drops a level fails the build (design doc Phase 0).
#
# Usage:  bash scripts/ci.sh
#         DOTNET=dotnet.exe bash scripts/ci.sh      (override the launcher)
#         SKIP_SHELL=1 bash scripts/ci.sh           (skip the Rust shell build; it is the slowest step)
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -z "${DOTNET:-}" ]; then
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET=dotnet
  elif command -v dotnet.exe >/dev/null 2>&1; then
    # Under WSL the Windows SDK is reachable through interop, but only with the .exe suffix.
    DOTNET=dotnet.exe
  else
    echo "dotnet not found on PATH (tried 'dotnet' and 'dotnet.exe')" >&2
    exit 127
  fi
fi

if [ -z "${NPM:-}" ]; then
  if command -v npm >/dev/null 2>&1; then
    NPM=npm
  elif command -v npm.cmd >/dev/null 2>&1; then
    NPM=npm.cmd
  else
    echo "npm not found on PATH" >&2
    exit 127
  fi
fi

echo "== build =="
"$DOTNET" build -v q --nologo

echo "== unit tests =="
"$DOTNET" test --nologo -v q

echo "== smoke + capability gate =="
"$DOTNET" run --project src/Partner.Smoke -v q -- --verify-baseline capabilities/manifest.baseline.json

# The frontend's contract test starts the API binary, so it runs after the .NET build above.
echo "== frontend (typecheck, unit + contract tests, build) =="
if [ ! -d ui/node_modules ]; then
  "$NPM" --prefix ui install
fi
"$NPM" --prefix ui run typecheck
"$NPM" --prefix ui run test
"$NPM" --prefix ui run build

if [ -z "${SKIP_SHELL:-}" ]; then
  echo "== shell (cargo build) =="
  if command -v cargo >/dev/null 2>&1; then
    # Same ordering as scripts/build-shell.sh: the shell bundles the published API, so publish first.
    "$DOTNET" publish src/Partner.Api -c Release -r win-x64 --self-contained true \
      -o src/Partner.Api/bin/Release/net8.0/win-x64/publish -v q --nologo
    cargo build --manifest-path src-tauri/Cargo.toml
  else
    echo "cargo not found on PATH — skipping the shell build" >&2
  fi
fi

echo "CI OK"
