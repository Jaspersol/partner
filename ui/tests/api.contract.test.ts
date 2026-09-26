// @vitest-environment node
// The contract test needs sockets, not a DOM; happy-dom would additionally impose CORS on the fetch calls.
import { spawn, type ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
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

/**
 * Finds the built API. The framework folder name follows the project's target framework, and a stale one can
 * linger after a retarget — picking by existence rather than by a hardcoded name keeps the test pointed at
 * what was actually built.
 */
function findApiExecutable(): string {
  const debugRoot = join(repoRoot, 'src', 'Partner.Api', 'bin', 'Debug')
  const candidates = existsSync(debugRoot)
    ? readdirSync(debugRoot)
        .map((name) => join(debugRoot, name, 'partner-api.exe'))
        .filter((path) => existsSync(path))
    : []

  if (candidates.length === 0) {
    throw new Error(`missing partner-api.exe under ${debugRoot} — run "dotnet build" before the frontend tests`)
  }

  return candidates.sort((left, right) => statSync(right).mtimeMs - statSync(left).mtimeMs)[0]!
}

const apiExe = findApiExecutable()

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
    expect(typeof settings.input.utteranceGapMs).toBe('number')
    expect(typeof settings.output.muteDuringTts).toBe('boolean')
    expect(typeof settings.asr.model).toBe('string')
    expect(typeof settings.tts.chunkMs).toBe('number')
    expect(typeof settings.model.apiKeySet).toBe('boolean')
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

  it('round-trips every settings section and reports what it stored', async () => {
    const current = await api.settings()
    const saved = await api.saveSettings({
      ...current,
      input: { ...current.input, utteranceGapMs: 777, enabled: true },
      asr: { ...current.asr, baseUrl: 'http://127.0.0.1:45101', model: 'contract-asr' },
      tts: { ...current.tts, baseUrl: 'http://127.0.0.1:45102', chunkMs: 120 },
    })

    expect(saved.settings.input.utteranceGapMs).toBe(777)
    expect(saved.settings.input.enabled).toBe(true)
    expect(saved.settings.asr.model).toBe('contract-asr')
    expect(saved.settings.tts.chunkMs).toBe(120)
    // Saving always reports what the consumer did as a result.
    expect(typeof saved.consumer.running).toBe('boolean')

    const reloaded = await api.settings()
    expect(reloaded.asr.model).toBe('contract-asr')
  })

  it('never echoes the API key back', async () => {
    const current = await api.settings()
    const saved = await api.saveSettings({
      ...current,
      model: { ...current.model, apiKey: 'contract-secret' },
    })

    expect(saved.settings.model.apiKeySet).toBe(true)
    expect(JSON.stringify(saved)).not.toContain('contract-secret')

    // Clearing is an explicit empty string, not an omission.
    const cleared = await api.saveSettings({
      ...current,
      model: { ...current.model, apiKey: '' },
    })
    expect(cleared.settings.model.apiKeySet).toBe(false)
  })

  it('rebuilds projections and reports the result', async () => {
    const report = await api.rebuild()
    expect(report.sessionItems).toBeGreaterThan(0)
    expect(report.sessionLastSeq).toBeGreaterThan(0)
  })

  it('reports how much of the context window the history occupies', async () => {
    const session = await api.session()
    const usage = session.context

    // The budget is the window minus the room reserved for the completion.
    expect(usage.contextTokens).toBe(32768)
    expect(usage.maxTokens).toBe(4096)
    expect(usage.budgetTokens).toBe(usage.contextTokens - usage.maxTokens)
    expect(usage.usedTokens).toBeGreaterThan(0)
    expect(usage.usedTokens).toBeLessThanOrEqual(usage.budgetTokens)
  })

  it('lists the devices the runtime can see', async () => {
    const devices = await api.devices()

    // The contract test runs on a machine with audio endpoints, but the assertion that matters is shape:
    // both directions come back as arrays of names, never null.
    expect(Array.isArray(devices.inputs)).toBe(true)
    expect(Array.isArray(devices.outputs)).toBe(true)
  })

  it('reports a model probe failure as a reason, not as an empty list', async () => {
    const closed = 9
    const probe = await api.listModels(`http://127.0.0.1:${closed}/v1`)

    expect(probe.models).toEqual([])
    expect(probe.error).toBeTruthy()
  })

  it('reports whether anything is executing the queue', async () => {
    const state = await api.consumer()

    // The contract test starts the API without model configuration, so this is the disabled state — and the
    // reason must name the missing variable rather than being an empty string.
    expect(state.running).toBe(false)
    expect(state.reason).toContain('PARTNER_LLM_BASE_URL')
    expect(state.model).toBeNull()
    expect(state.stats.handled).toBe(0)
  })

  it('reports an unknown queue item as nothing to cancel', async () => {
    expect(await api.cancel('does-not-exist')).toBe(false)
  })

  it('rejects an unknown memory record with a 404', async () => {
    await expect(api.editMemory('nope', 'x')).rejects.toMatchObject({ status: 404 })
  })
})
