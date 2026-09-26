import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import type {
  MemoryItem,
  MemoryRevision,
  PartnerApiSurface,
  QueueView as QueueViewData,
  RecallHit,
  RebuildReport,
  SessionRevision,
  SessionView as SessionViewData,
  SettingsView,
  RuntimeSettings,
} from '../src/api'
import MemoryView from '../src/views/MemoryView.vue'
import QueueView from '../src/views/QueueView.vue'
import SessionView from '../src/views/SessionView.vue'
import SettingsViewComponent from '../src/views/SettingsView.vue'

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
}

function createStub(overrides: Partial<PartnerApiSurface> = {}) {
  const calls: Calls = { edits: [], memoryEdits: [], deletes: [], cancels: [], saves: [], recalls: [], submits: [] }

  const session: SessionViewData = {
    lastSeq: 3,
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
    { recordId: 'm1', tier: 'Semantic', scope: 'Global', body: 'likes jasmine tea', createdAt: '2026-09-26T10:00:00+00:00', validTo: null, supersededBy: null, editedBy: null, importance: 0.9 },
  ]

  const settings: SettingsView = {
    input: {
      enabled: false,
      deviceId: null,
      silenceThreshold: 0.03,
      utteranceGapMs: 1200,
      sentenceGapMs: 450,
      windowSeconds: 8,
      asrStream: false,
      maxSplitChars: 40,
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
    asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
    tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
    model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120 },
  }

  const stub: PartnerApiSurface = {
    session: vi.fn(async () => session),
    editSession: vi.fn(async (seq: number, body: string) => {
      calls.edits.push({ seq, body })
    }),
    sessionRevisions: vi.fn(async (): Promise<SessionRevision[]> => [{ seq: 2, at: '2026-09-26T10:00:01+00:00', body: 'first (edited)' }]),
    queue: vi.fn(async () => queue),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } })),
    submitInput: vi.fn(async (payload: string, source: string) => {
      calls.submits.push({ payload, source })
      return { itemId: 'in-test', seq: 9, status: 'Queued', deduplicated: false }
    }),
    cancel: vi.fn(async (itemId: string) => {
      calls.cancels.push(itemId)
      return itemId !== 'finished'
    }),
    memory: vi.fn(async () => memory),
    recall: vi.fn(async (query: string): Promise<RecallHit[]> => {
      calls.recalls.push(query)
      return [{ recordId: 'm1', body: 'likes jasmine tea', total: 0.75, reasons: ['lexical', 'recency'] }]
    }),
    editMemory: vi.fn(async (recordId: string, body: string) => {
      calls.memoryEdits.push({ recordId, body })
    }),
    deleteMemory: vi.fn(async (recordId: string) => {
      calls.deletes.push(recordId)
    }),
    memoryRevisions: vi.fn(async (): Promise<MemoryRevision[]> => [{ at: '2026-09-26T10:00:09+00:00', before: 'likes tea', after: 'likes jasmine tea', actor: 'Human' }]),
    settings: vi.fn(async () => settings),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => {
      calls.saves.push(patch)
      return { settings: settings as unknown as SettingsView, consumer: { running: false, reason: "未配置模型", model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } } }
    }),
    rebuild: vi.fn(async (): Promise<RebuildReport> => ({ sessionItems: 3, sessionLastSeq: 3, memoryIndexed: 1, queueLogEntries: 2 })),
    ...overrides,
  }

  return { stub, calls }
}

describe('session view', () => {
  it('renders the log and shows the original body of an edited event', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    expect(rows).toHaveLength(2)
    expect(rows[0]?.text()).toContain('first')
    expect(rows[0]?.text()).toContain('已编辑 1 次 · 原文：first')
  })

  it('fills the editor from the selection and appends the edit on save', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    const draft = wrapper.find<HTMLTextAreaElement>('[data-test="session-draft"]')
    expect(draft.element.value).toBe('second')

    await draft.setValue('second, revised')
    await wrapper.find('[data-test="session-save"]').trigger('click')
    await flushPromises()

    expect(calls.edits).toEqual([{ seq: 3, body: 'second, revised' }])
    // The view re-reads after a write instead of trusting local state.
    expect(stub.session).toHaveBeenCalledTimes(2)
  })

  it('refuses an empty body without calling the API', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()
    await wrapper.find<HTMLTextAreaElement>('[data-test="session-draft"]').setValue('   ')
    await wrapper.find('[data-test="session-save"]').trigger('click')
    await flushPromises()

    expect(calls.edits).toEqual([])
    expect(wrapper.find('[data-test="error"]').text()).toContain('正文不能为空')
  })
})

describe('queue view', () => {
  it('summarises pending and failed work', async () => {
    const { stub } = createStub()
    const wrapper = mount(QueueView, { props: { api: stub } })
    await flushPromises()

    const summary = wrapper.find('[data-test="queue-summary"]').text()
    expect(summary).toContain('待处理 1')
    expect(summary).toContain('失败 1')
  })

  it('cancels an item and reports an item that is already finished', async () => {
    const { stub, calls } = createStub({ cancel: vi.fn(async () => false) })
    const wrapper = mount(QueueView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="queue-cancel"]')[0]?.trigger('click')
    await flushPromises()

    expect(calls.cancels).toHaveLength(0)
    expect(wrapper.find('[data-test="error"]').text()).toContain('无法取消')
  })
})

describe('memory view', () => {
  it('marks validity and switches the list to recall hits', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.find('[data-test="memory-list"] li').text()).toContain('有效')

    await wrapper.find<HTMLInputElement>('[data-test="memory-query"]').setValue('jasmine')
    await wrapper.find('[data-test="memory-recall"]').trigger('click')
    await flushPromises()

    expect(calls.recalls).toEqual(['jasmine'])
    expect(wrapper.find('[data-test="memory-summary"]').text()).toContain('1 条命中')
    expect(wrapper.find('[data-test="memory-list"] li').text()).toContain('lexical')
  })

  it('edits and deletes the selected record', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()
    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-draft"]').setValue('likes jasmine tea, no sugar')
    await wrapper.find('[data-test="memory-save"]').trigger('click')
    await flushPromises()

    expect(calls.memoryEdits).toEqual([{ recordId: 'm1', body: 'likes jasmine tea, no sugar' }])

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()
    await wrapper.find('[data-test="memory-delete"]').trigger('click')
    await flushPromises()

    expect(calls.deletes).toEqual(['m1'])
    expect(wrapper.find('[data-test="memory-target"]').text()).toBe('未选中')
  })
})

describe('settings view', () => {
  it('renders a card per configurable subsystem', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const headings = wrapper.findAll('section.card h2').map((node) => node.text())
    expect(headings).toEqual(['语音输入', '语音输出', '语音识别（ASR）', '语音合成（TTS）', '模型（对话）'])

    expect(wrapper.find('[data-test="asr-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="tts-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-api-key"]').exists()).toBe(true)
  })

  it('never fills the key field from the server and says whether one is stored', async () => {
    const { stub } = createStub({
      settings: vi.fn(async () => ({
        input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40 },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
        model: { baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120 },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // The key is write-only: the server has one, the field must not.
    expect(wrapper.find<HTMLInputElement>('[data-test="model-api-key"]').element.value).toBe('')
    expect(wrapper.find('[data-test="model-api-key-state"]').text()).toBe('已设置')
    expect(wrapper.find('[data-test="model-api-key-hint"]').text()).toContain('留空保持不变')
  })

  it('does not send an untouched key, and sends an explicit clear when asked', async () => {
    const { stub, calls } = createStub({
      settings: vi.fn(async () => ({
        input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40 },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
        model: { baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120 },
      })),
    })

    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

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
    expect(wrapper.find('[data-test="model-effect"]').text()).toContain('未配置模型')
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
    // ui.md §5.6: the status carries a timestamp, so the exact wording is no longer a fixed string.
    const status = wrapper.find('[data-test="settings-status"]').text()
    expect(status).toContain('已保存')
    expect(status).toMatch(/\d{2}:\d{2}:\d{2}/)
  })
})
