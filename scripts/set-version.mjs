import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const root = join(import.meta.dirname, '..')
const version = (process.argv[2] ?? '').trim().replace(/^v/, '')

if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)) {
  console.error('usage: node scripts/set-version.mjs v0.2.0')
  process.exit(1)
}

function eolOf(text) {
  return text.includes('\r\n') ? '\r\n' : '\n'
}

function setJsonVersion(path) {
  const full = join(root, path)
  if (!existsSync(full)) return
  const original = readFileSync(full, 'utf8')
  const data = JSON.parse(original)
  data.version = version
  if (data.packages && data.packages['']) data.packages[''].version = version
  const eol = eolOf(original)
  const text = JSON.stringify(data, null, 2) + '\n'
  writeFileSync(full, eol === '\n' ? text : text.replace(/\n/g, eol))
  console.log(`${path}: ${version}`)
}

function setCargoVersion(path) {
  const full = join(root, path)
  const original = readFileSync(full, 'utf8')
  const eol = eolOf(original)
  const lines = original.split(/\r?\n/)
  const start = lines.findIndex((line) => line.trim() === '[package]')
  if (start < 0) throw new Error(`${path}: no [package] section`)
  const index = lines.findIndex((line, i) => i > start && line.startsWith('version = '))
  if (index < 0) throw new Error(`${path}: no version in [package]`)
  lines[index] = `version = "${version}"`
  writeFileSync(full, lines.join(eol))
  console.log(`${path}: ${version}`)
}

setJsonVersion('src-tauri/tauri.conf.json')
setJsonVersion('ui/package.json')
setJsonVersion('ui/package-lock.json')
setCargoVersion('src-tauri/Cargo.toml')
