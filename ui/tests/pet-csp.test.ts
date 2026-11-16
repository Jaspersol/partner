// @vitest-environment node
// Read as text, not parsed by a browser: happy-dom enforces no policy, and the point is what the file says.
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

import { LIVE2D_BANNER } from '../src/pet/licence'

/**
 * The pet window's policy, the shell's, and the licence banner. Page meta tag and Tauri header are both enforced and
 * the stricter wins, so the asset protocol has to be in both.
 */

const repoRoot = join(process.cwd(), '..')
const pet = readFileSync(join(process.cwd(), 'pet.html'), 'utf8')
const shellConfig = JSON.parse(readFileSync(join(repoRoot, 'src-tauri', 'tauri.conf.json'), 'utf8')) as {
  app: { security: { csp: string } }
}
const shell = shellConfig.app.security.csp

function policy(html: string): string {
  const match = /http-equiv="Content-Security-Policy"\s+content="([^"]*)"/.exec(html)
  expect(match, 'no meta CSP in the page').not.toBeNull()
  return (match as RegExpExecArray)[1] as string
}

function directive(text: string, name: string): string {
  const part = text
    .split(';')
    .map((segment) => segment.trim())
    .find((segment) => segment.startsWith(`${name} `) || segment === name)
  expect(part, `no ${name} in: ${text}`).toBeDefined()
  return (part as string).slice(name.length).trim()
}

/** Every http(s) origin a policy mentions, deduplicated; trailing `;` from the directive list is not part of it. */
function origins(text: string): string[] {
  return [
    ...new Set(
      text
        .split(/\s+/)
        .map((token) => token.replace(/[;,]+$/, ''))
        .filter((token) => /^https?:\/\//.test(token))
        .map((token) => token.replace(/\*$/, '')),
    ),
  ]
}

describe('the pet window policy', () => {
  it('allows the asset protocol for scripts, connect and images', () => {
    const petPolicy = policy(pet)
    expect(directive(petPolicy, 'script-src')).toContain('http://asset.localhost')
    expect(directive(petPolicy, 'connect-src')).toContain('http://asset.localhost')
    expect(directive(petPolicy, 'img-src')).toContain('http://asset.localhost')
  })

  it('allows WebAssembly without opening up eval', () => {
    expect(policy(pet)).toContain("'wasm-unsafe-eval'")
    expect(policy(pet)).not.toContain("'unsafe-eval'")
  })

  it('names no origin beyond the asset protocol, the local API and Tauri\'s IPC endpoint', () => {
    for (const origin of origins(policy(pet))) {
      expect(['http://asset.localhost', 'http://ipc.localhost', 'http://127.0.0.1:']).toContain(origin)
    }
  })

  it('allows Tauri\'s IPC endpoint', () => {
    expect(directive(policy(pet), 'connect-src')).toContain('http://ipc.localhost')
  })
})

describe('the shell policy', () => {
  it('allows the asset protocol so the pet window can load the model', () => {
    expect(directive(shell, 'script-src')).toContain('http://asset.localhost')
    expect(directive(shell, 'img-src')).toContain('http://asset.localhost')
    expect(directive(shell, 'connect-src')).toContain('http://asset.localhost')
  })

  it('allows Tauri\'s `ipc:` scheme', () => {
    expect(directive(shell, 'connect-src')).toContain('ipc:')
  })

  it('keeps eval out of the shell', () => {
    expect(shell).not.toContain("'unsafe-eval'")
  })
})

describe('the Live2D banner', () => {
  it('is a distinct comment that survives minification', () => {
    expect(LIVE2D_BANNER.startsWith('/*!')).toBe(true)
    expect(LIVE2D_BANNER.endsWith('*/')).toBe(true)
    // The licence name and its URL are the two things the Open Software License requires to be kept.
    expect(LIVE2D_BANNER).toContain('Copyright(c) Live2D Inc. All rights reserved.')
    expect(LIVE2D_BANNER).toContain('https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html')
  })
})