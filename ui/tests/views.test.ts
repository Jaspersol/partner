import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import type {
  AgentsSettings,
  AgentsView,
  MemoryItem,
  MemoryRevision,
  PartnerApiSurface,
  QueueView as QueueViewData,
  RecallHit,
  SessionRevision,
  SessionView as SessionViewData,
  SettingsView,
  RuntimeSettings,
  UpdateCheck,
  UpdateDownload,
  WebView,
} from '../src/api'
import { version as packageVersion } from '../package.json'
import MemoryView from '../src/views/MemoryView.vue'
import SessionView from '../src/views/SessionView.vue'
import Composer from '../src/components/Composer.vue'
import SettingsViewComponent from '../src/views/SettingsView.vue'
import McpView from '../src/views/McpView.vue'
import SkillsView from '../src/views/SkillsView.vue'
import PromptsView from '../src/views/PromptsView.vue'
import { ApiError } from '../src/api'

/**
 * View behaviour with a stub API. The stub implements the same interface the real client does, so the
 * compiler keeps it in step; the contract test separately proves the real client matches the live server.
 */

interface Calls {
  edits: Array<{ seq: number; body: string }>
  memoryEdits: Array<{ recordId: string; body: string }>
  deletes: string[]
  cancels: string[]
  saves: RuntimeSettings[]
  recalls: string[]
  submits: { payload: string; source: string }[]
  removals: { seq: number; kind: string }[]
  withdrawalCancels: number
  tierChanges: { recordId: string; tier: string }[]
  scopeChanges: { recordId: string; scope: string }[]
  creates: { body: string; tier?: string; scope?: string }[]
  probes: string[]
  agentSaves: AgentsSettings[]
}

/**
 * The settings a stubbed API reports. Module level because a test that changes one segment (a skills deny
 * list, say) still has to build its override from the whole shape.
 */
const SETTINGS_FIXTURE: SettingsView = {
  input: {
    enabled: false,
    deviceId: null,
    silenceThreshold: 0.03,
    utteranceGapMs: 1200,
    wakeTimeoutMs: 5000,
    sentenceGapMs: 450,
    windowSeconds: 8,
    asrStream: false,
    maxSplitChars: 40,
    minSentenceChars: 10,
    wakeTone: true,
    bargeIn: false,
  },
  output: {
    enabled: false,
    deviceId: null,
    voice: null,
    speed: 1,
    gapMs: 0,
    drainMs: 400,
    sampleRate: 24000,
    muteDuringTts: true,
  },
  asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
  tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
  local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
  model: { protocol: 'openai', baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
  desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
  mcp: { enabled: false, servers: [], allowedTools: [] },
  skills: { disabled: [] },
  agents: { main: { prompt: null }, subAgents: [] },
  files: { enabled: false, roots: [] },
  web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
};

/**
 * The agents document a stubbed API reports. Two scenarios, the built-in desktop agent, and one file-read
 * agent — the state the page exists to edit. `ownerId` is what the add button reads to decide what is free.
 */
const AGENTS_FIXTURE: AgentsView = {
  main: {
    prompt: null,
    defaultPrompt: '你是主 agent。',
    effectivePrompt: '你是主 agent。\n\n可委派：桌面操控、读取文件',
    promptTokens: 18,
  },
  subAgents: [
    {
      id: 'desktop',
      name: '桌面操控',
      enabled: true,
      scenario: 'desktop',
      builtin: true,
      prompt: null,
      defaultPrompt: '你负责桌面。',
      effectivePrompt: '你负责桌面。\n\n汇报格式…',
      promptTokens: 12,
      maxRounds: 5,
      model: null,
    },
    {
      id: 'reader',
      name: '读文件的',
      enabled: true,
      scenario: 'file-read',
      builtin: false,
      prompt: '只读，不要猜。',
      defaultPrompt: '你负责读文件。',
      effectivePrompt: '只读，不要猜。\n\n汇报格式…',
      promptTokens: 14,
      maxRounds: 3,
      model: { protocol: 'openai', baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 60, contextTokens: 8192, maxTokens: 1024, supportsImages: false, reasoningEffort: 'medium' },
    },
  ],
  scenarios: [
    { key: 'desktop', name: '桌面操控', when: '需要操作界面时', ownerId: 'desktop', capabilityEnabled: true, available: true },
    { key: 'file-read', name: '读取文件', when: '需要读文件时', ownerId: 'reader', capabilityEnabled: true, available: true },
  ],
  problems: [],
  limits: { maxRounds: 10, defaultMaxRounds: 5, promptTokens: 8192 },
  files: { enabled: true, roots: ['D:/资料'] },
  desktopEnabled: true,
  webEnabled: false,
}

function createStub(overrides: Partial<PartnerApiSurface> = {}) {
  const calls: Calls = { edits: [], memoryEdits: [], deletes: [], cancels: [], saves: [], recalls: [], submits: [], removals: [], withdrawalCancels: 0, tierChanges: [], scopeChanges: [], creates: [], probes: [], agentSaves: [] }

  const session: SessionViewData = {
    lastSeq: 3,
    // One conversation, starting at the reader's own message in seq 1: delete is offered for both rows.
    conversation: { latestFromSeq: 1, withdrawn: null },
    context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 },
    items: [
      { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'first', originalBody: 'first', revisionCount: 1 },
      { seq: 3, at: '2026-09-26T10:00:02+00:00', kind: 'Assistant', source: 'Text', body: 'second', originalBody: null, revisionCount: 0 },
    ],
  }

  const queue: QueueViewData = {
    headSeq: 2,
    runningItemId: null,
    depth: 1,
    items: [
      { itemId: 'q1', seq: 1, source: 'Voice', status: 'Queued', enqueuedAt: '2026-09-26T10:00:05+00:00', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'pending work' },
      { itemId: 'q2', seq: 2, source: 'Text', status: 'Failed', enqueuedAt: '2026-09-26T10:00:00+00:00', startedAt: null, finishedAt: null, attempt: 2, error: 'disk unwritable', payload: 'backup' },
    ],
  }

  const memory: MemoryItem[] = [
    { recordId: 'm1', tier: 'Semantic', scope: 'Recall', body: 'likes jasmine tea', createdAt: '2026-09-26T10:00:00+00:00', validTo: null, supersededBy: null, editedBy: null, importance: 0.9 },
  ]

/**
 * The settings a stubbed API reports. Module level because a test that changes one segment (a skills deny
 * list, say) still has to build its override from the whole shape.
 */


  const stub: PartnerApiSurface = {
    session: vi.fn(async () => session),
    deleteLatest: vi.fn(async (seq: number) => {
      calls.removals.push({ seq, kind: 'delete' })
      return { removedFrom: 1, removedTo: 3, purgedEvents: 2, purgedRevisions: 1, purgedMemories: 0, eventSeq: 4 }
    }),
    withdrawFrom: vi.fn(async (seq: number) => {
      calls.removals.push({ seq, kind: 'withdraw' })
      return { withdrawnFrom: 1, withdrawnTo: 3, conversations: 1, events: 2 }
    }),
    cancelWithdrawal: vi.fn(async () => {
      calls.withdrawalCancels++
    }),
    editSession: vi.fn(async (seq: number, body: string) => {
      calls.edits.push({ seq, body })
    }),
    sessionRevisions: vi.fn(async (): Promise<SessionRevision[]> => [{ seq: 2, at: '2026-09-26T10:00:01+00:00', body: 'first (edited)' }]),
    queue: vi.fn(async () => queue),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], })),
    devices: vi.fn(async () => ({ inputs: ['synthetic-mic'], outputs: ['synthetic-speaker'] })),
    listModels: vi.fn(async () => ({ baseUrl: 'http://127.0.0.1:9/v1', models: ['qwen3', 'llama3'] })),    voice: vi.fn(async () => ({ input: { running: false, reason: '未启用', device: null, count: 0, rms: 0, ambientRms: 0, thresholdMisconfigured: false, awake: false }, output: { running: false, reason: '未启用', device: null, count: 0 } })),
    submitInput: vi.fn(async (payload: string, source: string) => {
      calls.submits.push({ payload, source })
      return { itemId: 'in-test', seq: 9, status: 'Queued', deduplicated: false }
    }),
    cancel: vi.fn(async (itemId: string) => {
      calls.cancels.push(itemId)
      return itemId !== 'finished'
    }),
    memory: vi.fn(async () => memory),
    memoryAll: vi.fn(async () => memory),
    skills: vi.fn(async () => ({ skills: [], warnings: [] })),
    createSkill: vi.fn(async (skill) => ({ skillId: skill.skillId, name: skill.name })),
    uploadSkill: vi.fn(async (_file, fileName) => ({ skillId: 'uploaded', name: fileName })),
    deleteSkill: vi.fn(async () => true),
    // Two engines, one of them in the breaker's cooldown: the settings page has to show a paused engine as
    // paused, because "搜不到" and "这家停了" look identical in the answer and are not the same thing.
    web: vi.fn(async (): Promise<WebView> => ({
      enabled: true,
      proxy: null,
      timeBudgetSeconds: 12,
      maxResults: 8,
      engines: [
        { id: 'duckduckgo', name: 'DuckDuckGo', nature: 'ranked', kinds: ['general'], singleSite: false, disabled: false, cooling: false, until: null, strikes: 0, lastReason: null },
        { id: 'wikipedia', name: 'Wikipedia', nature: 'ranked', kinds: ['encyclopedia'], singleSite: true, disabled: false, cooling: true, until: '2026-10-19T12:00:00+00:00', strikes: 3, lastReason: 'timeout after 8s' },
      ],
      healthPath: 'D:/workspace/web/health.json',
    })),
    setMemoryTier: vi.fn(async (recordId: string, tier: string) => {
      calls.tierChanges.push({ recordId, tier })
    }),
    setMemoryScope: vi.fn(async (recordId: string, scope: string) => {
      calls.scopeChanges.push({ recordId, scope })
    }),
    createMemory: vi.fn(async (input: { body: string; tier?: string; scope?: string }) => {
      calls.creates.push(input)
      return 'm-manual-new'
    }),
    recall: vi.fn(async (query: string): Promise<RecallHit[]> => {
      calls.recalls.push(query)
      // Tier and scope on the hit, because the server sends them: a hit is one of the rows the list shows,
      // not an anonymous match, and a stub that omits them would let the filters disagree with the server.
      return [
        {
          recordId: 'm1',
          body: 'likes jasmine tea',
          total: 0.75,
          reasons: ['lexical', 'recency'],
          tier: 'Semantic',
          scope: 'Recall',
          createdAt: '2026-09-26T10:00:00+00:00',
        },
      ]
    }),
    editMemory: vi.fn(async (recordId: string, body: string) => {
      calls.memoryEdits.push({ recordId, body })
    }),
    deleteMemory: vi.fn(async (recordId: string) => {
      calls.deletes.push(recordId)
    }),
    memoryRevisions: vi.fn(async (): Promise<MemoryRevision[]> => [{ at: '2026-09-26T10:00:09+00:00', before: 'likes tea', after: 'likes jasmine tea', actor: 'Human' }]),
    settings: vi.fn(async () => SETTINGS_FIXTURE),
    agents: vi.fn(async () => AGENTS_FIXTURE),
    saveAgents: vi.fn(async (agents: AgentsSettings) => {
      calls.agentSaves.push(agents)
      return {
        agents: {
          ...AGENTS_FIXTURE,
          main: { ...AGENTS_FIXTURE.main, prompt: agents.main.prompt },
          subAgents: agents.subAgents.map((agent) => {
            const known = AGENTS_FIXTURE.subAgents.find((item) => item.id === agent.id)
            return {
              ...(known ?? {
                name: agent.name,
                scenario: agent.scenario,
                builtin: false,
                defaultPrompt: '',
                effectivePrompt: '',
                promptTokens: 0,
              }),
              ...agent,
              model: agent.model === null ? null : { ...agent.model, apiKeySet: Boolean(agent.model.apiKey) },
            }
          }),
        },
        consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
      }
    }),
    agentJobs: vi.fn(async () => []),
    cancelAgentJob: vi.fn(async () => true),
    schedules: vi.fn(async () => []),
    createSchedule: vi.fn(),
    pauseSchedule: vi.fn(),
    resumeSchedule: vi.fn(),
    deleteSchedule: vi.fn(async () => true),
    enginePackage: vi.fn(async () => { throw new Error('no engine module in this fixture') }),
    downloadEnginePackage: vi.fn(),
    cancelEnginePackage: vi.fn(),
    setEngineRoot: vi.fn(),
    checkUpdate: vi.fn(async (current: string): Promise<UpdateCheck> => ({
      current,
      latest: current,
      available: false,
      installerName: null,
      sizeBytes: null,
      error: null,
    })),
    downloadUpdate: vi.fn(async (): Promise<UpdateDownload> => ({ phase: 'idle', bytesDone: 0, bytesTotal: null, path: null, error: null })),
    updateDownload: vi.fn(async (): Promise<UpdateDownload> => ({ phase: 'idle', bytesDone: 0, bytesTotal: null, path: null, error: null })),
    cancelUpdate: vi.fn(async (): Promise<UpdateDownload> => ({ phase: 'cancelled', bytesDone: 0, bytesTotal: null, path: null, error: null })),
    testVoice: vi.fn(async (kind: 'asr' | 'tts' | 'kws' | 'model') => {
      calls.probes.push(kind)
      return kind === 'model'
        ? { ok: true, detail: '可达，视觉支持（设置里声明 true）', latencyMs: 5 }
        : { ok: true, detail: '可达', latencyMs: 3 }
    }),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => {
      calls.saves.push(patch)
      return {
        settings: SETTINGS_FIXTURE as unknown as SettingsView,
        consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
        voice: { input: { running: false, reason: '未启用', device: null, count: 0, rms: 0, ambientRms: 0, thresholdMisconfigured: false, awake: false }, output: { running: false, reason: '未启用', device: null, count: 0 } },
        compaction: null,
      }
    }),
    uploadVoiceReference: vi.fn(async (file: Blob) => ({ path: `C:/w/voices/${(file as File).name}`, fileName: (file as File).name, bytes: 10 })),
    uploadMedia: vi.fn(async (file: Blob) => {
      const name = (file as File).name ?? 'a.png'
      return { mediaId: 'm1', contentType: 'image/png', fileName: name, path: `C:/w/media/m1__${name}`, bytes: 2048 }
    }),
    approvals: vi.fn(async () => ({ timeoutSeconds: 120, pending: [] })),
    answerApproval: vi.fn(async () => {}),
    desktop: vi.fn(async () => ({ enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [], lastObsId: null, entityCount: 0, entities: [] })),
    observeDesktop: vi.fn(async () => ({ obsId: 'obs-1', count: 0, entities: [] })),
    setApprovalGear: vi.fn(async () => ({ approval: 'all', changed: false })),
    mediaBlob: vi.fn(async () => new Blob(['png'], { type: 'image/png' })),
    ...overrides,
  }

  return { stub, calls }
}

describe('session view', () => {
  it('renders the log', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    expect(rows).toHaveLength(2)
    expect(rows[0]?.text()).toContain('first')
    expect(rows[1]?.text()).toContain('second')
  })

  it('does nothing when a message is clicked', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[aria-current="true"]').exists()).toBe(false)

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    // What a row can do to a row — 复用, 删除, 撤回 — is on the row itself, one hover away. A click on the
    // message itself neither marks it nor names it in the top bar.
    expect(wrapper.find('[aria-current="true"]').exists()).toBe(false)
  })
})

describe('day picker', () => {
  const usage = { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 }
  const reply = (day: string, days: string[], busy = false, seq = 1) => ({
    lastSeq: seq,
    conversation: { latestFromSeq: 1, withdrawn: null },
    context: usage,
    busy,
    days: days.map((entry) => ({ day: entry, count: 2 })),
    day,
    items: [{ seq, at: `${day}T10:00:00+00:00`, kind: 'User', source: 'Text', body: `on ${day}`, originalBody: null, revisionCount: 0 }],
  })

  /** Opens the cascader and reads one column's entries. */
  async function column(wrapper: ReturnType<typeof mount>, name: 'years' | 'months' | 'dates') {
    if (!wrapper.find('[data-test="session-day-panel"]').exists()) {
      await wrapper.find('[data-test="session-day"]').trigger('click')
    }

    return wrapper.findAll(`[data-test="session-day-${name}"] button`)
  }

  const dayValues = async (wrapper: ReturnType<typeof mount>) =>
    (await column(wrapper, 'dates')).map((option) => option.attributes('data-day'))

  it('offers only days that exist, and nothing when the server sends none', async () => {
    const plain = mount(SessionView, { props: { api: createStub().stub } })
    await flushPromises()
    expect(plain.find('[data-test="session-day"]').exists()).toBe(false)

    const { stub } = createStub({
      session: vi.fn(async () => reply('2027-01-03', ['2025-12-30', '2026-09-25', '2026-09-27', '2027-01-03'])),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    // The button names the day on screen.
    expect(wrapper.find('[data-test="session-day"]').text()).toContain('2027年1月3日')

    // Years, newest first — only the three that have a conversation.
    expect((await column(wrapper, 'years')).map((option) => option.text())).toEqual(['2027年', '2026年', '2025年'])
    // The columns open on the day on screen: its month, and the days of that month.
    expect((await column(wrapper, 'months')).map((option) => option.text())).toEqual(['1月'])
    expect(await dayValues(wrapper)).toEqual(['2027-01-03'])
  })

  it('narrows the month and day columns to what exists under the year and month chosen', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => reply('2026-09-27', ['2025-12-30', '2026-03-02', '2026-09-25', '2026-09-27'])),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect((await column(wrapper, 'months')).map((option) => option.text())).toEqual(['9月', '3月'])
    expect(await dayValues(wrapper)).toEqual(['2026-09-27', '2026-09-25'])

    await wrapper.find('[data-test="session-day-month-3"]').trigger('click')
    expect(await dayValues(wrapper)).toEqual(['2026-03-02'])

    // Changing the year only changes what is listed; the view has not moved.
    await wrapper.find('[data-test="session-day-year-2025"]').trigger('click')
    expect((await column(wrapper, 'months')).map((option) => option.text())).toEqual(['12月'])
    expect(await dayValues(wrapper)).toEqual(['2025-12-30'])
    expect(wrapper.find('[data-test="session-day"]').text()).toContain('2026年9月27日')
  })

  it('reads one closed day when an older day is chosen, and follows the newest again when it is chosen back', async () => {
    const session = vi.fn(async (_from?: number, _take?: number, window?: { day: string; open: boolean }) =>
      reply(window?.day === '2026-09-25' ? '2026-09-25' : '2026-09-27', ['2026-09-25', '2026-09-27']),
    )
    const wrapper = mount(SessionView, { props: { api: createStub({ session }).stub } })
    await flushPromises()
    expect(session.mock.calls.at(-1)?.[2]).toMatchObject({ day: 'latest', open: true })

    await column(wrapper, 'dates')
    await wrapper.find('[data-test="session-day-date-2026-09-25"]').trigger('click')
    await flushPromises()
    expect(session.mock.calls.at(-1)?.[2]).toMatchObject({ day: '2026-09-25', open: false })
    expect(wrapper.text()).toContain('on 2026-09-25')
    // Choosing a day closes the panel.
    expect(wrapper.find('[data-test="session-day-panel"]').exists()).toBe(false)

    await column(wrapper, 'dates')
    await wrapper.find('[data-test="session-day-date-2026-09-27"]').trigger('click')
    await flushPromises()
    expect(session.mock.calls.at(-1)?.[2]).toMatchObject({ open: true })
  })

  it('closes on Escape without choosing anything', async () => {
    const session = vi.fn(async () => reply('2026-09-27', ['2026-09-25', '2026-09-27']))
    const wrapper = mount(SessionView, { props: { api: createStub({ session }).stub } })
    await flushPromises()
    const calls = session.mock.calls.length

    await column(wrapper, 'dates')
    expect(wrapper.find('[data-test="session-day-panel"]').exists()).toBe(true)

    await wrapper.find('[data-test="session-day-panel"]').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[data-test="session-day-panel"]').exists()).toBe(false)
    expect(session.mock.calls.length).toBe(calls)
  })

  it('keeps showing the previous day across midnight while work is running, and moves on once idle', async () => {
    // The newest day on the server changes under the reader (a turn crossed midnight). While it runs the screen
    // stays on the day it was on — its window is open, so the new events still appear — and the new day is not
    // listed; on the first idle read it is.
    let midnight = false
    const session = vi.fn(async (_from?: number, _take?: number, window?: { day: string; open: boolean }) => {
      if (!midnight) return reply('2026-09-26', ['2026-09-26'])
      return reply(window?.day === 'latest' ? '2026-09-27' : '2026-09-26', ['2026-09-26', '2026-09-27'], true)
    })
    const wrapper = mount(SessionView, { props: { api: createStub({ session }).stub } })
    await flushPromises()

    midnight = true
    await wrapper.setProps({ summary: { depth: 1, pending: 1, failed: 0, runningItemId: null } })
    await (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh()
    await flushPromises()

    expect(session.mock.calls.at(-1)?.[2]).toMatchObject({ day: '2026-09-26', open: true })
    expect(await dayValues(wrapper)).toEqual(['2026-09-26'])

    session.mockImplementation(async (_from, _take, window) => reply(window?.day === 'latest' ? '2026-09-27' : '2026-09-26', ['2026-09-26', '2026-09-27']))
    await wrapper.setProps({ summary: { depth: 0, pending: 0, failed: 0, runningItemId: null } })
    // The first idle read still carries the previous answer's `busy`; the one after it is the idle one.
    const refresh = (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh
    session.mockImplementation(async (_from, _take, window) => reply(window?.day === 'latest' ? '2026-09-27' : '2026-09-26', ['2026-09-26', '2026-09-27'], false))
    await refresh()
    await refresh()
    await flushPromises()

    expect(session.mock.calls.at(-1)?.[2]).toMatchObject({ day: 'latest' })
    expect(await dayValues(wrapper)).toEqual(['2026-09-27', '2026-09-26'])
  })
})

describe('sub-agent progress', () => {
  const job = (id: string, name: string, round: number, status = 'running') => ({
    id,
    agentId: name,
    agentName: name,
    scenario: 'file-read',
    goal: `goal of ${id}`,
    status,
    round,
    maxRounds: 5,
    startedAt: `2026-09-26T10:00:0${id.slice(-1)}+00:00`,
    finishedAt: null,
    category: null,
    summary: null,
    error: null,
    rounds: [],
  })

  it('draws no cards when nothing is delegated', async () => {
    const wrapper = mount(SessionView, { props: { api: createStub().stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="subagent-cards"]').exists()).toBe(false)
  })

  it('draws a card per running job with its name and progress, oldest first, and none for finished ones', async () => {
    const agentJobs = vi.fn(async () => [job('j3', '读文件的', 1), job('j2', '桌面操控', 3), job('j1', '已完成的', 5, 'completed')])
    const wrapper = mount(SessionView, { props: { api: createStub({ agentJobs }).stub } })
    await flushPromises()

    const cards = wrapper.findAll('[data-test="subagent-card"]')
    expect(cards.map((card) => card.find('.sa-name').text())).toEqual(['桌面操控', '读文件的'])
    expect(cards.map((card) => card.find('[data-test="subagent-progress"]').text())).toEqual(['3/5', '1/5'])
    expect(cards[0]?.attributes('title')).toBe('goal of j2')
  })

  it('closes a card when its job finishes and lets the ones behind it take its place', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      let jobs = [job('j1', '甲', 1), job('j2', '乙', 2), job('j3', '丙', 3)]
      const agentJobs = vi.fn(async () => jobs)
      const wrapper = mount(SessionView, { props: { api: createStub({ agentJobs }).stub } })
      await flushPromises()
      const names = () => wrapper.findAll('[data-test="subagent-card"] .sa-name').map((name) => name.text())
      expect(names()).toEqual(['甲', '乙', '丙'])

      jobs = [job('j1', '甲', 5, 'completed'), job('j2', '乙', 3), job('j3', '丙', 3)]
      await vi.advanceTimersByTimeAsync(1500)
      await flushPromises()

      expect(names()).toEqual(['乙', '丙'])
      expect(wrapper.find('[data-test="subagent-card"] [data-test="subagent-progress"]').text()).toBe('3/5')

      jobs = [job('j2', '乙', 5, 'completed'), job('j3', '丙', 5, 'completed'), job('j1', '甲', 5, 'completed')]
      await vi.advanceTimersByTimeAsync(1500)
      await flushPromises()

      expect(wrapper.find('[data-test="subagent-cards"]').exists()).toBe(false)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  describe('while a sub-agent works for a turn that has already answered', () => {
    const usage = { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 }
    const row = (seq: number, kind: string, body: string, meta: Record<string, string> | null = null) => ({
      seq, at: '2026-09-26T10:00:00+00:00', kind, source: 'Text', body, originalBody: null, revisionCount: 0, meta,
    })
    const handoff = vi.fn(async () => ({
      lastSeq: 5,
      busy: false,
      conversation: { latestFromSeq: 1, withdrawn: null },
      context: usage,
      items: [
        row(1, 'User', '打开网易云音乐'),
        row(2, 'Thinking', '交给桌面子 agent'),
        row(3, 'ToolCall', 'agent_delegate', { id: 'a', name: 'agent_delegate', args: '{}' }),
        row(4, 'ToolResult', '已交给', { id: 'a', name: 'agent_delegate', ok: 'true' }),
        row(5, 'Assistant', '已经交给桌面操控子 agent 了'),
      ],
    }))

    it('does not fold, and keeps the stop button, until the sub-agent is done and has been answered', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        let jobs = [job('j1', '桌面操控', 2)]
        const agentJobs = vi.fn(async () => jobs)
        const wrapper = mount(SessionView, { props: { api: createStub({ session: handoff, agentJobs }).stub } })
        await flushPromises()

        // The model's run is over, but the work is not.
        expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(false)
        expect(wrapper.find('[data-tool]').exists()).toBe(true)
        expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="composer-send"]').exists()).toBe(false)

        // The sub-agent finishes. Its report is still on its way to the main agent, so there is a short wait
        // before the screen calls this finished.
        jobs = [job('j1', '桌面操控', 5, 'completed')]
        await vi.advanceTimersByTimeAsync(1500)
        await flushPromises()
        expect(wrapper.find('[data-test="subagent-cards"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(true)

        await vi.advanceTimersByTimeAsync(3000)
        await flushPromises()
        expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(true)
        expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(false)
        expect(wrapper.find('[data-test="composer-send"]').exists()).toBe(true)
        wrapper.unmount()
      } finally {
        vi.useRealTimers()
      }
    })

    it('stops the sub-agents when the stop button is pressed', async () => {
      const cancelAgentJob = vi.fn(async () => true)
      const agentJobs = vi.fn(async () => [job('j1', '甲', 1), job('j2', '乙', 2)])
      const wrapper = mount(SessionView, { props: { api: createStub({ session: handoff, agentJobs, cancelAgentJob }).stub } })
      await flushPromises()

      await wrapper.find('[data-test="composer-interrupt"]').trigger('click')
      await flushPromises()

      expect(cancelAgentJob.mock.calls.map((call) => (call as unknown as string[])[0]).sort()).toEqual(['j1', 'j2'])
    })
  })

  it('keeps the cards it has when a read fails', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      let fail = false
      const agentJobs = vi.fn(async () => {
        if (fail) throw new TypeError('network down')
        return [job('j1', '甲', 1)]
      })
      const wrapper = mount(SessionView, { props: { api: createStub({ agentJobs }).stub } })
      await flushPromises()
      expect(wrapper.findAll('[data-test="subagent-card"]')).toHaveLength(1)

      fail = true
      await vi.advanceTimersByTimeAsync(1500)
      await flushPromises()

      expect(wrapper.findAll('[data-test="subagent-card"]')).toHaveLength(1)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('context meter', () => {
  it('shows what the history costs against the prompt budget', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 3,
        conversation: { latestFromSeq: 1, withdrawn: null },
        items: [],
        context: { usedTokens: 1240, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 6, foldedItems: 2, fraction: 0.043 },
      })),
    })

    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const meter = wrapper.find('[data-test=session-context]')
    expect(meter.text()).toContain('上下文 1.2k / 28.7k')
    expect(meter.text()).toContain('4%')
    // The tooltip carries the detail the label has no room for.
    expect(meter.attributes('title')).toContain('已折叠 2 条')
  })

  it('turns loud when the window is nearly full', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 1,
        conversation: { latestFromSeq: 1, withdrawn: null },
        items: [],
        context: { usedTokens: 28000, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 40, foldedItems: 0, fraction: 0.97 },
      })),
    })

    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test=session-context]').classes()).toContain('context-tight')
  })

  it('renders nothing rather than a wrong number when the API omits it', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({ lastSeq: 1, items: [] }) as unknown as SessionViewData),
    })

    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test=session-context]').exists()).toBe(false)
  })
})

describe('a view that has not read yet', () => {
  // Every list starts with empty data, and the empty state must not double as the pending state: a reader
  // would see a confident wrong answer — 「（无匹配事件）」 on a session not yet asked about — before the request
  // that would contradict it goes out.

  it('holds a placeholder while the first read is in flight, and claims nothing when it lands empty', async () => {
    // Assigned inside the executor, which TypeScript cannot see — so the definite-assignment form rather
    // than a nullable that would be narrowed to `null` and then called.
    let release!: () => void
    const held = new Promise<void>((resolve) => {
      release = resolve
    })

    const { stub } = createStub({
      session: vi.fn(async () => {
        await held
        return { lastSeq: 0, items: [] } as unknown as SessionViewData
      }),
    })

    const wrapper = mount(SessionView, { props: { api: stub } })

    // Before the answer arrives: the skeleton, and no sentence about what is in a log nobody has read yet.
    expect(wrapper.find('.skeleton-stack').exists()).toBe(true)
    expect(wrapper.find('.empty').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('无匹配事件')

    release()
    await flushPromises()

    // After it: the placeholder goes, and an empty log still says nothing — there is no empty-state line.
    expect(wrapper.find('.skeleton-stack').exists()).toBe(false)
    expect(wrapper.find('.empty').exists()).toBe(false)
  })

  it('keeps the last good list on screen when a poll fails, and says nothing about it', async () => {
    // A failed refresh must leave the last good list where it is. Replacing a conversation with a spinner
    // would be a worse answer than the stale one — and the failure is not announced either, so the row that
    // was there simply stays.
    let call = 0
    const { stub } = createStub({
      session: vi.fn(async () => {
        call++
        if (call === 1) return { lastSeq: 1, items: [{ seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'kept', originalBody: null, revisionCount: 0 }] } as unknown as SessionViewData
        throw new Error('boom')
      }),
    })

    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()
    expect(wrapper.find('.skeleton-stack').exists()).toBe(false)

    await wrapper.vm.$.exposed?.refresh?.()
    await flushPromises()

    expect(wrapper.find('[data-test=error]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('boom')
    expect(wrapper.findAll('[data-test="session-list"] li').length).toBe(1)
  })
})

describe('session removal', () => {
  it('deletes the selected conversation, arming first, and clears the selection', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    // Permanent, and it takes the whole conversation, so the row arms first; a single click must not reach the
    // server.
    const button = wrapper.findAll('[data-test="session-list"] li')[1]?.find('[data-test="session-row-delete-inline"]')
    await button?.trigger('click')
    await flushPromises()
    expect(calls.removals).toEqual([])
    expect(button?.text()).toContain('确认删除')

    await button?.trigger('click')
    await flushPromises()

    // The rows are gone from the re-read list; that is the whole report.
    expect(calls.removals).toEqual([{ seq: 3, kind: 'delete' }])
    expect(wrapper.find('[aria-current="true"]').exists()).toBe(false)
  })

  it('withdraws on a single click, without a confirmation step', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    // Reversible until the next message, so there is nothing to confirm — and no second button appears.
    await wrapper
      .findAll('[data-test="session-list"] li')[1]
      ?.find('[data-test="session-row-withdraw-inline"]')
      .trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([{ seq: 3, kind: 'withdraw' }])
    expect(wrapper.find('[aria-current="true"]').exists()).toBe(false)
  })

  it('offers delete only on rows of the latest conversation', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 5,
        busy: false,
        // The latest conversation starts at seq 3; seq 1 belongs to an earlier one.
        conversation: { latestFromSeq: 3, withdrawn: null },
        context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 },
        items: [
          { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'older', originalBody: null, revisionCount: 0 },
          { seq: 3, at: '2026-09-26T10:00:02+00:00', kind: 'User', source: 'Text', body: 'latest', originalBody: null, revisionCount: 0 },
          { seq: 5, at: '2026-09-26T10:00:04+00:00', kind: 'Assistant', source: 'Text', body: 'reply', originalBody: null, revisionCount: 0 },
        ],
      })),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    expect(rows[0]?.find('[data-test="session-row-delete-inline"]').exists()).toBe(false)
    expect(rows[1]?.find('[data-test="session-row-delete-inline"]').exists()).toBe(true)
    expect(rows[2]?.find('[data-test="session-row-delete-inline"]').exists()).toBe(true)

    // …and the earlier conversation still offers withdrawing, which is what going back further is for.
    expect(rows[0]?.find('[data-test="session-row-withdraw-inline"]').exists()).toBe(true)
  })

  it('offers no delete at all while a withdrawal is pending', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 3,
        busy: false,
        conversation: { latestFromSeq: 1, withdrawn: { fromSeq: 1, toSeq: 3, conversations: 1, events: 2 } },
        context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 },
        items: [
          { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'first', originalBody: null, revisionCount: 0 },
        ],
      })),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="session-row-delete-inline"]').exists()).toBe(false)
    await wrapper.findAll('[data-test="session-list"] li')[0]?.trigger('click')
    await flushPromises()

    // Selecting the row must not offer it either — there is no second place delete is offered from.
    expect(wrapper.find('[data-test="session-row-delete-inline"]').exists()).toBe(false)
  })

  it('deletes from a row without selecting it first', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    await rows[1]?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await rows[1]?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([{ seq: 3, kind: 'delete' }])
  })

  it('withdraws from a row on one press of the row button', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    await rows[1]?.find('[data-test="session-row-withdraw-inline"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([{ seq: 3, kind: 'withdraw' }])
  })

  it('reports a removal through the list itself, with nothing said about it anywhere else', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    // The row's own two-press delete: arm, then confirm. There is no panel behind the list to do it from.
    const row = wrapper.findAll('[data-test="session-list"] li')[1]
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await flushPromises()

    // The rows are gone on the re-read, which is the report. No line under the toolbar repeats it.
    expect(wrapper.find('[data-test="session-notice"]').exists()).toBe(false)
    expect(wrapper.find('[data-test=error]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('已删除')
  })

  it('puts the deleted conversation\'s first message back in the box', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    // Deleting takes the whole latest conversation, so the text worth keeping is its first message — even
    // when the reader pressed delete on a later reply. The assistant's reply must not be resendable.
    const row = wrapper.findAll('[data-test="session-list"] li')[1]
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await flushPromises()

    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('first')
  })

  it('keeps the 复用 button putting the reader\'s own message back in the box', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    expect(rows[0]?.find('[data-test="session-row-restore"]').text()).toContain('复用')
    await rows[0]?.find('[data-test="session-row-restore"]').trigger('click')
    await flushPromises()

    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('first')
    // The assistant's reply carries no 复用 at all: putting it back in the box would send it as the reader's
    // own words on the next turn.
    expect(rows[1]?.find('[data-test="session-row-restore"]').exists()).toBe(false)
  })

  it('offers the way back from a withdrawal under the newest message, and never anywhere else', async () => {
    let pending: { fromSeq: number; toSeq: number; conversations: number; events: number } | null = {
      fromSeq: 5,
      toSeq: 9,
      conversations: 2,
      events: 4,
    }
    const { stub, calls } = createStub({
      session: vi.fn(async () => ({
        lastSeq: pending === null ? 4 : 9,
        busy: false,
        conversation: { latestFromSeq: 1, withdrawn: pending },
        context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 },
        items: [
          { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'kept', originalBody: null, revisionCount: 0 },
        ],
      })),
      cancelWithdrawal: vi.fn(async () => {
        calls.withdrawalCancels++
        pending = null
      }),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const undo = wrapper.find('[data-test="session-withdrawn-cancel"]')
    expect(undo.exists()).toBe(true)
    expect(undo.text()).toBe('取消撤回')

    // It is drawn under the newest message: inside the scroller, after the list, not in a banner elsewhere.
    expect(undo.element.closest('[data-test="session-list"]')).toBeNull()
    const scroller = wrapper.find('.scroll')
    expect(scroller.element.contains(undo.element)).toBe(true)
    expect(wrapper.find('[data-test="session-withdrawn-banner"]').exists()).toBe(false)

    await undo.trigger('click')
    await flushPromises()

    expect(calls.withdrawalCancels).toBe(1)
    // Gone with the withdrawal it was offering to take back — the re-read is what makes it go away.
    expect(wrapper.find('[data-test="session-withdrawn-cancel"]').exists()).toBe(false)
  })

  it('offers no way back when nothing is pending', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="session-withdrawn-cancel"]').exists()).toBe(false)
    expect(calls.withdrawalCancels).toBe(0)
  })

  it('lets the list stand when the server refuses a delete, and says nothing about the refusal', async () => {
    const { stub } = createStub({
      deleteLatest: vi.fn(async () => {
        throw new Error('409: 正在回复：等它结束（或先停止）再删除或撤回')
      }),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const row = wrapper.findAll('[data-test="session-list"] li')[1]
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await flushPromises()

    // The rows are still here, which is what a refusal looks like when nothing narrates it.
    expect(wrapper.findAll('[data-test="session-list"] li')).toHaveLength(2)
    expect(wrapper.find('[data-test="error"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('409')
  })

  it('lets an armed delete be backed out of', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const row = wrapper.findAll('[data-test="session-list"] li')[1]
    await row?.find('[data-test="session-row-delete-inline"]').trigger('click')
    await row?.find('[data-test="session-row-cancel"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([])
    // The button is still there and no longer armed, which is what "backed out of" means here.
    expect(row?.find('[data-test="session-row-delete-inline"]').text()).toBe('删除')
    expect(row?.find('[data-test="session-row-cancel"]').exists()).toBe(false)
  })

  it('no longer mentions 删除之后 anywhere', async () => {
    // 撤回 is the label and the meaning: 删除之后 would name an action that does not exist.
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.text()).not.toContain('删除之后')
    expect(wrapper.find('[data-test="session-row-retract"]').exists()).toBe(false)
    expect(wrapper.find('[data-test="session-retract"]').exists()).toBe(false)
  })
})

describe('approval placement', () => {
  it('puts the prompt in the conversation, right above the composer', async () => {
    // It was in the app shell first, which put it above every view — including the queue — and a prompt nobody
    // looks at is a prompt nobody answers.
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const pending = {
      approvalId: 'ap-1',
      toolName: 'desktop_act',
      arguments: '{"entityId":"1a2b","kind":"LeftClick"}',
      requestedAt: '2026-09-27T11:52:01+00:00',
      expiresAt: '2026-09-27T11:54:01+00:00',
    }
    const { stub } = createStub({
      session: vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, conversation: { latestFromSeq: null, withdrawn: null }, context: usage })),
    })

    const wrapper = mount(SessionView, {
      props: {
        api: stub,
        approvals: [pending],
        desktop: {
          enabled: true,
          leaseTtlMs: 15000,
          approval: 'all',
          allowedWindowTitles: [],
          lastObsId: 'obs-1',
          entityCount: 1,
          entities: [{ entityId: '1a2b', title: '网易云音乐', process: 'cloudmusic', foreground: false }],
        },
      },
    })
    await flushPromises()

    const banner = wrapper.find('[data-test="approval-banner"]')
    const composer = wrapper.find('[data-test="composer"]')
    expect(banner.exists()).toBe(true)
    expect(composer.exists()).toBe(true)

    // Above the composer, in the same view: a parked turn is about the message being written.
    const relation = banner.element.compareDocumentPosition(composer.element)
    expect(relation & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

    await wrapper.find('[data-test="approval-allow"]').trigger('click')
    expect(wrapper.emitted('answer')?.[0]).toEqual(['ap-1', true])
    wrapper.unmount()
  })
})

describe('session scroll position', () => {
  /** Lets the frame a pin scheduled run, so its guard is released before the test plays the reader. */
  const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))

  /** happy-dom has no layout, so the geometry a browser would compute is stated explicitly. */
  function geometry(wrapper: ReturnType<typeof mount>, scrollHeight: number, scrollTop: number, clientHeight = 200) {
    const el = wrapper.find('.scroll').element as HTMLElement
    Object.defineProperty(el, 'scrollHeight', { value: scrollHeight, configurable: true })
    Object.defineProperty(el, 'clientHeight', { value: clientHeight, configurable: true })
    el.scrollTop = scrollTop
    return el
  }

  it('opens on the newest event', async () => {
    // happy-dom has no layout, so the geometry a browser would compute is stated on the prototype before the
    // mount — the initial scroll happens during mount, before a test can reach the element.
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', { configurable: true, get: () => 1200 })
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 200 })

    try {
      const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
      const session = vi.fn(async () => ({ lastSeq: 9, items: [], busy: false, conversation: { latestFromSeq: null, withdrawn: null }, context: usage }))
      const { stub } = createStub({ session })
      const wrapper = mount(SessionView, { props: { api: stub } })
      await flushPromises()

      // The view does exactly one scroll on its own, and it is this one.
      expect(wrapper.find('.scroll').element.scrollTop).toBe(1200)
    } finally {
      // These were not own properties before the stub, so removing the shadow restores what was inherited.
      delete (HTMLElement.prototype as { scrollHeight?: unknown }).scrollHeight
      delete (HTMLElement.prototype as { clientHeight?: unknown }).clientHeight
    }
  })

  it('follows the reply when the reader is already at the bottom', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, conversation: { latestFromSeq: null, withdrawn: null }, context: usage }))
    const { stub } = createStub({ session })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const el = geometry(wrapper, 1000, 800)
    await wrapper.find('.scroll').trigger('scroll')

    // The reply grows, then a refresh lands: the bottom moves and the view moves with it.
    Object.defineProperty(el, 'scrollHeight', { value: 1400, configurable: true })
    await (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh()

    expect(el.scrollTop).toBe(1400)
  })

  it('does not mistake its own pin for the reader scrolling away', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, conversation: { latestFromSeq: null, withdrawn: null }, context: usage }))
    const { stub } = createStub({ session })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const el = geometry(wrapper, 1000, 1000)
    await wrapper.find('.scroll').trigger('scroll')

    // A browser fires a scroll event for a programmatic move. happy-dom does not, so it is dispatched here —
    // and it must not be read as the reader's intent, or following dies the moment it starts.
    await (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh()
    await wrapper.find('.scroll').trigger('scroll')

    Object.defineProperty(el, 'scrollHeight', { value: 1500, configurable: true })
    await (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh()

    expect(el.scrollTop).toBe(1500)
  })

  it('stays put when the reader has scrolled back', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, conversation: { latestFromSeq: null, withdrawn: null }, context: usage }))
    const { stub } = createStub({ session })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await nextFrame()
    const el = geometry(wrapper, 1000, 100)
    await wrapper.find('.scroll').trigger('scroll')

    // Reading something older while the reply arrives must not yank the view.
    Object.defineProperty(el, 'scrollHeight', { value: 1400, configurable: true })
    await (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh()

    expect(el.scrollTop).toBe(100)
  })

  it('starts following when the reader sends something themselves', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: false, conversation: { latestFromSeq: null, withdrawn: null }, context: usage }))
    const { stub } = createStub({ session })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const el = geometry(wrapper, 1000, 100)
    await wrapper.find('.scroll').trigger('scroll')

    // Sending is an explicit "show me what happens", unlike a reply arriving while reading.
    await wrapper.findComponent(Composer).vm.$emit('sent', { itemId: 'in-1', deduplicated: false })
    await flushPromises()

    expect(el.scrollTop).toBe(1000)
  })
})

describe('session refresh cadence', () => {
  it('re-reads quickly while a turn is running, and backs off when it is over', async () => {
    vi.useFakeTimers()
    try {
      const usage = { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 }
      let busy = true
      const session = vi.fn(async () => ({ lastSeq: 0, items: [], busy, conversation: { latestFromSeq: null, withdrawn: null }, context: usage }))
      const { stub } = createStub({ session })

      const wrapper = mount(SessionView, { props: { api: stub } })
      await flushPromises()

      const afterMount = session.mock.calls.length

      // A turn in flight: the view keeps up with it.
      await vi.advanceTimersByTimeAsync(1200)
      const whileBusy = session.mock.calls.length
      expect(whileBusy).toBeGreaterThan(afterMount)

      // The turn ends. One more read is what tells the view that — the flag it is holding is the previous
      // answer, which still says busy.
      busy = false
      await vi.advanceTimersByTimeAsync(400)
      const afterNotice = session.mock.calls.length
      expect(afterNotice).toBeGreaterThan(whileBusy)

      // Backed off: an idle session costs no requests for the next second.
      await vi.advanceTimersByTimeAsync(1200)
      expect(session.mock.calls.length).toBe(afterNotice)

      // But work started elsewhere still shows up, just on the slow interval.
      await vi.advanceTimersByTimeAsync(5000)
      expect(session.mock.calls.length).toBeGreaterThan(afterNotice)

      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('memory view', () => {
  /** Types a query and presses Enter: the search box is the only way to search. */
  async function search(wrapper: ReturnType<typeof mount>, text: string) {
    await wrapper.find<HTMLInputElement>('[data-test="memory-query"]').setValue(text)
    await wrapper.find('[data-test="memory-query"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()
  }

  const pickTier = (wrapper: ReturnType<typeof mount>, value: string) =>
    wrapper.find(`[data-test="memory-tier"] button[data-value="${value}"]`).trigger('click')

  it('marks validity and switches the list to recall hits', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="memory-list"] li').text()).toContain('有效')

    await search(wrapper, 'jasmine')

    expect(calls.recalls).toEqual(['jasmine'])
    expect(wrapper.find('[data-test="memory-summary"]').text()).toContain('1 条命中')
    expect(wrapper.find('[data-test="memory-list"] li').text()).toContain('lexical')
  })

  it('filters hits the way it filters records, because a hit is one of those records', async () => {
    // A hit carries the record's own tier and scope; a placeholder tier ('recall') and scope ('-') match no
    // filter, leaving an empty list under a summary that still reports hits and an empty state that says 无记忆.
    const { stub } = createStub({
      recall: vi.fn(async () => [
        { recordId: 'm1', body: '部署在阿里云上', total: 0.9, reasons: ['lexical'], tier: 'Semantic', scope: 'Retain', createdAt: '2026-09-26T10:00:00+00:00' },
        { recordId: 'm2', body: '部署手册在 wiki', total: 0.8, reasons: ['lexical'], tier: 'Procedural', scope: 'Recall', createdAt: '2026-09-26T10:00:00+00:00' },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLInputElement>('[data-test="memory-pinned-only"]').setValue(true)
    await search(wrapper, '部署')

    // The pinned one survives the filter; the ordinary one is gone.
    const rows = wrapper.findAll('[data-test="memory-list"] li')
    expect(rows).toHaveLength(1)
    expect(rows[0].text()).toContain('部署在阿里云上')

    // And the count says both numbers, because the list shows one of the two hits.
    expect(wrapper.find('[data-test="memory-summary"]').text()).toBe('检索「部署」→ 2 条命中 · 筛选后 1 条')
  })

  it('says nothing was found rather than nothing is remembered when a search filters everything away', async () => {
    const { stub } = createStub({
      recall: vi.fn(async () => [
        { recordId: 'm2', body: '部署手册在 wiki', total: 0.8, reasons: ['lexical'], tier: 'Procedural', scope: 'Recall', createdAt: '2026-09-26T10:00:00+00:00' },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await pickTier(wrapper, 'Procedural')
    await search(wrapper, '部署')

    // Only the matching tier is left empty; the store plainly is not.
    await pickTier(wrapper, 'Working')
    await flushPromises()

    expect(wrapper.findAll('[data-test="memory-list"] li')).toHaveLength(0)
    expect(wrapper.find('[data-test="memory-empty"]').text()).toBe('没有找到相关的记忆')

    // Back to the stored records and the same filter shows nothing — which is a different sentence.
    await wrapper.find('[data-test="memory-back-to-all"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test="memory-empty"]').text()).toBe('助手还没记住任何事')
  })

  it('keeps the summary honest when the filter changes after the search', async () => {
    // The summary is computed from the rows, so a filter change cannot leave the pre-filter count on screen:
    // there is no path by which the sentence and the rows disagree.
    const { stub } = createStub({
      recall: vi.fn(async () => [
        { recordId: 'm1', body: '一', total: 0.9, reasons: ['lexical'], tier: 'Semantic', scope: 'Retain', createdAt: '2026-09-26T10:00:00+00:00' },
        { recordId: 'm2', body: '二', total: 0.8, reasons: ['lexical'], tier: 'Procedural', scope: 'Recall', createdAt: '2026-09-26T10:00:00+00:00' },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await search(wrapper, '部署')
    expect(wrapper.find('[data-test="memory-summary"]').text()).toBe('检索「部署」→ 2 条命中')

    await wrapper.find<HTMLInputElement>('[data-test="memory-pinned-only"]').setValue(true)
    await flushPromises()
    expect(wrapper.find('[data-test="memory-summary"]').text()).toBe('检索「部署」→ 2 条命中 · 筛选后 1 条')

    // And loosening it again puts the single number back rather than a second one.
    await wrapper.find<HTMLInputElement>('[data-test="memory-pinned-only"]').setValue(false)
    await flushPromises()
    expect(wrapper.find('[data-test="memory-summary"]').text()).toBe('检索「部署」→ 2 条命中')
  })

  it('shows the tier and pin a hit actually has, not a blank', async () => {
    // With the placeholder tier, selecting a hit left the tier on no option at all and the pin off whatever the
    // record actually was — the panel describing something that is not there.
    const { stub } = createStub({
      recall: vi.fn(async () => [
        { recordId: 'm1', body: '部署在阿里云上', total: 0.9, reasons: ['lexical'], tier: 'Semantic', scope: 'Retain', createdAt: '2026-09-26T10:00:00+00:00' },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await search(wrapper, '部署')
    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="memory-set-tier"] [aria-pressed="true"]').attributes('data-value')).toBe('Semantic')
    expect(wrapper.find<HTMLInputElement>('[data-test="memory-retain"]').element.checked).toBe(true)
  })

  it('edits the selected record in a drawer, and deleting it takes two presses', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    // Nothing is open until a card is chosen.
    expect(wrapper.find('[data-test="drawer"]').exists()).toBe(false)

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test="memory-target"]').text()).toBe('m1')

    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-draft"]').setValue('likes jasmine tea, no sugar')
    await wrapper.find('[data-test="memory-save"]').trigger('click')
    await flushPromises()

    expect(calls.memoryEdits).toEqual([{ recordId: 'm1', body: 'likes jasmine tea, no sugar' }])

    // A memory that is deleted is gone: the first press only arms it.
    await wrapper.find('[data-test="memory-delete"]').trigger('click')
    expect(calls.deletes).toEqual([])
    expect(wrapper.find('[data-test="memory-delete"]').text()).toContain('确认删除')

    await wrapper.find('[data-test="memory-delete"]').trigger('click')
    await flushPromises()

    expect(calls.deletes).toEqual(['m1'])
    expect(wrapper.find('[data-test="drawer"]').exists()).toBe(false)
  })

  it('hides retired memories until asked, and says what replaced them', async () => {
    // "Fully visible" means the retired record is still reachable: the useful answer to a conflict is "what
    // changed it", and that answer lives on the row that was replaced. It is behind a switch, not deleted.
    const { stub } = createStub({
      memoryAll: vi.fn(async () => [
        {
          recordId: 'm1',
          tier: 'Working',
          scope: 'Recall',
          body: '用户用 pnpm 作为包管理器',
          createdAt: '2026-09-26T10:00:00+00:00',
          lastRecalledAt: '2026-09-27T10:00:00+00:00',
          validTo: '2026-09-28T10:00:00+00:00',
          supersededBy: 'm2',
          editedBy: 'Agent',
          importance: 0.4,
        },
        {
          recordId: 'm2',
          tier: 'Semantic',
          scope: 'Recall',
          body: '用户改用 npm 作为包管理器了',
          createdAt: '2026-09-28T10:00:00+00:00',
          lastRecalledAt: null,
          validTo: null,
          supersededBy: null,
          editedBy: null,
          importance: 0.4,
        },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.findAll('[data-test="memory-list"] li')).toHaveLength(1)
    expect(wrapper.find('[data-test="memory-list"] li').text()).toContain('有效')

    await wrapper.find<HTMLInputElement>('[data-test="memory-show-retired"]').setValue(true)
    const rows = wrapper.findAll('[data-test="memory-list"] li')
    expect(rows).toHaveLength(2)
    expect(rows[0]?.text()).toContain('已失效 → m2')
    expect(rows[0]?.text()).toContain('已被 m2 取代')
    expect(rows[1]?.text()).toContain('有效')

    // The tier is named in words, not shown as an enum value: "刚记下" and "事实" answer questions a reader has.
    expect(rows[0]?.find('[data-test="memory-row-tier"]').text()).toContain('刚记下')
    expect(rows[1]?.find('[data-test="memory-row-tier"]').text()).toContain('事实')
  })

  it('lets a person move a memory between tiers and re-reads afterwards', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()

    await wrapper.find('[data-test="memory-set-tier"] button[data-value="Procedural"]').trigger('click')
    await flushPromises()

    expect(calls.tierChanges).toEqual([{ recordId: 'm1', tier: 'Procedural' }])
    // What is on screen is what the server kept, not what was sent.
    expect(stub.memoryAll).toHaveBeenCalledTimes(2)
  })

  it('offers all four tiers with a count, including the ones with nothing in them', async () => {
    const { stub } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    const options = wrapper.findAll('[data-test="memory-tier"] button').map((option) => option.text())
    expect(options).toHaveLength(5) // 全部 + 四类

    // Every tier is listed even with no rows: building the list from the loaded rows makes an empty tier
    // unselectable and shows a bare enum name for the rest, though "this tier is empty" and "this tier does not
    // exist" are different facts.
    expect(options[1]).toContain('刚记下')
    expect(options[2]).toContain('经历')
    expect(options[3]).toContain('事实')
    expect(options[4]).toContain('做法')
    expect(options[2]).toContain('0')
    expect(options[3]).toContain('1')
  })

  it('marks a pinned row and says what pinning does not do', async () => {
    const { stub } = createStub({
      memoryAll: vi.fn(async () => [
        {
          recordId: 'm1',
          tier: 'Semantic',
          scope: 'Retain',
          body: '部署在阿里云上',
          createdAt: '2026-09-26T10:00:00+00:00',
          lastRecalledAt: null,
          validTo: null,
          supersededBy: null,
          editedBy: 'Human',
          importance: 0.7,
        },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="memory-list"] li').find('[data-test="memory-row-retain"]').text()).toBe('固定')

    // Pinning is about time, not about being right. A person who corrects themselves means it.
    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test="memory-retain-hint"]').text()).toContain('仍然可以纠正')
  })

  it('pins a memory against forgetting and releases it again, re-reading each time', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()

    await wrapper.find<HTMLInputElement>('[data-test="memory-retain"]').setValue(true)
    await flushPromises()
    expect(calls.scopeChanges).toEqual([{ recordId: 'm1', scope: 'Retain' }])

    await wrapper.find<HTMLInputElement>('[data-test="memory-retain"]').setValue(false)
    await flushPromises()
    expect(calls.scopeChanges[1]).toEqual({ recordId: 'm1', scope: 'Recall' })

    // The norm: the row on screen is what the server kept, not what was sent.
    expect(stub.memoryAll).toHaveBeenCalledTimes(3)
  })

  it('keeps the tier picker usable while a record is pinned', async () => {
    const { stub } = createStub({
      memoryAll: vi.fn(async () => [
        {
          recordId: 'm1',
          tier: 'Semantic',
          scope: 'Retain',
          body: '部署在阿里云上',
          createdAt: '2026-09-26T10:00:00+00:00',
          lastRecalledAt: null,
          validTo: null,
          supersededBy: null,
          editedBy: null,
          importance: 0.7,
        },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()

    // Trust and time are separate axes, so a pinned 「刚记下」 is legal and there is nothing to lock.
    // Silently dropping the pin on a tier change would be a state change nobody asked for.
    const buttons = wrapper.findAll('[data-test="memory-set-tier"] button')
    expect(buttons).toHaveLength(4)
    expect(buttons.every((button) => button.attributes('disabled') === undefined)).toBe(true)
  })

  it('filters by pin', async () => {
    const { stub } = createStub({
      memoryAll: vi.fn(async () => [
        { recordId: 'm1', tier: 'Semantic', scope: 'Retain', body: '固定的那条', createdAt: '2026-09-26T10:00:00+00:00', lastRecalledAt: null, validTo: null, supersededBy: null, editedBy: null, importance: 0.7 },
        { recordId: 'm2', tier: 'Semantic', scope: 'Recall', body: '普通的那条', createdAt: '2026-09-26T10:00:00+00:00', lastRecalledAt: null, validTo: null, supersededBy: null, editedBy: null, importance: 0.7 },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLInputElement>('[data-test="memory-pinned-only"]').setValue(true)
    await flushPromises()

    const rows = wrapper.findAll('[data-test="memory-list"] li')
    expect(rows).toHaveLength(1)
    expect(rows[0]?.text()).toContain('固定的那条')
  })

  it('writes a memory the distiller would not have kept, and selects it afterwards', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-new"]').trigger('click')
    await flushPromises()

    // The default is an established fact: something a person typed is not a note waiting to be corroborated.
    expect(wrapper.find('[data-test="memory-new-tier"] [aria-pressed="true"]').attributes('data-value')).toBe('Semantic')
    expect(wrapper.find<HTMLInputElement>('[data-test="memory-new-pin"]').element.checked).toBe(false)

    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-new-body"]').setValue('每周五之前提交周报')
    await wrapper.find('[data-test="memory-new-submit"]').trigger('click')
    await flushPromises()

    expect(calls.creates).toEqual([{ body: '每周五之前提交周报', tier: 'Semantic', scope: 'Recall' }])
    expect(stub.memoryAll).toHaveBeenCalledTimes(2)
  })

  it('refuses to send a hand-written memory with no body at all', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-new"]').trigger('click')
    await flushPromises()
    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-new-body"]').setValue('  ')
    await wrapper.find('[data-test="memory-new-submit"]').trigger('click')
    await flushPromises()

    // No request: there is nothing here that any rule could accept.
    expect(calls.creates).toEqual([])
    expect(wrapper.find('[data-test="error"]').text()).toContain('不能为空')
  })

  it('says, while writing a memory, that deleting a conversation will not delete it', async () => {
    // 「删除之后」 and 「撤回」 take the memories distilled from a turn with them, and this one is deliberately
    // not one of those — it has no source event, so the cascade cannot find it. That is a defensible choice,
    // and it is only defensible if the person is told before they assume otherwise.
    const { stub } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-new"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="memory-new-cascade-hint"]').text()).toContain('删掉对话不会删掉它')
  })

  it('sends a short body and shows the server refusal, rather than keeping its own length rule', async () => {
    // The 4-character minimum belongs to the distiller (`ConversationDistiller.MinBodyLength`) and the server
    // enforces it. A second copy of the number in the view is a number that will drift: raise the rule there and
    // this one keeps refusing text the store would have taken. So the view sends and reports.
    const { stub, calls } = createStub({
      createMemory: vi.fn(async () => {
        throw new Error('正文至少 4 个字')
      }),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-new"]').trigger('click')
    await flushPromises()
    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-new-body"]').setValue('啊')
    await wrapper.find('[data-test="memory-new-submit"]').trigger('click')
    await flushPromises()

    expect(calls.creates).toEqual([])
    expect(wrapper.find('[data-test="error"]').text()).toContain('正文至少 4 个字')

    // The dialog stays open, so the text is still there to be fixed.
    expect(wrapper.find('[data-test="memory-composer"]').exists()).toBe(true)
  })
})
describe('skills view', () => {
  it('lists a workspace skill and says whether it is in use', async () => {
    const { stub } = createStub({
      // Beta is switched off, and the deny list is what the page reads: a skill the server reports as disabled
      // but the settings do not list would be saved straight back on, which is the bug this guards.
      settings: vi.fn(async () => ({ ...SETTINGS_FIXTURE, skills: { disabled: ['beta'] } })),
      skills: vi.fn(async () => ({
        skills: [
          { skillId: 'alpha', name: 'Alpha', description: '做某件事的做法', enabled: true, instructions: '第一步…' },
          { skillId: 'beta', name: 'Beta', description: '另一件事', enabled: false, instructions: '别的…' },
        ],
        warnings: ['skills/broken: 缺少 SKILL.md'],
      })),
    })

    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()

    const items = wrapper.findAll('[data-test="skills-item"]')
    expect(items).toHaveLength(2)
    expect(wrapper.text()).toContain('做某件事的做法')
    expect(wrapper.text()).toContain('1 / 2 启用')

    // A skill that did not load is otherwise invisible, and "it just is not there" is the worst way to find out
    // you wrote the front matter wrong.
    expect(wrapper.text()).toContain('缺少 SKILL.md')
  })

  it('shows what a disabled skill still says, because that is what the choice is judged on', async () => {
    const { stub } = createStub({
      skills: vi.fn(async () => ({
        skills: [{ skillId: 'alpha', name: 'Alpha', description: '做法', enabled: true, instructions: '第一步：先读 SKILL.md' }],
        warnings: [],
      })),
    })
    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.text()).not.toContain('第一步：先读 SKILL.md')
    await wrapper.find('[data-test="skills-instructions-toggle"]').trigger('click')
    expect(wrapper.text()).toContain('第一步：先读 SKILL.md')
  })

  const ALPHA = { skillId: 'alpha', name: 'Alpha', description: '做法', enabled: true, instructions: '第一步' }

  async function choose(wrapper: ReturnType<typeof mount>, file: File) {
    const input = wrapper.find('[data-test="skills-file"]')
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await flushPromises()
  }

  it('uploads a chosen file, then lists the skill it became', async () => {
    const listings = [[], [ALPHA]]
    const { stub } = createStub({
      skills: vi.fn(async () => ({ skills: listings.shift() ?? [ALPHA], warnings: [] })),
      uploadSkill: vi.fn(async () => ({ skillId: 'alpha', name: 'Alpha' })),
    })
    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="skills-upload"]').trigger('click')
    const file = new File(['---'], 'alpha.zip')
    await choose(wrapper, file)

    expect(stub.uploadSkill).toHaveBeenCalledWith(file, 'alpha.zip')
    expect(wrapper.findAll('[data-test="skills-item"]')).toHaveLength(1)
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })

  it('says why an upload was refused and lists nothing new', async () => {
    const { stub } = createStub({
      uploadSkill: vi.fn(async () => {
        throw new ApiError(400, '这不是有效的技能：缺少前置元数据')
      }),
    })
    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()

    await choose(wrapper, new File(['x'], 'bad.md'))

    expect(wrapper.text()).toContain('这不是有效的技能：缺少前置元数据')
    expect(wrapper.text()).not.toContain('400:')
    expect(wrapper.emitted('changed')).toBeUndefined()
  })

  it('writes a skill from the form, and keeps the form open with the reason when the server refuses', async () => {
    const createSkill = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(409, "已经有一个叫 'alpha' 的技能。"))
      .mockResolvedValueOnce({ skillId: 'alpha', name: 'Alpha' })
    const { stub } = createStub({ createSkill })
    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="skills-new"]').trigger('click')
    await wrapper.find('[data-test="skills-new-submit"]').trigger('click')
    expect(createSkill).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test="skills-new-error"]').text()).toBe('四项都要填')

    await wrapper.find('[data-test="skills-new-id"]').setValue(' alpha ')
    await wrapper.find('[data-test="skills-new-name"]').setValue('Alpha')
    await wrapper.find('[data-test="skills-new-description"]').setValue('做法')
    await wrapper.find('[data-test="skills-new-instructions"]').setValue('第一步')
    await wrapper.find('[data-test="skills-new-submit"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="skills-new-error"]').text()).toBe("已经有一个叫 'alpha' 的技能。")

    await wrapper.find('[data-test="skills-new-submit"]').trigger('click')
    await flushPromises()

    expect(createSkill).toHaveBeenLastCalledWith({ skillId: 'alpha', name: 'Alpha', description: '做法', instructions: '第一步' })
    expect(wrapper.find('[data-test="skills-composer"]').exists()).toBe(false)
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })

  it('deletes only on the second press, and leaves nothing unsaved behind', async () => {
    const listings = [[ALPHA], []]
    const { stub } = createStub({
      settings: vi.fn(async () => ({ ...SETTINGS_FIXTURE, skills: { disabled: ['alpha'] } })),
      skills: vi.fn(async () => ({ skills: listings.shift() ?? [], warnings: [] })),
    })
    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="skills-delete"]').trigger('click')
    expect(stub.deleteSkill).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test="skills-delete"]').text()).toContain('确认删除')

    await wrapper.find('[data-test="skills-delete"]').trigger('click')
    await flushPromises()

    expect(stub.deleteSkill).toHaveBeenCalledWith('alpha')
    expect(wrapper.findAll('[data-test="skills-item"]')).toHaveLength(0)
    // The server dropped the id from the deny list too; the page must not call that an unsaved change.
    expect((wrapper.vm as unknown as { hasUnsavedChanges(): boolean }).hasUnsavedChanges()).toBe(false)
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })

  it('keeps a switch the person flipped but has not saved when a skill is added', async () => {
    const { stub } = createStub({ skills: vi.fn(async () => ({ skills: [ALPHA], warnings: [] })) })
    const wrapper = mount(SkillsView, { props: { api: stub } })
    await flushPromises()
    await wrapper.find('[data-test="skills-toggle"]').setValue(false)

    await choose(wrapper, new File(['x'], 'more.md'))

    expect((wrapper.vm as unknown as { hasUnsavedChanges(): boolean }).hasUnsavedChanges()).toBe(true)
  })
})

describe('mcp view', () => {

  it('refuses to save a server with no command, rather than failing later at start-up', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(McpView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="mcp-server-add"]').trigger('click')
    await wrapper.find('[data-test="mcp-save"]').trigger('click')
    await flushPromises()

    // A blank command would otherwise start a process nobody can identify, and the failure would be one line
    // in a log at a moment when the reason is not obvious.
    expect(calls.saves).toEqual([])
    expect(wrapper.find('[data-test="error"]').text()).toContain('名称和启动命令')
  })

  it('saves a configured server as a settings change', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(McpView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="mcp-server-add"]').trigger('click')
    await wrapper.find('[data-test="mcp-server-name"]').setValue('files')
    await wrapper.find('[data-test="mcp-server-command"]').setValue('node')
    await wrapper.find('[data-test="mcp-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves).toHaveLength(1)
    expect(calls.saves[0]?.mcp.servers[0]?.name).toBe('files')
  })
})

describe('settings view', () => {
  it('says nothing for the top bar to repeat', async () => {
    // The bar's middle line would repeat the view name the sidebar and the page heading already give. Only the
    // session view has something to put there.
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.emitted('context')).toBeUndefined()
  })

  it('groups the form by what a person wants to do, not by module', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const nav = wrapper.findAll('.settings-nav .nav-item').map((node) => node.text())
    expect(nav).toEqual(['通用', '对话模型', '语音输入', '语音输出', '电脑操控', '文件访问', '联网搜索', '本地引擎', '外观'])

    // Every field is in the document whichever category is showing (the others are only hidden), so a change on
    // one category is never lost by looking at another.
    expect(wrapper.find('[data-test="asr-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="tts-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="local-tts-enabled"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="local-kws-enabled"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="desktop-enabled"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-api-key"]').exists()).toBe(true)
    // The wake words are edited where a person looks for them — under voice input — though they are stored with
    // the engine.
    expect(wrapper.find('[data-test="settings-section-input"] [data-test="local-kws-keywords"]').exists()).toBe(true)
  })

  it('shows the installed version and the release the check finds', async () => {
    // The check button belongs to the desktop shell; the version itself is shown without one.
    const invoke = vi.fn(async () => true)
    const globals = window as unknown as { __TAURI_INTERNALS__?: { invoke: typeof invoke } }
    globals.__TAURI_INTERNALS__ = { invoke }

    try {
      const { stub } = createStub({
        checkUpdate: vi.fn(async (current: string): Promise<UpdateCheck> => ({
          current,
          latest: '9.9.9',
          available: true,
          installerName: 'Partner_9.9.9_x64-setup.exe',
          sizeBytes: 20 * 1024 * 1024,
          error: null,
        })),
      })
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      expect(wrapper.find('[data-test="settings-version"]').text()).toBe(`v${packageVersion}`)

      await wrapper.find('[data-test="settings-check-update"]').trigger('click')
      await flushPromises()

      expect(wrapper.find('[data-test="settings-update-available"]').text()).toContain('9.9.9')
      expect(wrapper.find('[data-test="settings-update-install"]').exists()).toBe(true)
    } finally {
      delete globals.__TAURI_INTERNALS__
    }
  })

  it('hands a verified installer to the shell instead of keeping the app alive', async () => {
    const invoke = vi.fn(async (command: string) => (command === 'install_update' ? undefined : false))
    const globals = window as unknown as { __TAURI_INTERNALS__?: { invoke: typeof invoke } }
    globals.__TAURI_INTERNALS__ = { invoke }
    // The download state is polled, not pushed. Capture the interval instead of waiting for it, so the test is
    // driven by the callback the view registered and not by a guessed duration.
    const timers: { callback: () => void; milliseconds: number | undefined }[] = []
    const setIntervalSpy = vi.spyOn(window, 'setInterval').mockImplementation(((
      callback: () => void,
      milliseconds?: number,
    ) => {
      timers.push({ callback, milliseconds })
      return 1
    }) as typeof window.setInterval)

    try {
      const { stub } = createStub({
        checkUpdate: vi.fn(async (current: string): Promise<UpdateCheck> => ({
          current,
          latest: '9.9.9',
          available: true,
          installerName: 'Partner_9.9.9_x64-setup.exe',
          sizeBytes: 1024,
          error: null,
        })),
        downloadUpdate: vi.fn(async (): Promise<UpdateDownload> => ({
          phase: 'downloading',
          bytesDone: 0,
          bytesTotal: 1024,
          path: null,
          error: null,
        })),
        updateDownload: vi.fn(async (): Promise<UpdateDownload> => ({
          phase: 'ready',
          bytesDone: 1024,
          bytesTotal: 1024,
          path: 'C:\\Temp\\Partner_9.9.9_x64-setup.exe',
          error: null,
        })),
      })
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      await wrapper.find('[data-test="settings-check-update"]').trigger('click')
      await flushPromises()
      await wrapper.find('[data-test="settings-update-install"]').trigger('click')
      await flushPromises()
      expect(wrapper.find('[data-test="settings-update-progress"]').exists()).toBe(true)

      const poll = timers.find((timer) => timer.milliseconds === 600)
      expect(poll).toBeDefined()
      poll?.callback()
      await flushPromises()

      expect(invoke).toHaveBeenCalledWith('install_update', { path: 'C:\\Temp\\Partner_9.9.9_x64-setup.exe' })
      expect(wrapper.find('[data-test="settings-update-starting"]').exists()).toBe(true)
    } finally {
      setIntervalSpy.mockRestore()
      delete globals.__TAURI_INTERNALS__
    }
  })

  describe('local engine package', () => {
    const readiness = (installed: boolean) => ({ installed, missing: installed ? [] : ['tts/python/python.exe', 'tts/model/tokenizer.json'] })
    const packageStatus = (overrides: Record<string, unknown> = {}) => ({
      root: 'C:\\Apps\\Partner\\engines',
      customRoot: false,
      tts: readiness(false),
      kws: readiness(false),
      ttsDefaults: { command: 'engines/tts/python/python.exe', arguments: ['tts-server.py', '--port', '{port}'], workingDirectory: 'engines/tts', port: 45102 },
      kwsDefaults: { command: 'engines/kws-asr/python/python.exe', arguments: ['asr-server.py', '--port', '{port}'], workingDirectory: 'engines/kws-asr', port: 45103 },
      downloadAvailable: true,
      download: { phase: 'idle', detail: null, bytesDone: 0, bytesTotal: null, error: null },
      ...overrides,
    })

    it('offers download, detect and manual pick, and hides the engine settings, while nothing is installed', async () => {
      const { stub } = createStub()
      vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus() as never)
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      expect(wrapper.find('[data-test="engine-download"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="engine-detect"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="engine-manual"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="engine-missing-tts"]').text()).toContain('tts/python/python.exe')
      expect(wrapper.find('[data-test="local-engine-tts"]').exists()).toBe(false)
      expect(wrapper.find('[data-test="local-engine-kws"]').exists()).toBe(false)
    })

    it('shows the engine settings with package-relative defaults once the engines are found', async () => {
      const { stub } = createStub()
      vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus({ tts: readiness(true), kws: readiness(true) }) as never)
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      expect(wrapper.find('[data-test="local-engine-tts"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="local-engine-kws"]').exists()).toBe(true)
      expect((wrapper.find('[data-test="local-tts-command"]').element as HTMLInputElement).value).toBe('engines/tts/python/python.exe')
      expect((wrapper.find('[data-test="local-kws-arguments"]').element as HTMLInputElement).value).toBe('asr-server.py --port {port}')
      expect(wrapper.find('[data-test="engine-download"]').exists()).toBe(false)
    })

    it('starts the download and reports its progress, then fills the settings when it finishes', async () => {
      vi.useFakeTimers()
      try {
        const { stub } = createStub()
        const running = packageStatus({ download: { phase: 'downloading', detail: '朗读引擎（1/2）', bytesDone: 512 * 1024 ** 2, bytesTotal: 1024 ** 3, error: null } })
        vi.mocked(stub.enginePackage).mockResolvedValueOnce(packageStatus() as never)
        vi.mocked(stub.downloadEnginePackage).mockResolvedValue(running as never)
        const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
        await flushPromises()

        await wrapper.find('[data-test="engine-download"]').trigger('click')
        await flushPromises()
        expect(wrapper.find('[data-test="engine-progress"]').text()).toContain('512 MB / 1.00 GB（50%）')
        expect(wrapper.find('[data-test="engine-cancel"]').exists()).toBe(true)

        vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus({ tts: readiness(true), kws: readiness(true), download: { phase: 'done', detail: null, bytesDone: 0, bytesTotal: null, error: null } }) as never)
        await vi.advanceTimersByTimeAsync(1100)
        await flushPromises()

        expect(wrapper.find('[data-test="engine-note"]').text()).toContain('下载完成')
        expect((wrapper.find('[data-test="local-tts-command"]').element as HTMLInputElement).value).toBe('engines/tts/python/python.exe')
        expect(wrapper.find('[data-test="engine-progress"]').exists()).toBe(false)
      } finally {
        vi.useRealTimers()
      }
    })

    it('defaults the recognition and speech clients to the local engines', async () => {
      const { stub } = createStub()
      vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus({ tts: readiness(true), kws: readiness(true) }) as never)
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      // No separate "point at the engine" step: the client addresses are filled in when the stored ones are empty.
      expect(wrapper.find('[data-test="local-kws-link"]').exists()).toBe(false)
      expect((wrapper.find('[data-test="asr-base-url"]').element as HTMLInputElement).value).toBe('http://127.0.0.1:45103')
      expect((wrapper.find('[data-test="asr-protocol"]').element as HTMLSelectElement).value).toBe('sensevoice')
      expect((wrapper.find('[data-test="tts-base-url"]').element as HTMLInputElement).value).toBe('http://127.0.0.1:45102')
      expect((wrapper.find('[data-test="tts-protocol"]').element as HTMLSelectElement).value).toBe('local')
    })

    it('keeps the connection tests on the voice pages only', async () => {
      const { stub } = createStub()
      vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus({ tts: readiness(true), kws: readiness(true) }) as never)
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      expect(wrapper.find('[data-test="local-engine-tts"] [data-test="tts-engine-test-row"]').exists()).toBe(false)
      expect(wrapper.find('[data-test="local-engine-kws"] [data-test="kws-test-row"]').exists()).toBe(false)
      expect(wrapper.find('[data-test="tts-test-row"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="asr-test-row"]').exists()).toBe(true)
    })

    it('uploads a reference clip for voice cloning and keeps the stored path', async () => {
      const { stub } = createStub()
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      expect(wrapper.find('[data-test="tts-clone-name"]').text()).toBe('未选择')
      const input = wrapper.find('[data-test="tts-clone-file"]')
      const file = new File(['RIFF'], '我的声音.wav', { type: 'audio/wav' })
      Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
      await input.trigger('change')
      await flushPromises()

      expect(stub.uploadVoiceReference).toHaveBeenCalledWith(file, '我的声音.wav')
      expect(wrapper.find('[data-test="tts-clone-name"]').text()).toBe('我的声音.wav')

      await wrapper.find('[data-test="tts-clone-clear"]').trigger('click')
      expect(wrapper.find('[data-test="tts-clone-name"]').text()).toBe('未选择')
    })

    it('says why a reference clip was refused', async () => {
      const { stub } = createStub()
      vi.mocked(stub.uploadVoiceReference).mockRejectedValue(new Error('参考音频需要是 .wav 文件'))
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      const input = wrapper.find('[data-test="tts-clone-file"]')
      Object.defineProperty(input.element, 'files', { value: [new File(['x'], 'a.mp3')], configurable: true })
      await input.trigger('change')
      await flushPromises()

      expect(wrapper.find('[data-test="tts-clone-error"]').text()).toContain('.wav')
    })

    it('offers only local and standard interfaces', async () => {
      const { stub } = createStub()
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      const values = (test: string) => wrapper.findAll(`[data-test="${test}"] option`).map((option) => option.attributes('value'))
      expect(values('asr-protocol')).toEqual(['sensevoice', 'openai'])
      expect(values('tts-protocol')).toEqual(['local', 'openai'])
    })

    it('says why a download cannot start instead of failing silently', async () => {
      const { stub } = createStub()
      vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus({ downloadAvailable: false }) as never)
      vi.mocked(stub.downloadEnginePackage).mockRejectedValue(new Error('还没有配置下载地址'))
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      await wrapper.find('[data-test="engine-download"]').trigger('click')
      await flushPromises()

      expect(wrapper.find('[data-test="engine-note"]').text()).toContain('还没有配置下载地址')
    })

    it('points at a folder the user already has and shows the server\'s reason when it holds no engine', async () => {
      const { stub } = createStub()
      vi.mocked(stub.enginePackage).mockResolvedValue(packageStatus() as never)
      vi.mocked(stub.setEngineRoot).mockRejectedValueOnce(new Error('这个目录里没有找到完整的引擎'))
      const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
      await flushPromises()

      await wrapper.find('[data-test="engine-manual"]').trigger('click')
      await wrapper.find('[data-test="engine-manual-root"]').setValue('D:\\empty')
      await wrapper.find('[data-test="engine-manual-apply"]').trigger('click')
      await flushPromises()
      expect(wrapper.find('[data-test="engine-note"]').text()).toContain('没有找到完整的引擎')
      expect(wrapper.find('[data-test="local-engine-tts"]').exists()).toBe(false)

      vi.mocked(stub.setEngineRoot).mockResolvedValueOnce(packageStatus({
        root: 'D:\\mine',
        customRoot: true,
        tts: readiness(true),
        ttsDefaults: { command: 'D:\\mine\\tts\\python\\python.exe', arguments: ['tts-server.py', '--port', '{port}'], workingDirectory: 'D:\\mine\\tts', port: 45102 },
      }) as never)
      await wrapper.find('[data-test="engine-manual-root"]').setValue('D:\\mine')
      await wrapper.find('[data-test="engine-manual-apply"]').trigger('click')
      await flushPromises()

      expect(stub.setEngineRoot).toHaveBeenLastCalledWith('D:\\mine')
      expect((wrapper.find('[data-test="local-tts-command"]').element as HTMLInputElement).value).toBe('D:\\mine\\tts\\python\\python.exe')
    })
  })

  it('shows one category at a time and remembers which', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const shown = (key: string) => !(wrapper.find(`[data-test="settings-section-${key}"]`).attributes('style') ?? '').includes('display: none')
    expect(shown('model')).toBe(true)
    expect(shown('input')).toBe(false)

    await wrapper.find('[data-test="settings-nav-input"]').trigger('click')
    expect(shown('model')).toBe(false)
    expect(shown('input')).toBe(true)
    expect(wrapper.find('[data-test="settings-nav-input"]').attributes('aria-current')).toBe('true')

    // Remembered, because the views are `v-if`: a control that forgets itself on every tab switch is worse than none.
    const second = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()
    expect(second.find('[data-test="settings-nav-input"]').attributes('aria-current')).toBe('true')
    second.unmount()
  })

  it('shows the three model limits in the connection card, with no 更多选项', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="settings-more-model"]').exists()).toBe(false)

    const card = wrapper.find('[data-test="model-api-key"]').element.closest('.g-card')
    expect(card).not.toBeNull()
    const labels = [...(card?.querySelectorAll('.g-row-label') ?? [])].map((node) => node.textContent?.trim())
    expect(labels).toContain('最长等待')
    expect(labels).toContain('记忆长度')
    expect(labels).toContain('单次回复上限')
  })

  it('offers the image capability, and saves it with the model', async () => {
    // The switch that decides whether blind apps are handled by showing the model a screenshot instead of OCR.
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const box = wrapper.find<HTMLInputElement>('[data-test=model-supports-images]')
    expect(box.exists()).toBe(true)
    expect(box.element.checked).toBe(false)

    await box.setValue(true)
    await wrapper.find('[data-test=settings-save]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.model.supportsImages).toBe(true)
  })

  it('shows the thinking gears as a mapping and edits it as a list with a default', async () => {
    // The strength is not chosen here. The table shows the name next to the value that is sent; rows are added
    // and removed, and one row is marked as the default.
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const values = () => wrapper.findAll('[data-test="reasoning-row"]').map((node) => node.attributes('data-value'))
    expect(values()).toEqual(['off', 'low', 'medium', 'high'])
    const row = (value: string) => wrapper.find(`[data-test="reasoning-row"][data-value="${value}"]`)
    expect((row('medium').find('[data-test="reasoning-label"]').element as HTMLInputElement).value).toBe('中')
    expect(row('medium').text()).toContain('medium')
    expect(wrapper.find('[data-test="reasoning-row"].is-default').attributes('data-value')).toBe('medium')

    // The default is a radio on the row.
    await wrapper.find('[data-test="reasoning-row"][data-value="high"] [data-test="reasoning-default"]').setValue(true)
    expect(wrapper.find('[data-test="reasoning-row"].is-default').attributes('data-value')).toBe('high')

    // A new row takes a display text and a value; the value is trimmed and lower-cased.
    await wrapper.find('[data-test="reasoning-add-label"]').setValue(' 极速 ')
    const add = wrapper.find('[data-test="reasoning-add-input"]')
    await add.setValue('  TURBO ')
    await add.trigger('keydown', { key: 'Enter' })
    expect(values()).toEqual(['off', 'low', 'medium', 'high', 'turbo'])
    expect((row('turbo').find('[data-test="reasoning-label"]').element as HTMLInputElement).value).toBe('极速')

    // A row's text can be changed in place; emptying it falls back to the built-in name.
    await row('low').find('[data-test="reasoning-label"]').setValue('浅')
    await row('low').find('[data-test="reasoning-label"]').trigger('change')

    // Moving the first row down swaps it with its neighbour.
    await wrapper.findAll('[data-test="reasoning-down"]')[0].trigger('click')
    expect(values()).toEqual(['low', 'off', 'medium', 'high', 'turbo'])

    // Removing a row that is not the default leaves the default where it was.
    await wrapper.findAll('[data-test="reasoning-remove"]')[0].trigger('click')
    expect(values()).toEqual(['off', 'medium', 'high', 'turbo'])

    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.model.reasoningGears).toEqual(['off', 'medium', 'high', 'turbo'])
    expect(calls.saves[0]?.model.reasoningEffort).toBe('high')
    expect(calls.saves[0]?.model.reasoningLabels).toEqual({ turbo: '极速' }) // low's text went with its row
  })

  it('moves the default to the first row when the default row is removed', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="reasoning-row"][data-value="medium"] [data-test="reasoning-remove"]').trigger('click')
    expect(wrapper.find('[data-test="reasoning-row"].is-default').attributes('data-value')).toBe('off')

    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()
    expect(calls.saves[0]?.model.reasoningEffort).toBe('off')
  })

  it('drops a duplicate or blank value rather than storing a row that can never be told apart', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const add = wrapper.find('[data-test="reasoning-add-input"]')
    await add.setValue('OFF')
    await add.trigger('keydown', { key: 'Enter' })
    await add.setValue('   ')
    await add.trigger('keydown', { key: 'Enter' })

    expect(wrapper.findAll('[data-test="reasoning-row"]')).toHaveLength(4)
    // The list did not change, so there is nothing to save.
    expect(wrapper.find('[data-test="settings-dirty"]').exists()).toBe(false)
  })

  it('never fills the key field from the server and says whether one is stored', async () => {
    const { stub } = createStub({
      settings: vi.fn(async () => ({
        input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, wakeTimeoutMs: 5000, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40,
    minSentenceChars: 10, wakeTone: true, bargeIn: false },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
        local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
        model: { protocol: 'openai', baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
      desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
      mcp: { enabled: false, servers: [], allowedTools: [] },
      skills: { disabled: [] },
      agents: { main: { prompt: null }, subAgents: [] },
      files: { enabled: false, roots: [] },
      web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // The key is write-only: the server has one, the field must not.
    expect(wrapper.find<HTMLInputElement>('[data-test="model-api-key"]').element.value).toBe('')
    expect(wrapper.find('[data-test="model-api-key-state"]').text()).toBe('已设置')
    expect(wrapper.find('[data-test="model-api-key-hint"]').text()).toContain('留空表示不改')
  })

  it('does not send an untouched key, and sends an explicit clear when asked', async () => {
    const { stub, calls } = createStub({
      settings: vi.fn(async () => ({
        input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, wakeTimeoutMs: 5000, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40,
    minSentenceChars: 10, wakeTone: true, bargeIn: false },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
        local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
        model: { protocol: 'openai', baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
      desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
      mcp: { enabled: false, servers: [], allowedTools: [] },
      skills: { disabled: [] },
      agents: { main: { prompt: null }, subAgents: [] },
      files: { enabled: false, roots: [] },
      web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // The save button only exists while there is something to save, so make an unrelated edit first.
    await wrapper.find<HTMLInputElement>('[data-test="model-max-tokens"]').setValue('2048')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    // An edit to an unrelated field must not wipe the stored key.
    expect(calls.saves[0]?.model).not.toHaveProperty('apiKey')

    await wrapper.find('[data-test="model-api-key-clear"]').trigger('click')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves[1]?.model).toHaveProperty('apiKey', '')
  })

  it('says whether the queue will be executed at all', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // No endpoint filled in, no consumer running: the page must not imply the settings took effect.
    expect(wrapper.find('[data-test="model-effect"]').text()).toContain('未配置')
  })

  it('reports each voice half, so an enabled-but-dead one is visible', async () => {
    const { stub } = createStub({
      voice: vi.fn(async () => ({
        input: {
          running: true,
          reason: '运行中',
          device: 'synthetic-mic',
          count: 3,
          rms: 0.0621,
          ambientRms: 0.004,
          thresholdMisconfigured: false,
          awake: false,
        },
        output: { running: false, reason: '未配置 TTS 服务地址', device: null, count: 0 },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="input-effect"]').text()).toContain('synthetic-mic')
    expect(wrapper.find('[data-test="input-effect"]').text()).toContain('已识别 3 段')
    // Enabled but not running: the reason must be on screen, not in a log.
    expect(wrapper.find('[data-test="output-effect"]').text()).toContain('未配置 TTS')
  })

  it('shows the live input level, so a dead microphone is not just "运行中"', async () => {
    const { stub } = createStub({
      voice: vi.fn(async () => ({
        input: {
          running: true,
          reason: '运行中',
          device: 'synthetic-mic',
          count: 0,
          rms: 0.0621,
          ambientRms: 0.004,
          thresholdMisconfigured: false,
          awake: false,
        },
        output: { running: false, reason: '未配置 TTS 服务地址', device: null, count: 0 },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const level = wrapper.find('[data-test="input-level"]')
    expect(level.exists()).toBe(true)
    // The number itself, not a bar whose width nobody can read off a screenshot.
    expect(level.text()).toContain('0.0621')
    expect(level.text()).toContain('0.03')
    // The threshold is drawn on the same track: "is the level above it" is the question being asked.
    expect(wrapper.find('[data-test="input-level"] .level-mark').attributes('style')).toContain('15.0%')
    wrapper.unmount()
  })

  it('says when the threshold sits below the noise floor instead of looking healthy', async () => {
    const { stub } = createStub({
      voice: vi.fn(async () => ({
        input: {
          running: true,
          reason: '运行中',
          device: 'synthetic-mic',
          count: 0,
          rms: 0.02,
          ambientRms: 0.055,
          thresholdMisconfigured: true,
          awake: false,
        },
        output: { running: false, reason: '未配置 TTS 服务地址', device: null, count: 0 },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const warning = wrapper.find('[data-test="input-threshold-warning"]')
    expect(warning.exists()).toBe(true)
    expect(warning.text()).toContain('0.055')
    wrapper.unmount()
  })

  it('offers the devices the runtime can see instead of asking for a name', async () => {
    const { stub } = createStub({
      devices: vi.fn(async () => ({ inputs: ['麦克风 (USB)', '麦克风 (NVIDIA Broadcast)'], outputs: ['扬声器 (Realtek)'] })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const inputDevice = wrapper.find<HTMLSelectElement>('[data-test="input-device"]')
    expect(inputDevice.element.tagName).toBe('SELECT')

    const options = inputDevice.findAll('option').map((option) => option.text())
    expect(options).toEqual(['（默认设备）', '麦克风 (USB)', '麦克风 (NVIDIA Broadcast)'])

    // The output list is separate: picking a microphone for playback is not a thing.
    expect(wrapper.find<HTMLSelectElement>('[data-test="output-device"]').findAll('option').map((o) => o.text())).toEqual([
      '（默认设备）',
      '扬声器 (Realtek)',
    ])
  })

  it('keeps a stored device that no longer enumerates selectable', async () => {
    const { stub } = createStub({
      settings: vi.fn(async () => ({
        input: { enabled: false, deviceId: '拔掉的麦克风', silenceThreshold: 0.03, utteranceGapMs: 1200, wakeTimeoutMs: 5000, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40,
    minSentenceChars: 10, wakeTone: true, bargeIn: false },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
        local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
        model: { protocol: 'openai', baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
        desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
        mcp: { enabled: false, servers: [], allowedTools: [] },
        skills: { disabled: [] },
        agents: { main: { prompt: null }, subAgents: [] },
        files: { enabled: false, roots: [] },
        web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
      })),
      devices: vi.fn(async () => ({ inputs: ['麦克风 (USB)'], outputs: [] })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const select = wrapper.find<HTMLSelectElement>('[data-test="input-device"]')
    // A device that is unplugged today must not silently become "default" on the next save.
    expect(select.element.value).toBe('拔掉的麦克风')
    expect(select.findAll('option').map((o) => o.text())).toContain('拔掉的麦克风')
  })

  it('falls back to typing when there are no devices to offer', async () => {
    const { stub } = createStub({ devices: vi.fn(async () => ({ inputs: [], outputs: [] })) })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="input-device"]').element.tagName).toBe('INPUT')
  })

  it('pulls the model list and turns the name field into a picker', async () => {
    const { stub } = createStub({
      listModels: vi.fn(async () => ({ baseUrl: 'http://127.0.0.1:11434/v1', models: ['llama3', 'qwen3:8b'] })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // Before pulling, the field is free text — a local endpoint may not implement the listing at all.
    expect(wrapper.find('[data-test="model-name"]').element.tagName).toBe('INPUT')

    await wrapper.find('[data-test="model-probe"]').trigger('click')
    await flushPromises()

    const select = wrapper.find<HTMLSelectElement>('[data-test="model-name"]')
    expect(select.element.tagName).toBe('SELECT')
    expect(select.findAll('option').map((o) => o.text())).toEqual(['llama3', 'qwen3:8b'])
    expect(wrapper.find('[data-test="model-count"]').text()).toContain('2 个可选')
  })

  it('reports why a model probe failed instead of showing an empty list', async () => {
    const { stub } = createStub({
      listModels: vi.fn(async () => ({
        baseUrl: 'http://127.0.0.1:9/v1',
        models: [],
        error: '列出模型返回 404 NotFound',
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="model-probe"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="model-probe-error"]').text()).toContain('404')
    // The field stays typeable: an endpoint without a listing is still a usable endpoint.
    expect(wrapper.find('[data-test="model-name"]').element.tagName).toBe('INPUT')
  })

  it('saves the selected model protocol', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const protocol = wrapper.find<HTMLSelectElement>('[data-test="model-protocol"]')
    expect(protocol.element.value).toBe('openai')

    await protocol.setValue('anthropic')
    await wrapper.find('[data-test=settings-save]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.model).toHaveProperty('protocol', 'anthropic')
  })

  it('shows the context and output limits, and reports what a shrink did', async () => {
    const { stub, calls } = createStub({
      saveSettings: vi.fn(async (patch: RuntimeSettings) => {
        calls.saves.push(patch)
        return {
          settings: {
            input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, wakeTimeoutMs: 5000, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40, minSentenceChars: 10, wakeTone: true, bargeIn: false },
            output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
            asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
            tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
            local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
            model: { protocol: 'openai', baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: patch.model.contextTokens, maxTokens: patch.model.maxTokens, supportsImages: patch.model.supportsImages, reasoningEffort: patch.model.reasoningEffort, reasoningGears: patch.model.reasoningGears },
            desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
            mcp: { enabled: false, servers: [], allowedTools: [] },
            skills: { disabled: [] },
            agents: { main: { prompt: null }, subAgents: [] },
            files: { enabled: false, roots: [] },
            web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
          },
          consumer: { running: true, reason: '运行中（设置）', model: 'm', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
          voice: { input: { running: false, reason: '未启用', device: null, count: 0, rms: 0, ambientRms: 0, thresholdMisconfigured: false, awake: false }, output: { running: false, reason: '未启用', device: null, count: 0 } },
          compaction: { foldedEvents: 3, summarySeq: 9, reason: '已压缩 3 条 → #9', compacted: true },
        }
      }),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find<HTMLInputElement>('[data-test="model-context-tokens"]').element.value).toBe('32768')
    expect(wrapper.find<HTMLInputElement>('[data-test="model-max-tokens"]').element.value).toBe('4096')

    await wrapper.find<HTMLInputElement>('[data-test="model-context-tokens"]').setValue('5')
    await wrapper.find('[data-test=settings-save]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.model).toHaveProperty('contextTokens', 5)
    // The shrink is reported: it changed what the next turn will be given, not just a number in a form.
    expect(wrapper.find('[data-test=settings-status]').text()).toContain('压缩')
  })

  it('waits for the button for every field, including the switches', async () => {
    // Every field, including the switches, waits for the button: a page where some controls save themselves
    // and others do not has no visible rule.
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()
    expect(calls.saves).toEqual([])

    for (const field of ['input-enabled', 'input-asr-stream', 'output-enabled', 'output-mute-during-tts']) {
      const box = wrapper.find(`[data-test="${field}"]`)
      expect(box.exists()).toBe(true)
      await box.setValue(true)
      await flushPromises()
      expect(calls.saves).toEqual([])
    }

    // …and the button commits all of them at once.
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()
    expect(calls.saves).toHaveLength(1)
    expect(calls.saves[0]?.input.enabled).toBe(true)
    expect(calls.saves[0]?.output.muteDuringTts).toBe(true)
  })

  it('says when there is something to save, and stops saying so once saved', async () => {
    // Without this, explicit saving just trades one surprise for another: the views are `v-if`, so switching
    // tabs would throw the edits away with no word. The dirty mark is what makes the state legible.
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="settings-dirty"]').exists()).toBe(false)

    await wrapper.find<HTMLInputElement>('[data-test="utterance-gap"]').setValue('750')
    expect(wrapper.find('[data-test="settings-dirty"]').exists()).toBe(true)

    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    // Saved, so not dirty any more — including because `apply()` rewrote the form underneath it.
    expect(calls.saves).toHaveLength(1)
    expect(wrapper.find('[data-test="settings-dirty"]').exists()).toBe(false)

    // Typing it back to where it was is also not a change.
    await wrapper.find<HTMLInputElement>('[data-test="utterance-gap"]').setValue('1200')
    expect(wrapper.find('[data-test="settings-dirty"]').exists()).toBe(false)
  })



  it('loads the stored values and shows what the API kept', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find<HTMLInputElement>('[data-test="utterance-gap"]').element.value).toBe('1200')

    await wrapper.find<HTMLInputElement>('[data-test="utterance-gap"]').setValue('750')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves).toEqual([expect.objectContaining({ input: expect.objectContaining({ utteranceGapMs: 750 }) })])
    // The status carries a timestamp, so the exact wording is not fixed.
    const status = wrapper.find('[data-test="settings-status"]').text()
    expect(status).toContain('已保存')
    expect(status).toMatch(/\d{2}:\d{2}:\d{2}/)
  })

  it('saves a voice change without erasing the servers and skills decided on their own pages', async () => {
    // The whole settings object goes out in one PUT, so a segment this page never reads comes back empty
    // unless it was read. Saving a voice setting would then silently delete every MCP server and every
    // skill the person had switched off.
    const { stub, calls } = createStub({
      settings: vi.fn(async () => ({
        ...SETTINGS_FIXTURE,
        mcp: {
          enabled: true,
          servers: [{ name: 'files', command: 'node', arguments: ['server.js'], workingDirectory: null, callTimeoutSeconds: 30 }],
          allowedTools: ['read_file'],
        },
        skills: { disabled: ['greet'] },
      })),
    })
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLInputElement>('[data-test="utterance-gap"]').setValue('900')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves).toHaveLength(1)
    expect(calls.saves[0]?.mcp.servers.map((server) => server.name)).toEqual(['files'])
    expect(calls.saves[0]?.mcp.allowedTools).toEqual(['read_file'])
    expect(calls.saves[0]?.skills.disabled).toEqual(['greet'])
  })

  it('saves a voice change without erasing the agents and the file whitelist', async () => {
    // Same hazard as the servers and skills above: the prompts page writes the agents section and this page
    // carries it. A save made here that dropped it would reset every prompt and delete the file whitelist.
    const { stub, calls } = createStub({
      settings: vi.fn(async () => ({
        ...SETTINGS_FIXTURE,
        agents: {
          main: { prompt: '你是我的人。' },
          subAgents: [
            { id: 'desktop', name: '桌面操控', enabled: false, scenario: 'desktop', prompt: null, maxRounds: 5, model: null },
            { id: 'agent-a1', name: '读文件的', enabled: true, scenario: 'file-read', prompt: '只读。', maxRounds: 2, model: { protocol: 'openai', baseUrl: 'http://x/v1', model: 'm', apiKeySet: true, timeoutSeconds: 60, contextTokens: 8192, maxTokens: 1024, supportsImages: false, reasoningEffort: 'medium' } },
          ],
        },
        files: { enabled: true, roots: ['D:/资料', 'E:/扫描'] },
        web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
      })),
    })
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLInputElement>('[data-test="utterance-gap"]').setValue('900')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves).toHaveLength(1)
    expect(calls.saves[0]?.agents.main.prompt).toBe('你是我的人。')
    expect(calls.saves[0]?.agents.subAgents.map((agent) => agent.id)).toEqual(['desktop', 'agent-a1'])
    expect(calls.saves[0]?.agents.subAgents[1]?.prompt).toBe('只读。')
    expect(calls.saves[0]?.files).toEqual({ enabled: true, roots: ['D:/资料', 'E:/扫描'] })
  })

  it('offers the file whitelist, and saves the directories it was given', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // Off by default, and the note says what the list means rather than leaving it to be guessed. It lives in
    // the card heading's hover mark now, so it is asked for rather than printed over the form.
    expect(wrapper.find<HTMLInputElement>('[data-test="files-enabled"]').element.checked).toBe(false)
    expect(wrapper.find('[data-test="files-whitelist-hint"]').text()).toContain('只能读')

    await wrapper.find('[data-test="files-enabled"]').setValue(true)
    await wrapper.find('[data-test="files-root-add"]').trigger('click')
    await wrapper.findAll<HTMLInputElement>('[data-test="files-root"]')[0].setValue('D:/资料')
    await wrapper.find('[data-test="files-root-add"]').trigger('click')
    await wrapper.findAll<HTMLInputElement>('[data-test="files-root"]')[1].setValue('  ')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    // The blank row is dropped rather than sent: the server would reject the whole save over an empty path.
    expect(calls.saves[0]?.files).toEqual({ enabled: true, roots: ['D:/资料'] })
  })

  it('says where a query goes before it says anything else about web search', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="settings-section-web"]').exists()).toBe(true)

    // The one capability on this page that starts on, and the one that sends what was typed off the machine.
    // Both facts are in the section — the second as a sentence of its own, not behind a hover.
    expect(wrapper.find<HTMLInputElement>('[data-test="web-enabled"]').element.checked).toBe(true)
    expect(wrapper.find('[data-test="web-callout"]').text()).toContain('会离开这台电脑')
    expect(wrapper.find('[data-test="web-effect"]').text()).toContain('已开启')

    // A paused engine says so where the switch that would switch it off lives. In an answer "没搜到" and
    // "这家停了" are the same sentence and they are not the same fact.
    const paused = wrapper.find('[data-test="web-engine-wikipedia"] [data-test="web-engine-state"]')
    expect(paused.text()).toContain('连续失败 3 次')
    expect(paused.text()).toContain('暂停中')

    // Switching one off stores "who was switched off", not a list of everyone who is on: an engine added by a
    // later build is then on by default rather than silently missing.
    await wrapper.find('[data-test="web-engine-duckduckgo-enabled"]').setValue(false)
    await wrapper.find('[data-test="web-proxy"]').setValue('  http://127.0.0.1:7890  ')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.web).toEqual({
      enabled: true,
      timeBudgetSeconds: 12,
      maxResults: 8,
      proxy: 'http://127.0.0.1:7890',
      disabledEngines: ['duckduckgo'],
    })
  })

  it('puts a field explanation behind a mark instead of a second line under the field', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // The sentence lives behind a mark rather than as a line of its own, so the controls above and below it
    // stay one row closer.
    const field = wrapper.findAll('label.g-row').find((label) => label.text().includes('多小声算安静'))
    expect(field).toBeDefined()
    expect(field!.find('[data-icon="info"]').exists()).toBe(true)
    expect(field!.find('.hint-pop').text()).toContain('音量低于这个值')

    // The unit is a suffix on the control, not part of the name.
    const gap = wrapper.findAll('label.g-row').find((label) => label.text().includes('停顿多久算说完'))
    expect(gap!.find('.g-unit').text()).toBe('毫秒')
    expect(gap!.find('.g-row-label').text()).not.toContain('ms')

    // The category's own explanation sits on its first switch.
    const first = wrapper.find('[data-test="input-enabled"]').element.closest('label')
    expect(first?.textContent).toContain('关闭后会立刻释放麦克风')
  })

  it('leaves the live status lines where they were, visible', async () => {
    // The mark is for sentences that explain; a line that reports what is happening right now is not one of
    // those, and hiding it behind a hover would mean a microphone that is silently running.
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="input-effect"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="output-effect"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-effect"]').exists()).toBe(true)
  })

  it('warns when desktop is switched on while its sub-agent is disabled, and offers the way there', async () => {
    const { stub } = createStub({
      settings: vi.fn(async () => ({
        ...SETTINGS_FIXTURE,
        agents: {
          main: { prompt: null },
          subAgents: [
            { id: 'desktop', name: '桌面操控', enabled: false, scenario: 'desktop', prompt: null, maxRounds: 5, model: null },
          ],
        },
      })),
    })
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // Off to begin with: nothing to warn about.
    expect(wrapper.find('[data-test="desktop-agent-warning"]').exists()).toBe(false)

    await wrapper.find('[data-test="desktop-enabled"]').setValue(true)
    const warning = wrapper.find('[data-test="desktop-agent-warning"]')
    expect(warning.exists()).toBe(true)
    expect(warning.text()).toContain('还需要启用「桌面操控」子 agent')

    await wrapper.find('[data-test="desktop-agent-fix"]').trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['prompts'])
  })

  it('offers how far the assistant may go to reach a window, defaulting to the old behaviour', async () => {
    // A server older than the setting sends no inputMode; the page must read that as the behaviour that
    // server has — taking the screen when needed — and not as a quiet switch to background-only.
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const select = wrapper.find<HTMLSelectElement>('[data-test="desktop-input-mode"]')
    expect(select.exists()).toBe(true)
    expect(select.element.value).toBe('foreground')
    expect(select.findAll('option').map((option) => option.element.value)).toEqual(['foreground', 'auto', 'background'])

    await select.setValue('auto')
    expect(wrapper.find('[data-test="desktop-input-mode-hint"]').text()).toContain('做不到再占用屏幕')

    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()
    expect(calls.saves[0]?.desktop.inputMode).toBe('auto')
  })
})

describe('prompts view', () => {
  /** One scenario free, so the add form has something to offer. */
  function agents(overrides: Partial<AgentsView> = {}): AgentsView {
    return {
      ...AGENTS_FIXTURE,
      scenarios: [
        { key: 'desktop', name: '桌面操控', when: '需要操作界面时', ownerId: 'desktop', capabilityEnabled: true, available: true },
        { key: 'file-read', name: '读取文件', when: '需要读文件时', ownerId: null, capabilityEnabled: true, available: true },
      ],
      subAgents: AGENTS_FIXTURE.subAgents.filter((agent) => agent.id === 'desktop'),
      ...overrides,
    }
  }

  it('pins the main agent on top and lists the sub-agents under it', async () => {
    const { stub } = createStub()
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="prompts-agent"]')
    expect(wrapper.find('[data-test="prompts-main"]').text()).toContain('主 Agent')
    expect(rows).toHaveLength(2)
    expect(rows[0]?.text()).toContain('桌面操控')
    expect(rows[0]?.find('[data-test="prompts-agent-builtin"]').exists()).toBe(true)
    expect(rows[1]?.text()).toContain('读文件的')

    // The detail pane starts on the main agent, with the default as the placeholder rather than as content.
    const textarea = wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]')
    expect(textarea.element.value).toBe('')
    expect(textarea.attributes('placeholder')).toBe('你是主 agent。')
    expect(wrapper.find('[data-test="prompts-main-modified"]').exists()).toBe(false)
  })

  it('disables the add button when both scenarios have an owner', async () => {
    const { stub } = createStub({ agents: vi.fn(async () => AGENTS_FIXTURE) })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    const add = wrapper.find<HTMLButtonElement>('[data-test="prompts-add"]')
    expect(add.element.disabled).toBe(true)
    expect(add.attributes('title')).toBe('两个场景都已有子 agent')
  })

  it('shows the desktop option disabled, because the built-in agent holds it', async () => {
    const { stub } = createStub({ agents: vi.fn(async () => agents()) })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="prompts-add"]').trigger('click')
    const options = wrapper.findAll<HTMLOptionElement>('[data-test="prompts-add-scenario"] option')

    expect(options[0]?.element.disabled).toBe(true)
    expect(options[0]?.text()).toContain('已被内置的桌面操控占用')
    expect(options[1]?.element.disabled).toBe(false)
  })

  it('sends null when 恢复默认 is pressed, rather than a copy of the default text', async () => {
    // A stored custom prompt, so 「恢复默认」 is a real change: from an untouched page it would make the form
    // identical to the server's copy, and Save — correctly — would have nothing to send.
    const { stub, calls } = createStub({
      agents: vi.fn(async () => ({ ...AGENTS_FIXTURE, main: { ...AGENTS_FIXTURE.main, prompt: '你只听我的。' } })),
    })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]').element.value).toBe('你只听我的。')
    expect(wrapper.find('[data-test="prompts-main-modified"]').exists()).toBe(true)

    await wrapper.find('[data-test="prompts-main-reset"]').trigger('click')
    expect(wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]').element.value).toBe('')
    expect(wrapper.find('[data-test="prompts-main-modified"]').exists()).toBe(false)
    await wrapper.find('[data-test="prompts-save"]').trigger('click')
    await flushPromises()

    expect(calls.agentSaves[0]?.main.prompt).toBeNull()
  })

  it('sends a whitespace-only prompt as null', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]').setValue('   \n  ')
    await wrapper.find('[data-test="prompts-save"]').trigger('click')
    await flushPromises()

    expect(calls.agentSaves[0]?.main.prompt).toBeNull()
  })

  it('sends a new sub-agent with an empty id, so the server names it', async () => {
    const { stub, calls } = createStub({ agents: vi.fn(async () => agents()) })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="prompts-add"]').trigger('click')
    await wrapper.find('[data-test="prompts-add-name"]').setValue('新来的')
    await wrapper.find('[data-test="prompts-add-commit"]').trigger('click')
    await wrapper.find('[data-test="prompts-save"]').trigger('click')
    await flushPromises()

    const added = calls.agentSaves[0]?.subAgents.find((agent) => agent.name === '新来的')
    expect(added?.id).toBe('')
    expect(added?.scenario).toBe('file-read')
  })

  it('does not send an untouched API key', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="prompts-agent"]')[1]?.trigger('click')
    // The stored key is never prefilled; the field says one exists and stays empty.
    const key = wrapper.find<HTMLInputElement>('[data-test="prompts-agent-api-key"]')
    expect(key.element.value).toBe('')
    expect(wrapper.find('[data-test="prompts-agent-api-key-state"]').text()).toBe('已设置')

    await wrapper.find<HTMLTextAreaElement>('[data-test="prompts-agent-textarea"]').setValue('只读。')
    await wrapper.find('[data-test="prompts-save"]').trigger('click')
    await flushPromises()

    const reader = calls.agentSaves[0]?.subAgents.find((agent) => agent.id === 'reader')
    expect(reader?.model).not.toHaveProperty('apiKey')
  })

  it('disables Save when the server measured the prompt over the limit', async () => {
    const over = { ...agents(), limits: { maxRounds: 10, defaultMaxRounds: 5, promptTokens: 10 } }
    const { stub } = createStub({ agents: vi.fn(async () => over) })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="prompts-main-tokens"]').text()).toContain('上限 10')
    expect(wrapper.find('[data-test="prompts-main-tokens"]').classes()).toContain('error')
    expect(wrapper.find<HTMLButtonElement>('[data-test="prompts-save"]').element.disabled).toBe(true)
    expect(wrapper.find('[data-test="prompts-over-limit"]').exists()).toBe(true)
  })

  it('disables Save the moment a draft is typed past the limit', async () => {
    // The server would refuse it, but the page should not offer a button whose only outcome is a 400.
    const tight = { ...agents(), limits: { maxRounds: 10, defaultMaxRounds: 5, promptTokens: 40 } }
    const { stub } = createStub({ agents: vi.fn(async () => tight) })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    // Nothing to save yet, so there is no button to be wrong about.
    expect(wrapper.find('[data-test="prompts-save"]').exists()).toBe(false)

    // 41 CJK characters are 41 tokens, plus the 13-token generated block the fixture's effective prompt carries.
    await wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]').setValue('字'.repeat(41))
    expect(wrapper.find('[data-test="prompts-main-tokens"]').text()).toContain('约 54 token')
    expect(wrapper.find<HTMLButtonElement>('[data-test="prompts-save"]').element.disabled).toBe(true)

    await wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]').setValue('字'.repeat(10))
    expect(wrapper.find<HTMLButtonElement>('[data-test="prompts-save"]').element.disabled).toBe(false)
  })

  it('shows every rule a rejected save broke', async () => {
    const { stub } = createStub({
      saveAgents: vi.fn(async () => {
        throw new ApiError(400, '设置无效', ['「读文件的」的最大轮数必须在 1–10 之间', '子 agent 的 id 重复'])
      }),
    })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="prompts-main-textarea"]').setValue('改一下')
    await wrapper.find('[data-test="prompts-save"]').trigger('click')
    await flushPromises()

    const listed = wrapper.findAll('[data-test="prompts-errors"] li').map((node) => node.text())
    expect(listed).toEqual(['「读文件的」的最大轮数必须在 1–10 之间', '子 agent 的 id 重复'])
  })

  it('says when the selected scenario has no capability behind it, with a way to settings', async () => {
    const { stub } = createStub({
      agents: vi.fn(async () => ({
        ...AGENTS_FIXTURE,
        files: { enabled: false, roots: [] },
        webEnabled: true,
        scenarios: AGENTS_FIXTURE.scenarios.map((scenario) =>
          scenario.key === 'file-read' ? { ...scenario, available: false } : scenario,
        ),
      })),
    })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    // The list says it before the detail does: a disabled capability makes the agent look fine in a list.
    expect(wrapper.findAll('[data-test="prompts-agent-unavailable"]')).toHaveLength(1)

    await wrapper.findAll('[data-test="prompts-agent"]')[1]?.trigger('click')
    expect(wrapper.find('[data-test="prompts-agent-capability"]').text()).toContain('对应能力没有开启')

    await wrapper.find('[data-test="prompts-agent-goto-settings"]').trigger('click')
    expect(wrapper.emitted('navigate')?.[0]).toEqual(['settings'])
  })

  it('shows the server problems as a banner at the top', async () => {
    const { stub } = createStub({
      agents: vi.fn(async () => ({ ...AGENTS_FIXTURE, problems: ['「读文件的」暂不可用：读取文件的能力没有开启'] })),
    })
    const wrapper = mount(PromptsView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="prompts-problems"]').text()).toContain('暂不可用')
  })
})

describe('interface language', () => {
  it('switches from the general settings and re-labels the page at once', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const options = wrapper.findAll('[data-test="settings-language"] button').map((node) => node.text())
    expect(options).toEqual(['简体中文', '繁體中文', 'English'])

    try {
      await wrapper.findAll('[data-test="settings-language"] button')[2]?.trigger('click')
      expect(wrapper.findAll('.settings-nav .nav-item').map((node) => node.text())).toEqual([
        'General',
        'Chat model',
        'Voice input',
        'Voice output',
        'Computer control',
        'File access',
        'Web search',
        'Local engines',
        'Appearance',
      ])

      await wrapper.findAll('[data-test="settings-language"] button')[1]?.trigger('click')
      expect(wrapper.findAll('.settings-nav .nav-item')[0]?.text()).toBe('一般')
    } finally {
      await wrapper.findAll('[data-test="settings-language"] button')[0]?.trigger('click')
    }
    expect(wrapper.findAll('.settings-nav .nav-item')[0]?.text()).toBe('通用')
  })

  it('leaves no Chinese on the pages when the language is English', async () => {
    const { setLocale } = await import('../src/i18n')
    setLocale('en')
    try {
      for (const [name, component] of [
        ['settings', SettingsViewComponent],
        ['mcp', McpView],
        ['skills', SkillsView],
        ['prompts', PromptsView],
        ['memory', MemoryView],
        ['session', SessionView],
      ] as const) {
        const { stub } = createStub()
        const wrapper = mount(component, { props: { api: stub } })
        await flushPromises()
        // Language names, and words the server or a fixture supplies, are not interface text.
        const text = wrapper.text().replace(/简体中文|繁體中文|未启用|桌面操控|读文件的|读取文件/g, '')
        expect(text.match(/[一-鿿]+/g) ?? [], name).toEqual([])
        wrapper.unmount()
      }
    } finally {
      setLocale('zh-CN')
    }
  })
})
