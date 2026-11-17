#!/usr/bin/env bash
# Exercise both entrypoints with recorded commands and fresh artifact markers.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/project/scripts" "$tmp/project/src-tauri" "$tmp/project/ui/node_modules" "$tmp/bin"
cp "$root/scripts/build-shell.sh" "$tmp/project/scripts/"
cp "$root/src-tauri/tauri.conf.json" "$tmp/project/src-tauri/"
export TEST_ROOT="$tmp/project" TEST_LOG="$tmp/log"
cat > "$tmp/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
set -eu
echo publish >> "$TEST_LOG"
mkdir -p "$TEST_ROOT/src/Partner.Api/bin/Release/net10.0/win-x64/publish"
echo fresh > "$TEST_ROOT/src/Partner.Api/bin/Release/net10.0/win-x64/publish/marker"
EOF
cat > "$tmp/bin/npm" <<'EOF'
#!/usr/bin/env bash
set -eu
if [[ "$*" == *"run build"* ]]; then
  echo typecheck >> "$TEST_LOG"
  echo vite >> "$TEST_LOG"
  mkdir -p "$TEST_ROOT/ui/dist"
  echo fresh > "$TEST_ROOT/ui/dist/marker"
elif [[ "$*" == *"exec tauri build"* ]]; then
  echo tauri >> "$TEST_LOG"
  hook=$(node -p 'JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).build.beforeBuildCommand' "$TEST_ROOT/src-tauri/tauri.conf.json")
  (cd "$TEST_ROOT/src-tauri" && bash -c "$hook")
  cargo build
else
  echo "unexpected npm call: $*" >&2; exit 1
fi
EOF
cat > "$tmp/bin/cargo" <<'EOF'
#!/usr/bin/env bash
set -eu
test "$(cat "$TEST_ROOT/src/Partner.Api/bin/Release/net10.0/win-x64/publish/marker")" = fresh
test "$(cat "$TEST_ROOT/ui/dist/marker")" = fresh
echo cargo >> "$TEST_LOG"
EOF
chmod +x "$tmp/bin/"*
export PATH="$tmp/bin:$PATH" DOTNET="$tmp/bin/dotnet" NPM="$tmp/bin/npm"
for mode in installer compile; do
  rm -rf "$TEST_ROOT/ui/dist" "$TEST_ROOT/src"
  : > "$TEST_LOG"
  if [ "$mode" = installer ]; then
    env -u SKIP_INSTALLER bash "$TEST_ROOT/scripts/build-shell.sh"
    expected=$'publish\ntauri\ntypecheck\nvite\ncargo'
  else
    SKIP_INSTALLER=1 bash "$TEST_ROOT/scripts/build-shell.sh"
    expected=$'publish\ntypecheck\nvite\ncargo'
  fi
  actual=$(cat "$TEST_LOG")
  if [ "$actual" != "$expected" ]; then
    printf '%s: unexpected calls:\n%s\n' "$mode" "$actual" >&2
    exit 1
  fi
done
