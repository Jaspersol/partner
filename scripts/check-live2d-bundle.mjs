import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'

/**
 * The Live2D notice check for a built bundle.
 *
 * The licence obligation is on the shipped JavaScript, not on the source: a chunk that bundles the Cubism Framework
 * has to carry Live2D's copyright notice; a pet-disabled build must not contain the Framework at all.
 * `pet-csp` pins the banner in the source; this is the same check against what `vite build` actually emitted,
 * which is what ends up in the installer.
 */

const BANNER = 'Copyright(c) Live2D Inc. All rights reserved.'
const dist = join(import.meta.dirname, '..', 'ui', 'dist')

if (!existsSync(dist)) {
  console.error('ui/dist does not exist; build the frontend first')
  process.exit(1)
}

/** PARTNER_PET=0 leaves the pet out of both the frontend and shell. */
const petEnabled = process.env.PARTNER_PET !== '0'
if (process.env.VITE_PARTNER_PET !== undefined && (process.env.VITE_PARTNER_PET !== '0') !== petEnabled) {
  throw new Error('VITE_PARTNER_PET conflicts with PARTNER_PET')
}

const assets = join(dist, 'assets')
const scripts = existsSync(assets) ? readdirSync(assets).filter((name) => name.endsWith('.js')) : []
if (scripts.length === 0) {
  console.error('ui/dist/assets holds no JavaScript; the build did not run')
  process.exit(1)
}

/**
 * True when the chunk actually includes easy-live2d's modules. The source map's `sources` list holds the module
 * file names, while its prose and comments also mention the package by name — matching the bare string would flag
 * a chunk that merely talks about it.
 */
const carriesLive2d = (name) => {
  const map = join(assets, `${name}.map`)
  if (!existsSync(map)) return false
  const parsed = JSON.parse(readFileSync(map, 'utf8'))
  const sources = Array.isArray(parsed.sources) ? parsed.sources : []
  return sources.some((source) => typeof source === 'string' && source.includes('node_modules/easy-live2d/'))
}

const withFramework = scripts.filter(carriesLive2d)
const unmarked = withFramework.filter((name) => !readFileSync(join(assets, name), 'utf8').includes(BANNER))

if (petEnabled) {
  if (!existsSync(join(dist, 'pet.html'))) {
    console.error('the pet build emitted no pet.html')
    process.exit(1)
  }
  if (withFramework.length === 0) {
    console.error('the pet build emitted no Live2D chunk; the pet page would load nothing')
    process.exit(1)
  }
  if (unmarked.length > 0) {
    console.error('a Live2D chunk is missing the copyright notice:', unmarked.join(', '))
    process.exit(1)
  }
} else if (withFramework.length > 0 || existsSync(join(dist, 'pet.html'))) {
  // The pet-disabled build must contain neither the Framework nor its page.
  console.error('a pet-disabled build still contains the pet:', withFramework.join(', ') || 'pet.html')
  process.exit(1)
}

console.log(`Live2D notice OK (${String(withFramework.length)} chunk(s), pet ${petEnabled ? 'on' : 'off'})`)