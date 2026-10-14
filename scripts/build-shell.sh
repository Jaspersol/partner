#!/usr/bin/env bash
# Produces the desktop bundle in one step.
#
# The ordering is the whole point: the shell bundles a *published* snapshot of the API, so building the shell
# without republishing first silently ships the previous API. That failure mode is invisible until a new
# endpoint 404s inside the app, so it is encoded here rather than left to a README note.
#
# Usage:  bash scripts/build-shell.sh
#         SKIP_INSTALLER=1 bash scripts/build-shell.sh   (compile the shell only, skip the NSIS package)
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -z "${DOTNET:-}" ]; then
  DOTNET=$(command -v dotnet || command -v dotnet.exe) || { echo "dotnet not found" >&2; exit 127; }
fi

if [ -z "${NPM:-}" ]; then
  NPM=$(command -v npm || command -v npm.cmd) || { echo "npm not found" >&2; exit 127; }
fi

PUBLISH_DIR="src/Partner.Api/bin/Release/net8.0/win-x64/publish"

echo "== publish the API sidecar =="
"$DOTNET" publish src/Partner.Api -c Release -r win-x64 --self-contained true -o "$PUBLISH_DIR" -v q --nologo

echo "== frontend =="
if [ ! -d ui/node_modules ]; then
  "$NPM" --prefix ui install
fi
"$NPM" --prefix ui run build

# engines/ is optional: without it the bundle is voice-HTTP-only; with it the local engines ride
# along (~1.3GB). tauri.conf.json always names ../engines as a resource, but a MISSING directory
# makes tauri fail — so when the installer has not been run, hand it an empty folder instead.
if [ ! -d engines ]; then
  mkdir -p engines
  echo "(engines/ absent — bundling an empty folder; run scripts/install-engines.ps1 for real engines)"
fi

if [ -n "${SKIP_INSTALLER:-}" ]; then
  echo "== shell (compile only) =="
  cargo build --manifest-path src-tauri/Cargo.toml --release
else
  echo "== shell + installer =="
  "$NPM" --prefix ui exec tauri build
fi

echo "SHELL OK"
