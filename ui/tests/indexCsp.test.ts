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
 */

// vitest runs with the frontend directory as cwd (see api.contract.test.ts).
const repoRoot = join(process.cwd(), '..')
const page = readFileSync(join(process.cwd(), 'index.html'), 'utf8')
const shell = readFileSync(join(repoRoot, 'src-tauri', 'tauri.conf.json'), 'utf8')

/** The img-src directive of a policy, whether it was written inline or as a Tauri config value. */
function imgSrc(policy: string): string {
  const directive = policy.split(';').map((part) => part.trim()).find((part) => part.startsWith('img-src'))
  expect(directive, `no img-src in: ${policy}`).toBeDefined()
  return (directive as string).slice('img-src'.length)
}

describe('the page policy', () => {
  it('allows blob: images, which is what an attachment preview is', () => {
    expect(imgSrc(page)).toContain('blob:')
  })

  it('allows blob: images in the Tauri shell too, or the header is stricter than the page', () => {
    const csp = (JSON.parse(shell).app as { security?: { csp?: string } }).security?.csp ?? ''
    expect(imgSrc(csp)).toContain('blob:')
  })

  it('keeps remote origins out of images, which is the reason the policy exists', () => {
    for (const source of [imgSrc(page), imgSrc((JSON.parse(shell).app as { security: { csp: string } }).security.csp)]) {
      for (const allowed of source.split(/\s+/).filter((token) => token !== '' && token !== "'self'")) {
        expect(['data:', 'blob:']).toContain(allowed)
      }
    }
  })
})
