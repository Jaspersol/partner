import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const root = join(import.meta.dirname, '..')
const target = join(root, 'src/Partner.Api/Voice/EnginePackage.cs')
const manifestPath = process.argv.slice(2).find((arg) => !arg.startsWith('--'))
const check = process.argv.includes('--check')

function fail(message) {
  console.error(message)
  process.exit(1)
}

if (!manifestPath) fail('usage: node scripts/apply-engines-manifest.mjs <engines-manifest.json> [--check]')

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8').replace(/^\uFEFF/, ''))
if (manifest.schema !== 1) fail(`${manifestPath}: unsupported schema ${manifest.schema}`)
if (!/^[A-Za-z0-9._-]+$/.test(manifest.tag ?? '')) fail(`${manifestPath}: bad tag ${JSON.stringify(manifest.tag)}`)
if (!Array.isArray(manifest.assets) || manifest.assets.length === 0) fail(`${manifestPath}: no assets`)

const repository = process.env.GITHUB_REPOSITORY || 'Jaspersol/partner'
const url = (name) => `https://github.com/${repository}/releases/download/${manifest.tag}/${name}`

for (const asset of manifest.assets) {
  if (!asset.name?.endsWith('.zip')) fail(`asset ${asset.name}: not a zip`)
  if (!/^[0-9a-f]{64}$/.test(asset.sha256 ?? '')) fail(`asset ${asset.name}: bad sha256`)
}

const pick = (prefix) => manifest.assets.filter((asset) => asset.name.startsWith(prefix)).sort((a, b) => a.name.localeCompare(b.name))
const tts = pick('partner-engine-tts-')
const kws = pick('partner-engine-kws')

if (tts.length === 0) fail(`${manifestPath}: no partner-engine-tts-* asset`)
if (kws.length === 0) fail(`${manifestPath}: no partner-engine-kws* asset`)

function block(field, assets, eol) {
  return [
    `    private static readonly (string Url, string Sha256)[] ${field} =`,
    '    [',
    ...assets.map((asset) => `        ("${url(asset.name)}", "${asset.sha256}"),`),
    '    ];',
  ].join(eol)
}

function replace(text, field, replacement) {
  const pattern = new RegExp(`    private static readonly \\(string Url, string Sha256\\)\\[\\] ${field} =\\r?\\n    \\[[\\s\\S]*?\\r?\\n    \\];`)
  if (!pattern.test(text)) fail(`${target}: cannot find the ${field} array`)
  return text.replace(pattern, replacement)
}

const before = readFileSync(target, 'utf8')
const eol = before.includes('\r\n') ? '\r\n' : '\n'
const after = replace(replace(before, 'TtsArchives', block('TtsArchives', tts, eol)), 'KwsArchives', block('KwsArchives', kws, eol))

console.log(`tts: ${tts.map((asset) => asset.name.replace('partner-engine-', '')).join(', ')}`)
console.log(`kws: ${kws.map((asset) => asset.name.replace('partner-engine-', '')).join(', ')}`)

if (before === after) {
  console.log('EnginePackage.cs already matches the manifest')
  process.exit(0)
}

if (check) fail('EnginePackage.cs does not match the manifest')

writeFileSync(target, after)
console.log(`updated ${target}`)
