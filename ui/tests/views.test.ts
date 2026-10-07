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
import SessionView from '../src/views/SessionView.vue'
import Composer from '../src/components/Composer.vue'
import SettingsViewComponent from '../src/views/SettingsView.vue'
import McpView from '../src/views/McpView.vue'
import SkillsView from '../src/views/SkillsView.vue'

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
  tierChanges: { recordId: string; tier: string }[]
  scopeChanges: { recordId: string; scope: string }[]
  creates: { body: string; tier?: string; scope?: string }[]
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
  model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
  desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
  mcp: { enabled: false, servers: [], allowedTools: [] },
  skills: { disabled: [] },
};

function createStub(overrides: Partial<PartnerApiSurface> = {}) {
  const calls: Calls = { edits: [], memoryEdits: [], deletes: [], cancels: [], saves: [], recalls: [], submits: [], removals: [], tierChanges: [], scopeChanges: [], creates: [] }

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
    { recordId: 'm1', tier: 'Semantic', scope: 'Recall', body: 'likes jasmine tea', createdAt: '2026-09-26T10:00:00+00:00', validTo: null, supersededBy: null, editedBy: null, importance: 0.9 },
  ]

/**
 * The settings a stubbed API reports. Module level because a test that changes one segment (a skills deny
 * list, say) still has to build its override from the whole shape.
 */


  const stub: PartnerApiSurface = {
    session: vi.fn(async () => session),
    deleteEvent: vi.fn(async (seq: number) => { calls.removals.push({ seq, kind: 'delete' }) }),
    retractFrom: vi.fn(async (seq: number) => { calls.removals.push({ seq, kind: 'retract' }) }),
    editSession: vi.fn(async (seq: number, body: string) => {
      calls.edits.push({ seq, body })
    }),
    sessionRevisions: vi.fn(async (): Promise<SessionRevision[]> => [{ seq: 2, at: '2026-09-26T10:00:01+00:00', body: 'first (edited)' }]),
    queue: vi.fn(async () => queue),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], })),
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
    memoryAll: vi.fn(async () => memory),
    skills: vi.fn(async () => ({ skills: [], warnings: [] })),
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
      return [{ recordId: 'm1', body: 'likes jasmine tea', total: 0.75, reasons: ['lexical', 'recency'] }]
    }),
    editMemory: vi.fn(async (recordId: string, body: string) => {
      calls.memoryEdits.push({ recordId, body })
    }),
    deleteMemory: vi.fn(async (recordId: string) => {
      calls.deletes.push(recordId)
    }),
    memoryRevisions: vi.fn(async (): Promise<MemoryRevision[]> => [{ at: '2026-09-26T10:00:09+00:00', before: 'likes tea', after: 'likes jasmine tea', actor: 'Human' }]),
    settings: vi.fn(async () => SETTINGS_FIXTURE),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => {
      calls.saves.push(patch)
      return {
        settings: SETTINGS_FIXTURE as unknown as SettingsView,
        consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
        voice: { input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } },
        compaction: null,
      }
    }),
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

describe('a view that has not read yet', () => {
  // The defect: every list starts with empty data, and the empty state was the same row as the pending state.
  // So the first thing a reader saw was a confident wrong answer — 「（无匹配事件）」 on a session that had not
  // been asked about yet — said before the request that would have contradicted it even went out.

  it('says it is still reading, rather than that there is nothing', async () => {
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

    // Before the answer arrives.
    expect(wrapper.find('[data-test="session-pending"]').exists()).toBe(true)
    expect(wrapper.find('.empty').exists()).toBe(false)

    release()
    await flushPromises()

    // After it, the same empty list is a real answer and says so.
    expect(wrapper.find('[data-test="session-pending"]').exists()).toBe(false)
    expect(wrapper.find('.empty').text()).toContain('无匹配事件')
  })

  it('does not fall back to "still reading" when a poll fails after a good one', async () => {
    // A failed refresh must leave the last good list on screen with an error beside it. Replacing a
    // conversation with a spinner would be a worse answer than the stale one.
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
    expect(wrapper.find('[data-test="session-pending"]').exists()).toBe(false)

    await wrapper.vm.$.exposed?.refresh?.()
    await flushPromises()

    expect(wrapper.find('[data-test=error]').text()).toContain('boom')
    expect(wrapper.find('[data-test="session-pending"]').exists()).toBe(false)
    expect(wrapper.findAll('[data-test="session-list"] li').length).toBe(1)
  })
})

describe('session removal', () => {
  it('deletes the selected event and clears the selection', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    // Physical and irreversible, so the panel arms first; a single click must not reach the server.
    await wrapper.find('[data-test="session-delete"]').trigger('click')
    await flushPromises()
    expect(calls.removals).toEqual([])

    await wrapper.find('[data-test="session-delete-confirm"]').trigger('click')
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

  it('deletes from a row without selecting it first', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    const rows = wrapper.findAll('[data-test="session-list"] li')
    await rows[1]?.find('[data-test="session-row-menu"]').trigger('click')
    await rows[1]?.find('[data-test="session-row-delete"]').trigger('click')
    await rows[1]?.find('[data-test="session-row-delete"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([{ seq: 3, kind: 'delete' }])
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('已删除 #3')
  })

  it('puts the outcome line where it can be seen, not below the composer', async () => {
    // It used to come after the composer, at the very bottom of the window: in a session a few hundred events
    // long, the one line saying what just happened was the first thing to scroll out of sight. The toolbar does
    // not scroll, so anything directly under it stays on screen for as long as the view is open.
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()
    await wrapper.find('[data-test="session-delete"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-test="session-delete-confirm"]').trigger('click')
    await flushPromises()

    const order = [...wrapper.element.querySelectorAll('[data-test]')].map((node) =>
      node.getAttribute('data-test'),
    )
    expect(order.indexOf('session-notice')).toBeGreaterThan(-1)
    expect(order.indexOf('session-notice')).toBeLessThan(order.indexOf('session-list'))
    expect(order.indexOf('session-notice')).toBeLessThan(order.indexOf('composer'))
  })

  it('puts the reader\'s own removed message back in the box', async () => {
    const { stub } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    // A retraction is physical: the text leaves the log for good, and the only copy left is the one the
    // composer is holding. seq 1 is the reader's own message; the assistant's reply must not be resendable.
    const rows = wrapper.findAll('[data-test="session-list"] li')
    // The row's actions sit behind one affordance (S4), so the menu is opened the way a reader opens it.
    await rows[0]?.find('[data-test="session-row-menu"]').trigger('click')
    await rows[0]?.find('[data-test="session-restore"]').trigger('click')
    await flushPromises()

    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('first')
    // Closing the menu is what proves the assistant's reply is not resendable: the row is no longer open.
    expect(rows[1]?.find('[data-test="session-restore"]').exists()).toBe(false)
  })

  it('hides the tool cards in results-only mode and shows them again', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 4,
        busy: false,
        context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 },
        items: [
          { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'ask', originalBody: null, revisionCount: 0 },
          { seq: 2, at: '2026-09-26T10:00:01+00:00', kind: 'ToolCall', source: 'Text', body: 'bash', originalBody: null, revisionCount: 0, meta: { id: 'c1', name: 'bash', args: '{}' } },
          { seq: 4, at: '2026-09-26T10:00:03+00:00', kind: 'Assistant', source: 'Text', body: 'done', originalBody: null, revisionCount: 0 },
        ],
      })),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    expect(wrapper.findAll('[data-tool]')).toHaveLength(1)
    await wrapper.find('[data-test="session-results-only"]').trigger('click')
    expect(wrapper.findAll('[data-tool]')).toHaveLength(0)
    expect(wrapper.text()).toContain('ask')
    expect(wrapper.text()).toContain('done')

    await wrapper.find('[data-test="session-results-only"]').trigger('click')
    expect(wrapper.findAll('[data-tool]')).toHaveLength(1)
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
    await wrapper.find('[data-test="session-delete-confirm"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="error"]').text()).toContain('404')
  })

  it('lets an armed delete be backed out of', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    await wrapper.findAll('[data-test="session-list"] li')[1]?.trigger('click')
    await flushPromises()

    await wrapper.find('[data-test="session-delete"]').trigger('click')
    await wrapper.find('[data-test="session-delete-cancel"]').trigger('click')
    await flushPromises()

    expect(calls.removals).toEqual([])
    expect(wrapper.find('[data-test="session-delete"]').exists()).toBe(true)
  })

  it('shows a thinking row folded once the answer has landed', async () => {
    const { stub } = createStub({
      session: vi.fn(async () => ({
        lastSeq: 5,
        busy: false,
        context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 },
        items: [
          { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: '几点了', originalBody: null, revisionCount: 0 },
          { seq: 2, at: '2026-09-26T10:00:04+00:00', kind: 'Thinking', source: 'Text', body: '先确认时区', originalBody: null, revisionCount: 0 },
          { seq: 5, at: '2026-09-26T10:00:05+00:00', kind: 'Assistant', source: 'Text', body: '下午三点', originalBody: null, revisionCount: 0 },
        ],
      })),
    })
    const wrapper = mount(SessionView, { props: { api: stub } })
    await flushPromises()

    // Folded: the reasoning is there to be consulted, not to be re-read on every scrollback. The answer is
    // what the reader came for, and the reasoning sits next to it.
    const row = wrapper.find('[data-test="session-thinking"]')
    expect(row.exists()).toBe(true)
    expect(row.attributes('open')).toBeUndefined()
    expect(wrapper.text()).toContain('下午三点')
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
      session: vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, context: usage })),
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
      const session = vi.fn(async () => ({ lastSeq: 9, items: [], busy: false, context: usage }))
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

  it('does not mistake its own pin for the reader scrolling away', async () => {
    const usage = { usedTokens: 0, budgetTokens: 1, contextTokens: 2, maxTokens: 1, keptItems: 0, foldedItems: 0, fraction: 0 }
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, context: usage }))
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
    const session = vi.fn(async () => ({ lastSeq: 1, items: [], busy: true, context: usage }))
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

  it('lists retired memories and says what replaced them', async () => {
    // "Fully visible" means the retired record is still there: the useful answer to a conflict is "what changed
    // it", and that answer lives on the row that was replaced.
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

    const rows = wrapper.findAll('[data-test="memory-list"] li')
    expect(rows).toHaveLength(2)
    expect(rows[0]?.text()).toContain('已失效 → m2')
    expect(rows[1]?.text()).toContain('有效')

    // The tier is named, not shown as an enum value: "暂记" and "确定" answer questions a reader has.
    expect(rows[0]?.find('[data-test="memory-row-tier"]').text()).toContain('暂记')
    expect(rows[1]?.find('[data-test="memory-row-tier"]').text()).toContain('确定')
  })

  it('lets a person move a memory between tiers and re-reads afterwards', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()

    await wrapper.find<HTMLSelectElement>('[data-test="memory-set-tier"]').setValue('Semantic')
    await flushPromises()

    expect(calls.tierChanges).toEqual([{ recordId: 'm1', tier: 'Semantic' }])
    // The norm (ui.md): what is on screen is what the server kept, not what was sent.
    expect(stub.memoryAll).toHaveBeenCalledTimes(2)
  })

  it('lists all four tiers with a count, including the ones with nothing in them', async () => {
    const { stub } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    const options = wrapper.findAll('[data-test="memory-tier"] option').map((option) => option.text())
    expect(options).toHaveLength(5) // 全部 + 四层

    // The list used to build this from the tiers the loaded rows happened to have, so an empty tier could not be
    // selected and the rest showed a bare enum name. "This tier is empty" and "this tier does not exist" are
    // different facts.
    expect(options[1]).toContain('暂记')
    expect(options[2]).toContain('经过')
    expect(options[3]).toContain('确定')
    expect(options[4]).toContain('做法')
    expect(options[2]).toContain('0')
    expect(options[3]).toContain('1')
  })

  it('marks a retained row and says what retention does not do', async () => {
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

    expect(wrapper.find('[data-test="memory-list"] li').find('[data-test="memory-row-retain"]').text()).toBe('保留')

    // Retention is about time, not about being right. A person who corrects themselves means it.
    await wrapper.find('[data-test="memory-list"] li').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test="memory-retain-hint"]').text()).toContain('不挡纠正')
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

  it('keeps the tier picker usable while a record is retained', async () => {
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

    // Trust and time are separate axes, so a retained Working record is legal and there is nothing to lock.
    // Silently dropping the retention on a tier change would be a state change nobody asked for.
    expect(wrapper.find<HTMLSelectElement>('[data-test="memory-set-tier"]').element.disabled).toBe(false)
    expect(wrapper.find('[data-test="memory-set-tier"]').findAll('option')).toHaveLength(4)
  })

  it('filters by retention', async () => {
    const { stub } = createStub({
      memoryAll: vi.fn(async () => [
        { recordId: 'm1', tier: 'Semantic', scope: 'Retain', body: '保留的那条', createdAt: '2026-09-26T10:00:00+00:00', lastRecalledAt: null, validTo: null, supersededBy: null, editedBy: null, importance: 0.7 },
        { recordId: 'm2', tier: 'Semantic', scope: 'Recall', body: '普通的那条', createdAt: '2026-09-26T10:00:00+00:00', lastRecalledAt: null, validTo: null, supersededBy: null, editedBy: null, importance: 0.7 },
      ]),
    })
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find<HTMLSelectElement>('[data-test="memory-scope"]').setValue('Retain')
    await flushPromises()

    const rows = wrapper.findAll('[data-test="memory-list"] li')
    expect(rows).toHaveLength(1)
    expect(rows[0]?.text()).toContain('保留的那条')
  })

  it('writes a memory the distiller would not have kept, and selects it afterwards', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-new"]').trigger('click')
    await flushPromises()

    // The default is an established fact: something a person typed is not a note waiting to be corroborated.
    expect(wrapper.find<HTMLSelectElement>('[data-test="memory-new-tier"]').element.value).toBe('Semantic')
    expect(wrapper.find<HTMLSelectElement>('[data-test="memory-new-scope"]').element.value).toBe('Recall')

    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-new-body"]').setValue('每周五之前提交周报')
    await wrapper.find('[data-test="memory-new-submit"]').trigger('click')
    await flushPromises()

    expect(calls.creates).toEqual([{ body: '每周五之前提交周报', tier: 'Semantic', scope: 'Recall' }])
    expect(stub.memoryAll).toHaveBeenCalledTimes(2)
  })

  it('refuses a hand-written memory that carries nothing worth keeping', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(MemoryView, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="memory-new"]').trigger('click')
    await flushPromises()
    await wrapper.find<HTMLTextAreaElement>('[data-test="memory-new-body"]').setValue('  ')
    await wrapper.find('[data-test="memory-new-submit"]').trigger('click')
    await flushPromises()

    expect(calls.creates).toEqual([])
    expect(wrapper.find('[data-test="error"]').text()).toContain('4')
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
    expect(wrapper.find('[data-test="error"]').text()).toContain('名字和命令')
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
  it('renders a card per configurable subsystem', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const headings = wrapper.findAll('section.card h2').map((node) => node.text())
    expect(headings).toEqual(['语音输入', '语音输出', '语音识别（ASR）', '语音合成（TTS）', '桌面（M9）', '模型（对话）'])

    expect(wrapper.find('[data-test="asr-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="tts-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-base-url"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="desktop-enabled"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="model-api-key"]').exists()).toBe(true)
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

  it('edits the gear menu itself: add, rename, reorder, remove, and set the default', async () => {
    // The menu is stored state now, not a constant in the file, so the editor has to round-trip the order
    // it was given and put the default on a gear that is actually in the list.
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const rows = () => wrapper.findAll('[data-test="model-gear"]')
    expect(rows().map((node) => (node.element as HTMLInputElement).value))
      .toEqual(['off', 'low', 'medium', 'high'])

    // Rename is committed on change and lower-cased: casing is not a different gear.
    await rows()[1].setValue('  MINIMAL ')
    await rows()[1].trigger('change')
    expect(rows().map((node) => (node.element as HTMLInputElement).value))
      .toEqual(['off', 'minimal', 'medium', 'high'])

    // The default moves to a gear in the list rather than being typed blind.
    await wrapper.findAll('[data-test="model-gear-set-default"]')[3].trigger('click')
    expect(wrapper.find<HTMLInputElement>('[data-test="model-reasoning-effort"]').element.value).toBe('high')

    // Moving the first row down swaps it with its neighbour; the rest keep their order.
    await wrapper.findAll('[data-test="model-gear-down"]')[0].trigger('click')
    expect(rows().map((node) => (node.element as HTMLInputElement).value))
      .toEqual(['minimal', 'off', 'medium', 'high'])

    await wrapper.find('[data-test="model-gear-add"]').trigger('click')
    expect(rows()).toHaveLength(5)

    // The new row is empty; typing into it is what makes it a gear.
    await rows()[4].setValue('turbo')
    await rows()[4].trigger('change')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.model.reasoningGears).toEqual(['minimal', 'off', 'medium', 'high', 'turbo'])
    expect(calls.saves[0]?.model.reasoningEffort).toBe('high')
  })

  it('drops a duplicate rather than storing a menu entry that can never be chosen', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    const rows = () => wrapper.findAll('[data-test="model-gear"]')

    // Renaming the third row to "off" makes it a repeat. The row the user just typed wins and the earlier
    // copy goes, so the caret stays in a row that still holds what they entered.
    await rows()[2].setValue('off')
    await rows()[2].trigger('change')
    expect(rows().map((node) => (node.element as HTMLInputElement).value)).toEqual(['low', 'off', 'high'])

    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    expect(calls.saves[0]?.model.reasoningGears).toEqual(['low', 'off', 'high'])
  })

  it('drops a row that was left blank instead of storing a gap in the menu', async () => {
    const { stub, calls } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="model-gear-add"]').trigger('click')
    await wrapper.find('[data-test="settings-save"]').trigger('click')
    await flushPromises()

    // The added row is empty and never reaches the server: a blank entry would be a menu item that reads
    // as a level that thinks very little, or as a silent no-op.
    expect(calls.saves[0]?.model.reasoningGears).toEqual(['off', 'low', 'medium', 'high'])
  })

  it('marks which gear is the default instead of leaving it to be inferred', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // The stored default is "medium"; the row next to it says so, so the two cannot silently disagree.
    const marks = wrapper.findAll('[data-test="model-gear-default"]')
    expect(marks).toHaveLength(1)
    expect(wrapper.findAll('[data-test="model-gear"]')[2].element.parentElement?.textContent).toContain('默认')
  })

  it('never fills the key field from the server and says whether one is stored', async () => {
    const { stub } = createStub({
      settings: vi.fn(async () => ({
        input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40 },
        output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
        asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
        tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
        model: { baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
      desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
      mcp: { enabled: false, servers: [], allowedTools: [] },
      skills: { disabled: [] },
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
        model: { baseUrl: 'http://127.0.0.1:9/v1', model: 'm', apiKeySet: true, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
      desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
      mcp: { enabled: false, servers: [], allowedTools: [] },
      skills: { disabled: [] },
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
        model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
        desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
        mcp: { enabled: false, servers: [], allowedTools: [] },
        skills: { disabled: [] },
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
            model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: patch.model.contextTokens, maxTokens: patch.model.maxTokens, supportsImages: patch.model.supportsImages, reasoningEffort: patch.model.reasoningEffort, reasoningGears: patch.model.reasoningGears },
            desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
            mcp: { enabled: false, servers: [], allowedTools: [] },
            skills: { disabled: [] },
          },
          consumer: { running: true, reason: '运行中（设置）', model: 'm', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
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

  it('waits for the button for every field, including the switches', async () => {
    // Four toggles saved themselves the moment they were flipped while the rest waited for the button at the
    // bottom. Nothing on the page said which was which, so "why did this one apply and that one not" had no
    // answer available to the reader. Now the page saves or it does not.
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

  it('folds groups away, and remembers which ones between visits', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    // Open on arrival: folding by default would hide five of six groups from someone who came to change one
    // thing, and only the reader knows which thing.
    const asr = () => wrapper.find('[data-test="settings-group-asr"]')
    expect(asr().attributes('aria-expanded')).toBe('true')

    await asr().trigger('click')
    expect(asr().attributes('aria-expanded')).toBe('false')
    // The inline style rather than `isVisible()`: happy-dom does not fold 's \n    // into its visibility calculation, so `isVisible()` would report a folded group as visible.
    expect(wrapper.find('#settings-body-asr').attributes('style')).toContain('display: none')

    // Remembered, because the views are `v-if` — a control that forgets itself on every tab switch is worse
    // than no control.
    const second = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()
    expect(second.find('[data-test="settings-group-asr"]').attributes('aria-expanded')).toBe('false')
    second.unmount()

    await wrapper.find('[data-test="settings-group-asr"]').trigger('click')
    expect(wrapper.find('#settings-body-asr').attributes('style') ?? '').not.toContain('display: none')
  })

  it('folds and unfolds everything at once', async () => {
    const { stub } = createStub()
    const wrapper = mount(SettingsViewComponent, { props: { api: stub } })
    await flushPromises()

    await wrapper.find('[data-test="settings-fold-all"]').trigger('click')
    const expanded = () => wrapper.findAll('[aria-expanded="true"]').length
    expect(expanded()).toBe(0)

    // The label says what the button will do, not what just happened.
    expect(wrapper.find('[data-test="settings-fold-all"]').text()).toContain('展开全部')
    await wrapper.find('[data-test="settings-fold-all"]').trigger('click')
    expect(expanded()).toBe(6)
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
})
