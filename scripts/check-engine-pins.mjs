/**
 * Engine pin check: the archives the installer downloads must be the archives that were published.
 *
 *   node scripts/check-engine-pins.mjs                 # asks `gh` about the release the pins name
 *   node scripts/check-engine-pins.mjs --json a.json   # same, from a saved `gh release view`
 *
 * The installer carries no engines. On first use it downloads four multi-hundred-megabyte archives from a
 * fixed release URL and refuses any archive whose SHA-256 does not match the constant compiled into it. Those
 * constants are the whole contract: nothing regenerates them any more, so a single mistyped nibble ships an
 * installer that cannot install engines on any machine until it is fixed and re-released.
 *
 * `gh release view <tag> --json assets` reports each asset's `digest` as `sha256:...`, computed by GitHub
 * from the stored bytes, so this compares against what a download would actually hash — not against another
 * committed file that could be wrong in the same way as the constants.
 *
 * Exits non-zero on: a pin whose asset is missing from the release, a digest mismatch, a draft release (its
 * assets 404 for anonymous downloads), or pins that disagree about which release they live in.
 */

import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const ROOT = join(import.meta.dirname, '..')
const SOURCE = join(ROOT, 'src', 'Partner.Api', 'Voice', 'EnginePackage.cs')

/** `("https://github.com/<owner>/<repo>/releases/download/<tag>/<asset>", "<sha256>")`. */
const PIN =
  /\(\s*"(https:\/\/github\.com\/([^/"]+\/[^/"]+)\/releases\/download\/([^/"]+)\/([^"]+))"\s*,\s*"([0-9a-fA-F]{64})"\s*\)/g

function fail(message) {
  console.error(`engine pins: ${message}`)
  process.exit(1)
}

function ghRelease(repo, tag) {
  try {
    return execFileSync('gh', ['release', 'view', tag, '--repo', repo, '--json', 'tagName,isDraft,assets'], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
      // Windows installs gh as gh.exe on PATH, which execFileSync finds without a shell; `shell` only matters
      // for a .cmd shim, and every argument here is a constant or a tag taken out of EnginePackage.cs.
      shell: process.platform === 'win32',
    })
  } catch (error) {
    const detail = (String(error.stderr ?? '') || error.message).trim()
    fail(
      `cannot read the ${tag} release of ${repo} through gh: ${detail}\n` +
        `  install the GitHub CLI and log in, or pass --json <file> with a saved "gh release view ${tag} --repo ${repo} --json assets"`,
    )
  }
}

const args = process.argv.slice(2)
const jsonFlag = args.indexOf('--json')
if (jsonFlag >= 0 && !args[jsonFlag + 1]) fail('--json needs a file: node scripts/check-engine-pins.mjs --json <file>')
const releaseJson = jsonFlag >= 0 ? readFileSync(args[jsonFlag + 1], 'utf8') : null

const pins = [...readFileSync(SOURCE, 'utf8').matchAll(PIN)].map(([, url, repo, tag, name, sha256]) => ({
  url,
  repo,
  tag,
  name,
  sha256: sha256.toLowerCase(),
}))
if (pins.length === 0) fail(`no pinned archive in ${SOURCE}`)

// One release for all four archives: a half-migrated pin set would download some packs from a release whose
// other packs were never uploaded.
const targets = [...new Set(pins.map((pin) => `${pin.repo}@${pin.tag}`))]
if (targets.length > 1) fail(`the pins do not all live in one release: ${targets.join(', ')}`)
const { repo, tag } = pins[0]

const release = JSON.parse(releaseJson ?? ghRelease(repo, tag))
const assets = new Map((release.assets ?? []).map((asset) => [asset.name, asset]))
if (assets.size === 0) fail(`the ${tag} release has no assets`)

// A draft release is invisible to anyone who is not logged in to the repository, so every download 404s.
if (release.isDraft) fail(`the ${tag} release is a draft: its assets 404 for anonymous downloads`)

let digests = 0
for (const pin of pins) {
  const asset = assets.get(pin.name)
  if (!asset) fail(`${tag} has no asset ${pin.name} — the installer would 404`)
  if (asset.state && asset.state !== 'uploaded') fail(`${tag}/${pin.name} is ${asset.state}, not uploaded`)

  const digest = (asset.digest ?? '').replace(/^sha256:/i, '').toLowerCase()
  if (!digest) {
    console.warn(`engine pins: ${pin.name} has no digest on the release; only its presence was checked`)
    continue
  }
  if (digest !== pin.sha256) fail(`${pin.name}: EnginePackage.cs pins ${pin.sha256}, ${tag} holds ${digest}`)
  digests += 1
}

const extra = [...assets.keys()].filter((name) => !pins.some((pin) => pin.name === name))
console.log(
  `engine pins OK (${pins.length} archives on ${repo}@${tag}, ${digests} verified by digest` +
    `${extra.length > 0 ? `, ${extra.length} other asset(s) ignored` : ''})`,
)
