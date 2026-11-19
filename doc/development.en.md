# Development and debugging

[简体中文](./development.md) | [繁體中文](./development.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

## Architecture

| Layer | Technology | Responsibility |
|---|---|---|
| Runtime | C# / .NET 10 | Session, queue, turns, memory, governance, MCP, desktop, voice |
| Frontend | Vue 3 + Vite + TS | The UI; can be debugged alone in a browser |
| Desktop shell | Tauri 2 (Rust) | Opens the window, starts the API, injects the token |

The layers only talk over HTTP with a Bearer token. Every endpoint requires it, including `/api/health`.

Settings reads use an in-process snapshot. Manual edits to workspace `settings/runtime.json` are not picked up automatically; restart the API to load them. Invalid JSON at startup falls back to safe defaults and is rewritten on the next successful save. `GET /api/settings` returns a `revision` that whole-document `PUT /api/settings` must include: a missing revision returns 400 and a stale one returns 409. Clients should retain unsaved edits and reload to resolve the conflict. Failed commits do not advance the revision; revisions are only valid for the current process.

Tool `decision` values in `GET /api/consumer` are `deny`, `allow`, `ask`, or `conditional`. `conditional` means the actual decision depends on call arguments, session provenance, or a lease; it is not an approval.

## Full check

```bash
bash scripts/ci.sh               # doc refs → CSS → build → unit tests → smoke + capability gate → frontend → shell
SKIP_SHELL=1 bash scripts/ci.sh  # skip the shell build
```

Windows only.

## Per-layer commands

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ReflexEngine"   # one test class
dotnet run --project src/Partner.Smoke                    # smoke only; run from the repo root

npm --prefix ui run typecheck
npm --prefix ui run test
npm --prefix ui run build

node scripts/check-docs.mjs                               # do doc references still resolve
node scripts/check-css-names.mjs                          # scoped class name check
node scripts/check-engine-pins.mjs                        # do the engine URLs and hashes still match the published assets (needs gh)
node scripts/check-live2d-bundle.mjs                      # does the built bundle carry the Live2D notice (needs a build)
node scripts/e2e-shell.mjs                                # shell e2e; needs a desktop session, not in CI

cargo build --manifest-path src-tauri/Cargo.toml          # shell only
```

## Frontend dependency audit and the Live2D patch

On 2026-11-17 `npm audit` ran against the updated lockfile: 5 moderate findings, all in development dependency chains, and `npm audit --omit=dev` reported 0. The previous record was 11 (8 moderate, 3 critical); audit results follow the advisory database and do not prove the installer has a production RCE. Frontend development and CI use Node 22.12 or later 22.x, or another Node version Vitest 5 supports (`^24.0.0 || >=26.0.0`).

| Dependency chain | Action and residual risk |
|---|---|
| `vitest` / `@vitest/mocker` / `tinypool` | Upgraded to Vitest and mocker 5.1.0; the new Vitest no longer depends on Tinypool, and the lockfile no longer reports the earlier [mock path traversal](https://github.com/advisories/GHSA-82fw-gwwq-j7x9), [mock injection](https://github.com/advisories/GHSA-qxg6-89mg-rqv8), Tinypool [prototype pollution](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3) and [option injection](https://github.com/advisories/GHSA-85c8-ppgw-ccpr). We still only run trusted repository code and never expose the test server to untrusted networks. |
| `happy-dom` | Upgraded to 21.0.4; the lockfile no longer reports the earlier [VM escape](https://github.com/advisories/GHSA-37j7-fg3j-429f), [cross-origin credentials](https://github.com/advisories/GHSA-w4gp-fjgq-3q4g), [script injection](https://github.com/advisories/GHSA-6q6h-j7hj-3r64), [SSRF](https://github.com/advisories/GHSA-c8p4-j97h-3vg8) and [cookie scope forgery](https://github.com/advisories/GHSA-5fpq-26gf-qwcr). The test environment is still not a safe sandbox for untrusted HTML or scripts. |
| `vue-tsc` → `@vue/language-core` → `minimatch` | Upgraded to vue-tsc / language-core 3.3.14; that chain no longer reports the [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249) in the lockfile. |
| `@vue/test-utils` 2.6.0 → `js-beautify` 2.0.3 → `editorconfig` 3.0.2 / `glob` 13.0.7 → `minimatch` 10.2.6 | The remaining 5 moderate findings are this chain and the packages it touches: [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249), [glob traversal cancellation](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4). Current upstream versions still depend on the affected ranges; we do not force overrides of transitive dependencies across majors, which would break tool contracts. Run only in trusted working trees and avoid untrusted glob input; keep tracking compatible fixes in test-utils, js-beautify, editorconfig and glob, and re-run the audit and tests after updating the lockfile. |

Behavioural compatibility after the upgrade still has to be verified with a clean `npm ci`, the type check, the frontend tests and a build; do not use `npm audit fix --force` to force a cross-major upgrade.

`easy-live2d` is pinned at 1.1.0 and patched by `patch-package --error-on-fail`, which applies `ui/patches/easy-live2d+1.1.0.patch`; do not replace it with a runtime monkeypatch. A pre-physics hook has been suggested upstream: [Panzer-Jack/easy-live2d#55](https://github.com/Panzer-Jack/easy-live2d/issues/55); upstream has not merged an equivalent implementation. The suggested callback runs after motion / expression / effects and before pose / physics, exposes per-frame parameter reads and writes plus motion state, and supports registration before load and passing null to unregister.

When upgrading that library, check the patch for ESM, CJS and both sets of type declarations; a clean `npm ci` must apply the patch, then run the type check, the frontend tests, a build and verification with a real physics-chain model to confirm programmatic layering is still after effects and before physics and actually drives physics. Only remove the patch and the now-unneeded `patch-package` once a released version offers an equivalent interface and the callers have migrated to it.

## Session projection benchmark

`tools/SessionProjectionBenchmark` generates a deterministic UTF-8 session log and measures open, first projection, projection after append and the process peak working set separately. Each measurement appends one record, so copy a fresh directory from the same original dataset and run it in its own process; record 100k and 1M separately, repeat several times and note the machine, runtime, disk and cache conditions. Do not run it in a real user workspace.

```bash
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-100k 100000
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-1m 1000000
cp -R /tmp/partner-bench-100k /tmp/partner-bench-run
dotnet run -c Release --project tools/SessionProjectionBenchmark -- measure /tmp/partner-bench-run
```

When comparing before and after a change, copy the same benchmark tool into the baseline tree, run it in both trees, and give each run a fresh copy of the original dataset. Open time includes the log repair check and the index rebuild; the peak is the working set of the whole measurement process, not the allocations of a single phase.

Measurements this round (times in ms; peak in bytes):

| Version | Initial events | Open | First projection | Projection after append | Peak working set |
|---|---:|---:|---:|---:|---:|
| Before | 100000 | 210.95 | 243.22 | 33.71 | 104202240 |
| After | 100000 | 278.86 | 264.83 | 0.76 | 85458944 |
| Before | 1000000 | 1240.16 | 1182.26 | 250.34 | 532164608 |
| After | 1000000 | 1173.75 | 1304.74 | 0.67 | 413970432 |

The measuring environment and the initial event counts of the generated data were the same, but these are single samples and still need repeating to assess variance. The results show the projection after append and the peak working set going down, the first-projection time going up, and open time moving in different directions at the two sizes. An ordinary append builds the immutable chunk view incrementally; edits, retractions or potential forward references still replay in full, and physical deletion rebuilds the cache.

## API only

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

The first argument is the workspace, the second the port. The API resolves the workspace to an absolute path; on Windows the launch argument can use `/`, but file tool targets still require `\`. Do not add `--urls`; it would be taken as the workspace path. Without a token, one is printed to stdout once at start-up:

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

The token can also come from the `PARTNER_API_TOKEN` environment variable, never the command line.

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
```

## Frontend only

```bash
npm --prefix ui run dev          # http://localhost:5173
```

Set the token and port in the browser console (the frontend does not accept them as URL parameters):

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

## Capability gate

Capability levels are derived from smoke checks that actually ran; the baseline is `capabilities/manifest.baseline.json`. A capability that disappears or drops a level fails the gate. After adding a smoke check:

```bash
dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json
```

The two desktop checks that move focus only run with `PARTNER_DESKTOP_LIVE=1`; otherwise they are reported as not verified, not as failures.

## Common pitfalls

- Use npm for the frontend, not pnpm.
- After changing the API, republish before packaging; `scripts/build-shell.sh` does this in order.
- Close the app before running smoke: an extra WebView2 window slows desktop observation a lot and timing-sensitive checks fail together. With the app running, `cargo build` also fails because the file is locked.
- Desktop tests must run serially; they all read the same real desktop.
- Do not run two `dotnet test` or smoke runs at once; they compete for temporary ports.
- `npm run preview` runs on 4173 and is already in the CORS allowlist; new local origins go in `ApiHost.cs`.
