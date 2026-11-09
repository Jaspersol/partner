# Release process

**English** | [简体中文](./release.md) | [繁體中文](./release.zh-TW.md)

Back to [documentation index](./README.en.md).

## In one line

There is one kind of tag: push `v*` to publish the installer. The voice engines are not published by this flow; the installer always downloads the already published `engines-v1` release.

## What gets published

| Asset | Produced by | Lives in |
|---|---|---|
| `Partner_<version>_x64-setup.exe`, `<installer>.exe.sha256` | the `app` job of `.github/workflows/release.yml` | the app release (tag `vX.Y.Z`) |
| `SHA256SUMS.txt`, `THIRD-PARTY-NOTICES.md` | same | same |

The installer does **not** contain the engines: after installing, click download under Settings → Local engines. The URLs and SHA-256 come from the constants committed in `src/Partner.Api/Voice/EnginePackage.cs`, which point at the `engines-v1` release. The installer only creates `engines/tts/` and `engines/kws-asr/` and puts the two server scripts inside them; the map form of `bundle.resources` in `tauri.conf.json` treats the **value as the full target path** (not a directory), so it has to read `engines/tts/tts-server.py`. Writing `engines/tts/` drops the script as a file named `tts`, taking the engine folder's place.

## In-app updates

Settings → General shows the version from `ui/package.json` (the workflow writes it from the tag). The check has two paths; both sets of constants live in `src/Partner.Api/Updates/UpdateService.cs`:

- **API primary**: the release JSON at `api.github.com/repos/Jaspersol/partner/releases/latest` supplies the version, the installer's name, size and download URLs, and its `<installer>.exe.sha256` checksum.
- **Direct-link fallback**: no REST API at all. `releases/latest` answers with a 302 to `releases/tag/<tag>` (where the version comes from), `SHA256SUMS.txt` names the installer and carries its SHA-256, and the asset comes from `releases/latest/download/<file name>` or `releases/download/<tag>/<file name>`. `api.github.com` allows only 60 calls an hour per address: when a shared egress address has spent the quota and the API answers 403, 429, or 403 with `Retry-After`, the network is down, or a release has no `.exe.sha256`, the check switches to this path automatically and reports `source: direct`. A failed API is not retried on every check during a short cooldown.

Both paths must obtain a digest: with neither `.exe.sha256` nor `SHA256SUMS.txt` the update is not offered. The download tries `browser_download_url` → the API asset URL → the tag-pinned direct link → the `releases/latest/download/` direct link in order, and any one of them answering is enough. Candidate URLs and redirect targets are restricted to GitHub hosts, the streaming download has a size cap, and the file is checked for its `MZ` header and SHA-256 before the shell is handed a path.

Every app release therefore has to keep three assets: `Partner_<version>_x64-setup.exe`, `<installer>.exe.sha256`, and the `SHA256SUMS.txt` that lists it. Older releases with only the latter two still update (through the fallback); when neither path can supply a checksum the app refuses the update.

The download is checked against that SHA-256 before the installer is started and the app exits. The installer is run with `/UPDATE /P`: it does **not** uninstall the previous version and rewrites `api/`, the shell and the two bundled engine scripts in place, which skips the maintenance page's default "uninstall before installing". The runtimes and models under `engines/` are not part of the installer's file list, so an update leaves them alone and nothing has to be downloaded again. A fork changes `UpdateService.Repository` to its own repository.

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
  -o src/Partner.Api/bin/Release/net10.0/win-x64/publish
npm --prefix ui exec tauri build
```

The installer ends up in `src-tauri/target/release/bundle/nsis/`.

## Known limitations

- Unit tests, frontend unit/contract tests and `Partner.Smoke --verify-baseline` run before publishing; any failure stops the job. The old `continue-on-error` exemption is gone.
- `bundle.targets` in `tauri.conf.json` is `nsis` only, so the release ships an exe installer and no msi.
- The biggest engine pack is 1.16 GB, still under GitHub's 2 GiB per-asset limit; anything larger has to be split further.
