#!/usr/bin/env bash
# Produces the desktop bundle in one step.
#
# The ordering is the whole point: the shell bundles a *published* snapshot of the API, so building the shell
# without republishing first silently ships the previous API. That failure mode is invisible until a new
# endpoint 404s inside the app, so it is encoded here rather than left to a README note.
#
# Usage:  bash scripts/build-shell.sh                    (development build: includes the desktop pet)
#         SKIP_INSTALLER=1 bash scripts/build-shell.sh (compile the shell only, skip the NSIS package)
#         RELEASE=1 bash scripts/build-shell.sh        (distributable build: no pet, release config)
#
# A default build deliberately keeps the desktop pet for local development. Until Live2D approves the Expandable
# Application publication licence (see doc/pet.md and doc/release.md), an installer built here must NOT be
# distributed: it bundles the Cubism Framework. RELEASE=1 sets VITE_PARTNER_PET=0 and PARTNER_PET=0 and merges
# tauri.release.conf.json, matching .github/workflows/release.yml. The CI workflow is the supported way to produce
# a shippable installer.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -z "${DOTNET:-}" ]; then
  DOTNET=$(command -v dotnet || command -v dotnet.exe) || { echo "dotnet not found" >&2; exit 127; }
fi

if [ -z "${NPM:-}" ]; then
  NPM=$(command -v npm || command -v npm.cmd) || { echo "npm not found" >&2; exit 127; }
fi

PUBLISH_DIR="src/Partner.Api/bin/Release/net10.0/win-x64/publish"

# Distributable builds take the pet out (see the header). Development builds deliberately keep it.
if [ -n "${RELEASE:-}" ]; then
  export VITE_PARTNER_PET=0
  export PARTNER_PET=0
fi

echo "== publish the API sidecar =="
"$DOTNET" publish src/Partner.Api -c Release -r win-x64 --self-contained true -o "$PUBLISH_DIR" -v q --nologo

echo "== frontend =="
if [ ! -d ui/node_modules ]; then
  "$NPM" --prefix ui install
fi
"$NPM" --prefix ui run build

# The local engines (interpreter, inference packages, models: ~3GB) are NOT part of the installer. Only the two
# small server scripts under engines/ are bundled (see tauri.conf.json); the rest is downloaded from the
# settings page into <install dir>/engines, so this build does not depend on what is installed locally.

if [ -n "${SKIP_INSTALLER:-}" ]; then
  echo "== shell (compile only) =="
  cargo build --manifest-path src-tauri/Cargo.toml --release
else
  echo "== shell + installer =="
  if [ -n "${RELEASE:-}" ]; then
    "$NPM" --prefix ui exec tauri build -- --config src-tauri/tauri.release.conf.json
  else
    "$NPM" --prefix ui exec tauri build
  fi
fi

echo "SHELL OK"
