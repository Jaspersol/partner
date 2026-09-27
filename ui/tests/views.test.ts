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
import Composer from '../src/components/Composer.vue'
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
  removals: { seq: number; kind: string }[]
}

function createStub(overrides: Partial<PartnerApiSurface> = {}) {
  const calls: Calls = { edits: [], memoryEdits: [], deletes: [], cancels: [], saves: [], recalls: [], submits: [], removals: [] }

  const session: SessionViewData = {
    lastSeq: 3,
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
    model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096 },
  }

  const stub: PartnerApiSurface = {
    session: vi.fn(async () => session),
    deleteEvent: vi.fn(async (seq: number) => { calls.removals.push({ seq, kind: 'delete' }) }),
    retractFrom: vi.fn(async (seq: number) => { calls.removals.push({ seq, kind: 'retract' }) }),
    editSession: vi.fn(async (seq: number, body: string) => {
      calls.edits.push({ seq, body })
    }),
    sessionRevisions: vi.fn(async (): Promise<SessionRevision[]> => [{ seq: 2, at: '2026-09-26T10:00:01+00:00', body: 'first (edited)' }]),
    queue: vi.fn(async () => queue),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } })),
    devices: vi.fn(async () => ({ inputs: ['synthetic-mic'], outputs: ['synthetic-speaker'] })),
    listModels: vi.fn(async () => ({ baseUrl: 'http://127.0.0.1:9/v1', models: ['qwen3', 'llama3'] })),    voice: vi.fn(async () => ({ input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } })),
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
      return {
        settings: settings as unknown as SettingsView,
        consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } },
        voice: { input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } },
        compaction: null,
      }
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

describe('context meter', () => {
  it('shows what the history costs against the prompt budget', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 3,
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

describe('session removal', () => {
  it('deletes the selected event and clears the selection', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    await wrapper.find('[data-test="session-delete"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([{ seq: 3, kind: 'delete' }])
    expect(wrapper.find('[data-test="session-target"]').text()).toBe('未选中')
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('已删除 #3')
  })

  it('asks twice before retracting, and does nothing until confirmed', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    // The first click only arms it: retraction removes everything after the selection too.
    await wrapper.find('[data-test="session-retract"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([])
    expect(wrapper.find('[data-test="session-retract-confirm"]').exists()).toBe(true)

    await wrapper.find('[data-test="session-retract-confirm"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([{ seq: 3, kind: 'retract' }])
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('已撤回 #3')
  })

  it('can be backed out of', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()
    await wrapper.find('[data-test="session-retract"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-test="session-retract-cancel"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([])
    expect(wrapper.find('[data-test="session-retract"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="session-retract-confirm"]').exists()).toBe(false)
  })

  it('surfaces a refusal instead of pretending it worked', async () => {
    const { stub } = createStub({
      deleteEvent: vi.fn(async () => {
        throw new Error('404: #3 不在当前视图里')
      }),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()
    await wrapper.find('[data-test="session-delete"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="error"]').text()).toContain('404')
  })
})

describe('session scroll position', () => {
  /** happy-dom has no layout, so the geometry a browser would compute is stated explicitly. */
  function geometry(wrapper: ReturnType<typeof mount>, scrollHeight: number, scrollTop: number, clientHeight = 200) {
    const el = wrapper.find('.scroll').element as HTMLElement
    Object.defineProperty(el, 'scrollHeight', { value: scrollHeight, configurable: true })
    Object.defineProperty(el, 'clientHeight', { value: clientHeight, configurable: true })
    el.scrollTop = scrollTop
    return el
  }

  it('follows the reply when the reader is already at the bottom', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, context: usage }))
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

  it('stays put when the reader has scrolled back', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, context: usage }))
    const { stub } = createStub({ session })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const el = geometry(wrapper, 1000, 100)
    await wrapper.find('.scroll').trigger('scroll')

    // Reading something older while the reply arrives must not yank the view.
    Object.defineProperty(el, 'scrollHeight', { value: 1400, configurable: true })
    await (wrapper.vm as unknown as { refresh: () => Promise<void> }).refresh()

    expect(el.scrollTop).toBe(100)
  })

  it('starts following when the reader sends something themselves', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: false, context: usage }))
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
      const session = vi.fn(async () => ({ lastSeq: 0, items: [], busy, context: usage }))
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

describe('queue view', () => {
  it('summarises pending and failed work', async () => {
    const { stub } = createStub()
    const wrapper = mount(QueueView, { props: { api: stub } })
    await flushPromises()

    const summary = wrapper.find('[data-test="queue-summary"]').text()
    expect(summary).toContain('待处理 1')
    expect(summary).toContain('失败 1')
  })

  it('lists only what is still waiting to go in', async () => {
    const { stub } = createStub()
    const wrapper = mount(QueueView, { props: { api: stub } })
    await flushPromises()

    // The tab is the queue, not its history: the finished item is counted, never listed.
    const rows = wrapper.findAll('[data-test=queue-list] li')
    expect(rows).toHaveLength(1)
    expect(rows[0]?.text()).toContain('pending work')
    expect(wrapper.text()).not.toContain('disk unwritable')

    const summary = wrapper.find('[data-test=queue-summary]').text()
    expect(summary).toContain('待处理 1')
    expect(summary).toContain('失败 1')
  })

  it('says so when nothing is queued, even with failures on the books', async () => {
    const { stub } = createStub({
      queue: vi.fn(async () => ({
        headSeq: 2,
        runningItemId: null,
        depth: 0,
        items: [
          { itemId: 'q2', seq: 2, source: 'Text', status: 'Failed', enqueuedAt: '2026-09-26T10:00:00+00:00', startedAt: null, finishedAt: null, attempt: 2, error: 'disk unwritable', payload: 'backup' },
        ],
      })),
    })
    const wrapper = mount(QueueView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.findAll('[data-test=queue-list] li')).toHaveLength(0)
    expect(wrapper.find('[data-test=queue-empty]').text()).toContain('当前没有待输入的内容')
    expect(wrapper.find('[data-test=queue-summary]').text()).toContain('失败 1')
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
        model: { baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096 },
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
        model: { baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096 },
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

  it('reports each voice half, so an enabled-but-dead one is visible', async () => {
    const { stub } = createStub({
      voice: vi.fn(async () => ({
        input: { running: true, reason: '运行中', device: 'synthetic-mic', count: 3 },
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
        input: { enabled: false, deviceId: '拔掉的麦克风', silenceThreshold: 0.03, utteranceGapMs: 1200, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40 },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
        model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096 },
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

  it('shows the context and output limits, and reports what a shrink did', async () => {
    const { stub, calls } = createStub({
      saveSettings: vi.fn(async (patch: RuntimeSettings) => {
        calls.saves.push(patch)
        return {
          settings: {
            input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40 },
            output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
            asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
            tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
            model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: patch.model.contextTokens, maxTokens: patch.model.maxTokens },
          },
          consumer: { running: true, reason: '运行中（设置）', model: 'm', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } },
          voice: { input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } },
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
