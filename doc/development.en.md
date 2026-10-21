# Development and debugging

[简体中文](./development.md) | [繁體中文](./development.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

## Architecture

| Layer | Technology | Responsibility |
|---|---|---|
| Runtime | C# / .NET 8 | Session, queue, turns, memory, governance, MCP, desktop, voice |
| Frontend | Vue 3 + Vite + TS | The UI; can be debugged alone in a browser |
| Desktop shell | Tauri 2 (Rust) | Opens the window, starts the API, injects the token |

The layers only talk over HTTP with a Bearer token. Every endpoint requires it, including `/api/health`.

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
node scripts/e2e-shell.mjs                                # shell e2e; needs a desktop session, not in CI

cargo build --manifest-path src-tauri/Cargo.toml          # shell only
```

## API only

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

The first argument is the workspace, the second the port. Do not add `--urls`; it would be taken as the workspace path. Without a token, one is printed to stdout once at start-up:

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
