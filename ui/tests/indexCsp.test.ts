// @vitest-environment node
// This reads files, not a DOM: the whole point is that the policy is checked as text.
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * The page's Content-Security-Policy, checked as text because happy-dom enforces none.
 *
 * Attachment previews use object URLs, so `blob:` must be in img-src in both the page meta tag and the Tauri
 * shell config: both are enforced and the stricter wins.
 *
 * The shell is also the host of the pet window, which loads its model and textures over the asset protocol, so its
 * img-src has to admit `http://asset.localhost`. The main page does not: it has no reason to draw anything from
 * there, and keeping that origin out is what stops the main window from rendering imported assets.
 */

// vitest runs with the frontend directory as cwd (see api.contract.test.ts).
const repoRoot = join(process.cwd(), '..')
const page = readFileSync(join(process.cwd(), 'index.html'), 'utf8')
const shell = readFileSync(join(repoRoot, 'src-tauri', 'tauri.conf.json'), 'utf8')

const ASSET_ORIGIN = 'http://asset.localhost'

/** The img-src directive of a policy, whether it was written inline or as a Tauri config value. */
function imgSrc(policy: string): string {
  const directive = policy.split(';').map((part) => part.trim()).find((part) => part.startsWith('img-src'))
  expect(directive, `no img-src in: ${policy}`).toBeDefined()
  return (directive as string).slice('img-src'.length)
}

function shellPolicy(): string {
  return (JSON.parse(shell).app as { security?: { csp?: string } }).security?.csp ?? ''
}

describe('the page policy', () => {
  it('allows blob: images, which is what an attachment preview is', () => {
    expect(imgSrc(page)).toContain('blob:')
  })

  it('allows blob: images in the Tauri shell too, or the header is stricter than the page', () => {
    expect(imgSrc(shellPolicy())).toContain('blob:')
  })

  it('allows the shell\'s IPC endpoint in both policies', () => {
    for (const policy of [page, shellPolicy()]) {
      const connect = policy.split(';').map((part) => part.trim()).find((part) => part.startsWith('connect-src'))
      expect(connect, `no connect-src in: ${policy}`).toBeDefined()
      expect(connect).toContain('http://ipc.localhost')
    }
  })

  it('keeps remote origins out of images, which is the reason the policy exists', () => {
    for (const allowed of imgSrc(page).split(/\s+/).filter((token) => token !== '' && token !== "'self'")) {
      expect(['data:', 'blob:']).toContain(allowed)
    }
  })

  it('lets the shell reach the asset protocol for the pet window, and nothing else remote', () => {
    for (const allowed of imgSrc(shellPolicy()).split(/\s+/).filter((token) => token !== '' && token !== "'self'")) {
      expect(['data:', 'blob:', ASSET_ORIGIN]).toContain(allowed)
    }
    expect(imgSrc(shellPolicy())).toContain(ASSET_ORIGIN)
  })
})