# Release process

**English** | [简体中文](./release.md) | [繁體中文](./release.zh-TW.md)

Back to [documentation index](./README.en.md).

## In one line

There is one kind of tag: push `v*` to publish the installer. The voice engines are not published by this flow; the installer always downloads the already published `engines-v1` release.

## What gets published

| Asset | Produced by | Lives in |
|---|---|---|
| `Partner_<version>_x64-setup.exe` | the `app` job of `.github/workflows/release.yml` | the app release (tag `vX.Y.Z`) |
| `SHA256SUMS.txt`, `THIRD-PARTY-NOTICES.md` | same | same |

The installer does **not** contain the engines: after installing, click download under Settings → Local engines. The URLs and SHA-256 come from the constants committed in `src/Partner.Api/Voice/EnginePackage.cs`, which point at the `engines-v1` release.

## Triggers

| How | What happens |
|---|---|
| push tag `v0.2.0` | builds the installer and publishes the release |
| Actions → release → Run workflow, `tag` empty | build only; the output goes to the Actions artifact, no release |
| same, with `tag` and `publish` ticked | publishes the installer under that tag |

The version comes from the tag: the workflow runs `scripts/set-version.mjs` to write `0.2.0` into `src-tauri/tauri.conf.json`, `Cargo.toml` and `ui/package.json`, so the tag and the installer cannot drift apart.

## Engine packs

The workflow neither packs nor publishes engines, so no self-hosted runner is needed. `engines-v1` is read-only: published installers pin the SHA-256 of the files under it, and changing any byte makes the download fail its final check.

Nothing regenerates those constants any more, so every build (and `node scripts/check-engine-pins.mjs` locally) compares them with the SHA-256 digests GitHub stores for the release assets — no 3.4GB download. A mistyped constant or a swapped asset fails the build instead of shipping an installer that cannot install engines on any machine.

To really change the engines, pack them by hand under a new tag on a machine that has `engines/`, then paste the URL table printed by `scripts/pack-engines.ps1` into `EnginePackage.cs`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v2
```

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
node scripts/check-engine-pins.mjs   # needs an authenticated gh
npm --prefix ui ci
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
