# Development and debugging

[简体中文](./development.md) | [繁體中文](./development.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

## Architecture

| Layer | Technology | Responsibility |
|---|---|---|
| Runtime | C# / .NET 10 | Sessions, queues, turns, memory, governance, MCP, desktop and voice |
| Frontend | Vue 3 + Vite + TS | The UI, which can also be debugged by itself in a browser |
| Desktop shell | Tauri 2 (Rust) | Opens the window, starts the API and injects the token |

The layers communicate only through HTTP with a Bearer token. Every endpoint, including `/api/health`, requires that token.

Settings are read from an in-process snapshot. Editing `settings/runtime.json` in the workspace by hand does not apply automatically; restart the API to load it. Invalid JSON at startup falls back to safe defaults and is rewritten after the next successful save. `GET /api/settings` returns a `revision`, and a full-document `PUT /api/settings` must include it: a missing value returns 400, while an old value returns 409. Clients should preserve unsaved edits, reload the settings and resolve the conflict. Failed commits do not advance the revision, which is valid only inside the current process.

The tool `decision` in `GET /api/consumer` is one of `deny`, `allow`, `ask` or `conditional`. `conditional` means the result still depends on call arguments, session provenance or a lease; it is not an approval.

## Full check

```bash
bash scripts/ci.sh               # doc references → CSS → build → unit tests → smoke + capability gate → frontend → shell
SKIP_SHELL=1 bash scripts/ci.sh  # skip the shell build
```

Run this on Windows only.

## Per-layer commands

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ReflexEngine"   # one test class
dotnet run --project src/Partner.Smoke                    # smoke only; run from the repository root

npm --prefix ui run typecheck
npm --prefix ui run test
npm --prefix ui run build

node scripts/check-docs.mjs                               # verify that documentation references still resolve
node scripts/check-css-names.mjs                          # check scoped class names
node scripts/check-engine-pins.mjs                        # compare engine URLs and hashes with published assets; gh login required
node scripts/check-live2d-bundle.mjs                      # check the built bundle for the Live2D notice; build first
node scripts/e2e-shell.mjs                                # shell e2e; needs a desktop session and is not run in CI

cargo build --manifest-path src-tauri/Cargo.toml          # compile the shell only
```

## Frontend dependency audit and Live2D patch

On 2026-11-17, `npm audit` against the updated lockfile found 5 moderate findings, all in development dependency chains; `npm audit --omit=dev` found 0. The previous record was 11 (8 moderate and 3 critical). Audit counts follow the advisory database and do not establish that the installer contains a production RCE. Frontend development and CI use Node 22.12 or a later 22.x release, or another Node version supported by Vitest 5 (`^24.0.0 || >=26.0.0`).

| Dependency chain | Treatment and remaining risk |
|---|---|
| `vitest` / `@vitest/mocker` / `tinypool` | Upgraded to Vitest and mocker 5.1.0. The new Vitest no longer depends on Tinypool, and the lockfile no longer reports the earlier [mock path traversal](https://github.com/advisories/GHSA-82fw-gwwq-j7x9), [mock injection](https://github.com/advisories/GHSA-qxg6-89mg-rqv8), Tinypool [prototype pollution](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3) or [option injection](https://github.com/advisories/GHSA-85c8-ppgw-ccpr). Continue to run only trusted repository code and never expose the test server to untrusted networks. |
| `happy-dom` | Upgraded to 21.0.4. The lockfile no longer reports the former [VM escape](https://github.com/advisories/GHSA-37j7-fg3j-429f), [cross-origin credentials](https://github.com/advisories/GHSA-w4gp-fjgq-3q4g), [script injection](https://github.com/advisories/GHSA-6q6h-j7hj-3r64), [SSRF](https://github.com/advisories/GHSA-c8p4-j97h-3vg8) or [cookie-scope forgery](https://github.com/advisories/GHSA-5fpq-26gf-qwcr). The test environment is still not a safe sandbox for untrusted HTML or scripts. |
| `vue-tsc` → `@vue/language-core` → `minimatch` | Upgraded to vue-tsc / language-core 3.3.14. This chain no longer reports the [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249) in the lockfile. |
| `@vue/test-utils` 2.6.0 → `js-beautify` 2.0.3 → `editorconfig` 3.0.2 / `glob` 13.0.7 → `minimatch` 10.2.6 | The remaining 5 moderate findings are in this chain and its affected packages: [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249) and [glob traversal cancellation](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4). Current upstream releases still depend on the affected ranges, so transitive dependencies are not forcibly overridden across major versions, which could break tool contracts. Run only in trusted worktrees and avoid untrusted glob input; track compatible fixes in test-utils, js-beautify, editorconfig and glob, then rerun the audit and tests after updating the lockfile. |

After the upgrade, behavioral compatibility still requires a clean `npm ci`, type checking, frontend tests and a build. Do not use `npm audit fix --force` to force a cross-major upgrade.

`easy-live2d` is pinned to 1.1.0 and patched by `patch-package --error-on-fail`, applying `ui/patches/easy-live2d+1.1.0.patch`; do not replace this with a runtime monkeypatch. A pre-physics hook has been proposed upstream at [Panzer-Jack/easy-live2d#55](https://github.com/Panzer-Jack/easy-live2d/issues/55), but no equivalent has been merged. The proposed callback runs after motion / expression / effects and before pose / physics, exposes per-frame parameter reads and writes plus motion state, and supports registration before load and unregistering by passing null.

When upgrading the library, check the patch against ESM, CJS and both type-declaration sets. A clean `npm ci` must apply the patch successfully. Then run type checking, frontend tests, a build and a real physics-chain model verification to confirm that procedural overlays remain after effects and before physics and still drive physics. Remove the patch and the no-longer-needed `patch-package` only after a released version provides an equivalent interface and callers have migrated.

## Session projection benchmark

`tools/SessionProjectionBenchmark` creates a deterministic UTF-8 session log and measures opening, the first projection, projection after an append and the process peak working set as separate values. Each measurement appends one record, so copy a fresh directory from the same original dataset and run it in a separate process. Record 100k and 1M separately, repeat several times, and note the machine, runtime, disk and cache conditions. Do not run it in a real user workspace.

```bash
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-100k 100000
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-1m 1000000
cp -R /tmp/partner-bench-100k /tmp/partner-bench-run
dotnet run -c Release --project tools/SessionProjectionBenchmark -- measure /tmp/partner-bench-run
```

For before-and-after comparisons, copy the same benchmark tool into the baseline tree, run it in both trees and give each run a fresh copy of the original dataset. Startup time includes log-repair checks and index rebuilding. The peak is the working set of the whole measurement process, not the allocation of one phase.

A normal append builds the immutable chunk view incrementally. Edits, retractions and possible forward references still require a full replay, while physical deletion rebuilds the cache.

## API only

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

The first argument is the workspace and the second is the port. The API converts the workspace to an absolute path. Windows launch arguments may use `/`, but file-tool targets still require `\`. Do not add `--urls`; it is interpreted as the workspace path. Without a token, startup prints it once to stdout:

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

The token may also be supplied with `PARTNER_API_TOKEN`, never on the command line.

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
```

## Frontend only

```bash
npm --prefix ui run dev          # http://localhost:5173
```

Set the token and port from the browser console; the frontend does not accept URL parameters:

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

## Capability gate

Capability levels are derived from smoke checks that actually ran, with `capabilities/manifest.baseline.json` as the baseline. A capability that disappears or drops a level fails the gate. After adding a smoke check, update the baseline with:

```bash
dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json
```

The two desktop checks that move focus run only when `PARTNER_DESKTOP_LIVE=1` is set. Otherwise they are reported as “not verified”, not as failures.

## Common pitfalls

- Use npm for the frontend, not pnpm.
- Republish the API before packaging after any API change; `scripts/build-shell.sh` already enforces that order.
- Stop the application before smoke tests. An extra WebView2 window significantly slows desktop observation and can make timing-sensitive checks fail together. `cargo build` can also fail while the app is running because files are locked.
- Desktop tests must be serial because they all inspect the same real desktop.
- Do not run two `dotnet test` or smoke passes at once; they compete for temporary ports.
- `npm run preview` uses port 4173, which is already in the CORS allowlist. Add new local origins in `ApiHost.cs`.
