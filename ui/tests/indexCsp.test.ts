// @vitest-environment node
// This reads files, not a DOM: the whole point is that the policy is checked as text.
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * The page's Content-Security-Policy, checked against what the UI actually renders.
 *
 * <para>
 * A CSP is invisible to every other test in this folder: mounting the composer in happy-dom hands back an
 * `<img src="blob:...">` that looks perfectly fine, and the browser refuses it afterwards for reasons that
 * never reach the console of the component. That is how an attachment could upload, show its name and its
 * byte count, and display nothing at all where the picture was — with a green test suite.
 * </para>
 *
 * <para>
 * So the policy is read here as text. The bytes of an attachment cannot be linked to (an `<img>` carries no
 * Authorization header, and a token in a query string leaks into logs and history), so the preview is an
 * object URL and `blob:` has to be in img-src — in the page's own meta tag <em>and</em> in the Tauri shell's,
 * since a header and a meta tag are enforced together and the stricter of the two wins.
 * </para>
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
    // 'self' plus data: and blob: — a URL the server serves, a data URL and an object URL. Nothing else.
    for (const source of [imgSrc(page), imgSrc((JSON.parse(shell).app as { security: { csp: string } }).security.csp)]) {
      for (const allowed of source.split(/\s+/).filter((token) => token !== '' && token !== "'self'")) {
        expect(['data:', 'blob:']).toContain(allowed)
      }
    }
  })
})
