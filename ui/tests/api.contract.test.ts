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
 * Finds the newest built API across Debug and Release; the framework folder name changes with the TFM.
 */
function findApiExecutable(): string {
  const binRoot = join(repoRoot, 'src', 'Partner.Api', 'bin')
  const candidates = ['Debug', 'Release']
    .map((configuration) => join(binRoot, configuration))
    .filter((root) => existsSync(root))
    .flatMap((root) =>
      readdirSync(root)
        .map((name) => join(root, name, 'partner-api.exe'))
        .filter((path) => existsSync(path)),
    )

  if (candidates.length === 0) {
    throw new Error(`missing partner-api.exe under ${binRoot} — run "dotnet build" before the frontend tests`)
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
    expect(typeof settings.model.reasoningEffort).toBe('string')
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

  it('round-trips the thinking gear and stores it normalized', async () => {
    // The gear is free text (an endpoint may understand a word the menu does not offer), so what the round
    // trip has to prove is that it survives — and that its shape is normalized, so `HIGH` and `high` are one
    // setting rather than two.
    const current = await api.settings()
    const saved = await api.saveSettings({
      ...current,
      model: { ...current.model, reasoningEffort: '  HIGH ' },
    })

    expect(saved.settings.model.reasoningEffort).toBe('high')

    const cleared = await api.saveSettings({
      ...current,
      model: { ...current.model, reasoningEffort: 'off' },
    })

    expect(cleared.settings.model.reasoningEffort).toBe('off')
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

  it('reports how much of the context window the history occupies', async () => {
    const session = await api.session()
    const usage = session.context

    // The budget is the window minus the room reserved for the completion.
    expect(usage.contextTokens).toBe(128000)
    expect(usage.maxTokens).toBe(32000)
    expect(usage.budgetTokens).toBe(usage.contextTokens - usage.maxTokens)
    expect(usage.usedTokens).toBeGreaterThan(0)
    expect(usage.usedTokens).toBeLessThanOrEqual(usage.budgetTokens)
  })

  it('returns the desktop section the settings page refills from', async () => {
    // The settings page writes the form back from this response. A missing section reads as "off", so saving
    // would flip the switch back.
    const settings = await api.settings()
    expect(settings.desktop).toBeDefined()
    expect(typeof settings.desktop.enabled).toBe('boolean')
    expect(typeof settings.desktop.leaseTtlMs).toBe('number')
  })

  it('reports the web section and the engines the settings page draws one row per', async () => {
    // The page renders /api/web directly and the component tests stub it, so this round trip is the only place
    // the real payload meets the WebView type. It is also the one section that ships enabled, which is the
    // default the page has to state out loud beside the switch.
    const view = await api.web()

    expect(view.enabled).toBe(true)
    expect(typeof view.timeBudgetSeconds).toBe('number')
    expect(typeof view.maxResults).toBe('number')
    expect(view.proxy === null || typeof view.proxy === 'string').toBe(true)
    expect(typeof view.healthPath).toBe('string')
    expect(view.engines.length).toBeGreaterThan(0)

    for (const engine of view.engines) {
      expect(typeof engine.id).toBe('string')
      expect(typeof engine.name).toBe('string')
      expect(Array.isArray(engine.kinds)).toBe(true)
      expect(['ranked', 'record']).toContain(engine.nature)
      expect(typeof engine.singleSite).toBe('boolean')
      // Health is reported per engine rather than as a list of problems, so every field the row reads has to
      // be present on every engine that has never failed.
      expect(typeof engine.disabled).toBe('boolean')
      expect(typeof engine.cooling).toBe('boolean')
      expect(typeof engine.strikes).toBe('number')
      expect(engine.until === null || typeof engine.until === 'string').toBe(true)
      expect(engine.lastReason === null || typeof engine.lastReason === 'string').toBe(true)
    }

    // Turning one engine off is the only edit this endpoint's rows offer, and it is stored in the settings
    // document the page writes back — so what has to round-trip is that document, not the view.
    const current = await api.settings()
    const switchedOff = view.engines[0].id
    await api.saveSettings({ ...current, web: { ...current.web, disabledEngines: [switchedOff] } })

    const after = await api.web()
    expect(after.engines.filter((engine) => engine.disabled).map((engine) => engine.id)).toEqual([switchedOff])

    await api.saveSettings({ ...current, web: { ...current.web, disabledEngines: [] } })
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

  it('gives a recall hit the tier and retention the UI filters on', async () => {
    // The frontend's filter is `row.tier === 'Semantic' && row.scope === 'Retain'` on strings. An enum that
    // serialises as a number matches neither, and the list empties under a count that still says there are
    // hits — so this asserts the *string* form against the real server, not against a stub.
    const created = await api.createMemory({ body: '发布前先跑一次迁移脚本', tier: 'Procedural', scope: 'Retain' })
    try {
      const hits = await api.recall('迁移脚本')
      const hit = hits.find((candidate) => candidate.recordId === created)

      expect(hit).toBeDefined()
      expect(hit!.tier).toBe('Procedural')
      expect(hit!.scope).toBe('Retain')
      expect(Date.parse(hit!.createdAt)).not.toBeNaN()
    } finally {
      await api.deleteMemory(created)
    }
  })

  it('refuses a tier or retention nobody defined instead of quietly picking one', async () => {
    // The two sibling routes (/tier, /scope) reject these with the allowed names. Answering 200 here would
    // store the record under a tier nobody asked for, which is the one outcome a caller cannot detect.
    await expect(api.createMemory({ body: '记得每周五之前提交周报', tier: 'Guess' }))
      .rejects.toMatchObject({ status: 400 })
    await expect(api.createMemory({ body: '记得每周五之前提交周报', scope: 'Global' }))
      .rejects.toMatchObject({ status: 400 })

    // Absent is still the default rather than a refusal: that is what the plain form sends.
    const plain = await api.createMemory({ body: '记得每周五之前提交周报' })
    expect(plain).toMatch(/^m-manual-/)
    await api.deleteMemory(plain)
  })

})
