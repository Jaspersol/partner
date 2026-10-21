# Release process

**English** | [简体中文](./release.md) | [繁體中文](./release.zh-TW.md)

Back to [documentation index](./README.en.md).

## In one line

Push a `v*` tag and GitHub Actions builds the installer, then publishes it together with the engine packs.

## What gets published

| Asset | Produced by | Lives in |
|---|---|---|
| `Partner_<version>_x64-setup.exe` | the `app` job in `.github/workflows/release.yml` | the app release (tag `vX.Y.Z`) |
| `SHA256SUMS.txt`, `engines-manifest.json`, `THIRD-PARTY-NOTICES.md` | same job | same release |
| `partner-engine-tts-a-runtime.zip`, `-b-talker`, `-c-codec`, `partner-engine-kws.zip` | `scripts/pack-engines.ps1` | the engine release (tag `engines-vN`) |

The installer does **not** contain the engines. After installing, the settings page downloads them; the URLs and SHA-256 values are pinned at build time.

## Triggers

| Trigger | Result |
|---|---|
| push tag `v0.2.0` | full release: engine packs (when a runner is configured) + installer + release |
| Actions → release → Run workflow with an empty `tag` | build only; the installer lands in the workflow artifacts |
| same, with `tag` and `publish` ticked | publish that tag |

The tag decides the version: the workflow runs `scripts/set-version.mjs`, which writes `0.2.0` into `src-tauri/tauri.conf.json`, `Cargo.toml` and `ui/package.json`, so the tag and the installer version cannot drift apart.

## Why the engine packs are normally packed on your machine

The weights and venvs are not in git (`engines/` holds only the two server scripts), and a hosted runner has no 2.3 GB of models, so it **cannot** build the engine packs. Two options:

- **Default**: run once on a machine that has `engines/`

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v1
  ```

  It rebuilds a relocatable Python runtime, splits the packs, hashes them, writes `NOTICE/`, uploads to `engines-v1` and prints the URL table for `EnginePackage.cs`.

- **Automated**: register a self-hosted runner labelled `self-hosted, windows, x64` that has `gh` and `engines/tts/model`, then set the repository variable `ENGINE_RUNNER=true`. The `engines` job then packs and uploads on every tag push, and the `app` job builds afterwards. The repository variable `ENGINE_TARGET_BRANCH` (default `master`) decides which branch the engine tag is created on.

Ship a new engine set under a new tag (`engines-v2`) instead of overwriting the old one: a machine that installed the old packs would fail the SHA-256 check when retrying half way.

## How the installer pins the engine packs

The `app` job:

1. `gh release download engines-v1 --pattern engines-manifest.json`;
2. `node scripts/apply-engines-manifest.mjs dist/engines/engines-manifest.json --check` — compares the URLs and SHA-256 values in the manifest with the constants in `EnginePackage.cs` and **fails the build when they differ**.

So a repacked engine set has to be synced into the constants. To do that by hand, run the same script without `--check`: it rewrites the two arrays in `EnginePackage.cs` and keeps the existing indentation and line endings.

## Branches and protection

| Branch | Purpose |
|---|---|
| `master` | default and protected: pull requests only, no force push, no deletion, linear history |
| `dev` | day-to-day work and dry runs |

Tag `master`: `git checkout master && git pull && git tag v0.2.0 && git push github v0.2.0`.

A squash merge makes the two histories diverge: the same files count as added on both sides, so the next pull request conflicts. Realign `dev` after every merge:

```bash
git fetch github
git reset --hard github/master
```

## The same steps locally

```bash
node scripts/check-docs.mjs
node scripts/check-css-names.mjs
npm --prefix ui ci
node scripts/apply-engines-manifest.mjs dist/engines/engines-manifest.json --check
node scripts/set-version.mjs v0.2.0
dotnet build Partner.sln -c Release
dotnet publish src/Partner.Api -c Release -r win-x64 --self-contained true \
  -o src/Partner.Api/bin/Release/net8.0/win-x64/publish
npm --prefix ui exec tauri build
```

The installer ends up in `src-tauri/target/release/bundle/nsis/`.

## Known limitations

- The unit-test step is marked `continue-on-error`: the repository has two pre-existing failures unrelated to releasing (`AgentsEndpointTests.The_round_count_and_the_prompt_have_limits`, `SettingsEndpointTests.Growing_the_context_does_not_compact`). Drop the exemption once they are fixed.
- `bundle.targets` in `tauri.conf.json` is `nsis` only, so the release ships an exe installer and no msi.
- The biggest engine pack is 1.16 GB, still under GitHub's 2 GiB per-asset limit; anything larger has to be split further.
