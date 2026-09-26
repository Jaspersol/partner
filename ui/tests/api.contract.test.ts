// @vitest-environment node
// The contract test needs sockets, not a DOM; happy-dom would additionally impose CORS on the fetch calls.
import { spawn, type ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { ApiError, PartnerApi } from '../src/api'

/**
 * The frontend's types against the real server binary. This is the test that catches contract drift: the API
 * returns anonymous JSON, so nothing but a live round trip proves the two sides still agree.
 */

// vitest runs with the frontend directory as cwd.
const repoRoot = join(process.cwd(), '..')
const apiExe = join(repoRoot, 'src', 'Partner.Api', 'bin', 'Debug', 'net8.0', 'partner-api.exe')

let api: PartnerApi
let server: ChildProcess
let workspace: string
let port: number

async function pickFreePort(): Promise<number> {
  const listening = Promise.withResolvers<number>()
  const probe = createServer()

  probe.once('error', listening.reject)
  probe.listen(0, '127.0.0.1', () => {
    const address = probe.address()
    if (address === null || typeof address === 'string') {
      listening.reject(new Error('could not determine a free port'))
      return
    }
    probe.close(() => listening.resolve(address.port))
  })

  return listening.promise
}

beforeAll(async () => {
  if (!existsSync(apiExe)) {
    throw new Error(`missing ${apiExe} — run "dotnet build" before the frontend tests`)
  }

  port = await pickFreePort()
  workspace = mkdtempSync(join(tmpdir(), 'partner-ui-contract-'))

  // Seed the append-only log directly: there is no "create event" endpoint by design (events come from the
  // agent), and the on-disk JSONL shape is itself part of the contract this test protects.
  mkdirSync(join(workspace, 'session'), { recursive: true })
  const events = [
    { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'user', source: 'text', body: 'turn on the lamp' },
    { seq: 2, at: '2026-09-26T10:00:01+00:00', kind: 'assistant', source: 'text', body: 'done' },
  ]
  writeFileSync(join(workspace, 'session', 'events.jsonl'), events.map((e) => JSON.stringify(e)).join('\n') + '\n')

  server = spawn(apiExe, [workspace, String(port)], {
    env: { ...globalThis.process.env, PARTNER_API_TOKEN: 'contract-test-token' },
    stdio: ['ignore', 'pipe', 'pipe'],
  })

  // The API prints readiness only once it is accepting connections, so await that line rather than a delay.
  const ready = Promise.withResolvers<void>()
  server.stdout?.on('data', (chunk: Buffer) => {
    if (chunk.toString().includes('listening on')) {
      ready.resolve()
    }
  })
  server.once('exit', (code) => ready.reject(new Error(`partner-api exited early with code ${code}`)))

  await ready.promise
  api = new PartnerApi({ port, token: 'contract-test-token' })
})

afterAll(async () => {
  if (server && server.exitCode === null) {
    // Removing the workspace before the process is gone fails on Windows: SQLite still holds its file.
    const exited = Promise.withResolvers<void>()
    server.once('exit', () => exited.resolve())
    server.kill()
    await exited.promise
  }

  if (workspace) {
    rmSync(workspace, { recursive: true, force: true })
  }
})

describe('authentication', () => {
  it('rejects a request with no token', async () => {
    await expect(new PartnerApi({ port, token: '' }).session()).rejects.toMatchObject({ status: 401 })
  })

  it('rejects a request with the wrong token', async () => {
    await expect(new PartnerApi({ port, token: 'wrong' }).session()).rejects.toBeInstanceOf(ApiError)
  })

  it('accepts the token the server was started with', async () => {
    await expect(api.session()).resolves.toBeDefined()
  })
})

describe('the API contract the UI binds to', () => {
  it('answers session, queue, memory and settings with the shapes the types describe', async () => {
    const session = await api.session()
    expect(Array.isArray(session.items)).toBe(true)
    expect(typeof session.lastSeq).toBe('number')

    const queue = await api.queue()
    expect(typeof queue.depth).toBe('number')
    expect(Array.isArray(queue.items)).toBe(true)

    const memory = await api.memory()
    expect(Array.isArray(memory)).toBe(true)

    const settings = await api.settings()
    expect(typeof settings.utteranceGapMs).toBe('number')
    expect(typeof settings.muteDuringTts).toBe('boolean')
  })

  it('keeps the original body when a session event is edited', async () => {
    const before = await api.session()
    const target = before.items[0]
    expect(target).toBeDefined()
    if (!target) {
      return
    }

    await api.editSession(target.seq, `${target.body} (edited)`)

    const after = await api.session()
    const edited = after.items.find((item) => item.seq === target.seq)
    expect(edited?.body).toBe(`${target.body} (edited)`)
    expect(edited?.originalBody).toBe(target.body)
    expect(edited?.revisionCount).toBeGreaterThan(0)

    const revisions = await api.sessionRevisions(target.seq)
    expect(revisions.at(-1)?.body).toBe(`${target.body} (edited)`)
  })

  it('round-trips settings and reports what it stored', async () => {
    const saved = await api.saveSettings({ utteranceGapMs: 777, inputEnabled: true })
    expect(saved.utteranceGapMs).toBe(777)
    expect(saved.inputEnabled).toBe(true)

    const reloaded = await api.settings()
    expect(reloaded.utteranceGapMs).toBe(777)
  })

  it('rebuilds projections and reports the result', async () => {
    const report = await api.rebuild()
    expect(report.sessionItems).toBeGreaterThan(0)
    expect(report.sessionLastSeq).toBeGreaterThan(0)
  })

  it('reports an unknown queue item as nothing to cancel', async () => {
    expect(await api.cancel('does-not-exist')).toBe(false)
  })

  it('rejects an unknown memory record with a 404', async () => {
    await expect(api.editMemory('nope', 'x')).rejects.toMatchObject({ status: 404 })
  })
})
