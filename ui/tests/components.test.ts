import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { ConsumerState, PartnerApiSurface, QueueSummary, RuntimeSettings } from '../src/api'
import ApprovalBanner from '../src/components/ApprovalBanner.vue'
import Composer from '../src/components/Composer.vue'
import Inspector from '../src/components/Inspector.vue'
import MarkdownBody from '../src/components/MarkdownBody.vue'
import SideBar from '../src/components/SideBar.vue'
import StatusBar from '../src/components/StatusBar.vue'
import TopBar from '../src/components/TopBar.vue'
import EventList from '../src/components/EventList.vue'
import SessionView from '../src/views/SessionView.vue'
import { summarizeQueue } from '../src/api'
import type { QueueView } from '../src/api'

const baseItem = {
  seq: 1,
  at: '2026-09-26T10:00:00+00:00',
  kind: 'User',
  source: 'Text',
  body: 'hi',
  originalBody: null,
  revisionCount: 0,
}

describe('approval banner', () => {
  const pending = {
    approvalId: 'ap-1-120000',
    toolName: 'desktop_act',
    arguments: '{"entityId":"104d4","kind":"KeyPress","virtualKey":67}',
    requestedAt: '2026-09-27T11:52:01+00:00',
    expiresAt: '2026-09-27T11:54:01+00:00',
  }

  const desktop = {
    enabled: true,
    leaseTtlMs: 15000,
    approval: 'all',
    allowedWindowTitles: [],
    lastObsId: 'obs-1',
    entityCount: 1,
    entities: [{ entityId: '104d4', title: '网易云音乐', process: 'cloudmusic', foreground: false }],
  }

  it('names the window an approval is about, not just its id', async () => {
    // The model once pressed a key at a window that was not the one it meant; the prompt is where a human can
    // see that, so it has to say which window rather than an opaque handle.
    const wrapper = mount(ApprovalBanner, { props: { pending: [pending], desktop } })

    expect(wrapper.find('[data-test="approval-tool"]').text()).toBe('desktop_act')
    expect(wrapper.find('[data-test="approval-target"]').text()).toContain('网易云音乐')
    expect(wrapper.text()).toContain('KeyPress')
    expect(wrapper.find('[data-test="approval-hint"]').text()).toContain('无人应答即拒绝')
  })

  it('falls back to the id when the window is no longer in the observation', async () => {
    const wrapper = mount(ApprovalBanner, {
      props: { pending: [pending], desktop: { ...desktop, entities: [] } },
    })

    expect(wrapper.find('[data-test="approval-target"]').text()).toContain('#104d4')
  })

  it('answers with the decision the human made', async () => {
    const wrapper = mount(ApprovalBanner, { props: { pending: [pending], desktop } })

    await wrapper.find('[data-test="approval-deny"]').trigger('click')
    expect(wrapper.emitted('answer')?.[0]).toEqual(['ap-1-120000', false])

    await wrapper.find('[data-test="approval-allow"]').trigger('click')
    expect(wrapper.emitted('answer')?.[1]).toEqual(['ap-1-120000', true])
  })

  it('renders nothing when no tool is waiting', () => {
    const wrapper = mount(ApprovalBanner, { props: { pending: [], desktop: null } })

    expect(wrapper.find('[data-test="approval-banner"]').exists()).toBe(false)
  })
})

describe('event list', () => {
  it('pairs a tool call with its result into one card', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 7, kind: 'ToolCall', body: 'clock', meta: { id: 'c1', name: 'clock', args: '{city:上海}' } },
          { ...baseItem, seq: 8, kind: 'ToolResult', body: '12:00', meta: { id: 'c1', name: 'clock', ok: 'true' } },
        ],
      },
    })

    // Two events, one thing to a reader.
    const cards = wrapper.findAll('[data-tool]')
    expect(cards).toHaveLength(1)
    expect(cards[0]?.attributes('data-tool')).toBe('clock')
    expect(cards[0]?.attributes('data-status')).toBe('ok')

    // Folded by default, with a summary that says what happened.
    expect(cards[0]?.attributes('open')).toBeUndefined()
    const summary = cards[0]!.find('summary').text()
    expect(summary).toContain('clock')
    expect(summary).toContain('上海')
    expect(summary).toContain('完成')

    // Opening it carries the arguments and the result.
    expect(cards[0]!.text()).toContain('参数')
    expect(cards[0]!.text()).toContain('结果')
  })

  it('marks a failed call without opening it', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 7, kind: 'ToolCall', body: 'desktop_act', meta: { id: 'c1', name: 'desktop_act', args: '{}' } },
          { ...baseItem, seq: 8, kind: 'ToolResult', body: 'approval denied', meta: { id: 'c1', name: 'desktop_act', ok: 'false' } },
        ],
      },
    })

    const card = wrapper.find('[data-tool=desktop_act]')
    expect(card.attributes('data-status')).toBe('failed')
    expect(card.find('summary').text()).toContain('失败')
    expect(card.text()).toContain('approval denied')
  })

  it('shows a call that has not come back yet as in flight', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 7, kind: 'ToolCall', body: 'desktop_observe', meta: { id: 'c1', name: 'desktop_observe', args: '{}' } },
        ],
      },
    })

    const card = wrapper.find('[data-tool=desktop_observe]')
    expect(card.attributes('data-status')).toBe('running')
    expect(card.find('summary').text()).toContain('进行中')
  })

  it('renders a result whose call is missing rather than dropping it', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 9, kind: 'ToolResult', body: 'orphan', meta: { id: 'gone', name: 'clock', ok: 'true' } },
        ],
      },
    })

    expect(wrapper.findAll('[data-tool]')).toHaveLength(1)
    expect(wrapper.text()).toContain('orphan')
  })

  it('marks the reply that is still being written and keeps it out of the inspector', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [{ ...baseItem, seq: 0, kind: 'AssistantStreaming', streaming: true, body: '正在' }],
      },
    })

    expect(wrapper.find('[data-test=session-streaming]').exists()).toBe(true)
    expect(wrapper.find('li').classes()).toContain('streaming')

    // Clicking it must not try to select seq 0: there is nothing to edit yet.
    await wrapper.find('li').trigger('click')
    expect(wrapper.emitted('select')).toBeUndefined()
  })

  it('renders the streaming reply as markdown, half-finished or not', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          {
            ...baseItem,
            seq: 0,
            kind: 'AssistantStreaming',
            streaming: true,
            // A reply arrives in pieces, so the renderer sees markdown that is not finished yet.
            body: '**重点**\n\n- 一\n- 二\n\n还有未闭合的 *星号',
          },
        ],
      },
    })

    const row = wrapper.find('li')
    expect(row.find('strong').text()).toBe('重点')
    expect(row.findAll('li')).toHaveLength(2)

    // The half-written emphasis stays literal rather than swallowing the rest of the text.
    expect(row.text()).toContain('未闭合的 *星号')
  })

  it('selects a normal event on click', async () => {
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 3 }] } })
    await wrapper.find('li').trigger('click')
    expect(wrapper.emitted('select')?.[0]).toEqual([3])
  })
})

/**
 * The pieces the shell is built from. Each is mounted with props, so a failure points at the component
 * rather than at the app.
 */

function stubApi(overrides: Partial<PartnerApiSurface> = {}): PartnerApiSurface {
  return {
    session: vi.fn(async () => ({ lastSeq: 0, items: [], context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 } })),
    editSession: vi.fn(async () => {}),
    deleteEvent: vi.fn(async () => {}),
    retractFrom: vi.fn(async () => {}),
    sessionRevisions: vi.fn(async () => []),
    queue: vi.fn(async () => ({ headSeq: 0, runningItemId: null, depth: 0, items: [] })),
    cancel: vi.fn(async () => true),
    submitInput: vi.fn(async () => ({ itemId: 'in-1', seq: 1, status: 'Queued', deduplicated: false })),
    uploadMedia: vi.fn(async () => ({ mediaId: 'm1', contentType: 'image/png', bytes: 2048 })),
    approvals: vi.fn(async () => ({ timeoutSeconds: 120, pending: [] })),
    answerApproval: vi.fn(async () => {}),
    desktop: vi.fn(async () => ({ enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [], lastObsId: null, entityCount: 0, entities: [] })),
    observeDesktop: vi.fn(async () => ({ obsId: 'obs-1', count: 0, entities: [] })),
    setApprovalGear: vi.fn(async () => ({ approval: 'all', changed: false })),
    mediaBlob: vi.fn(async () => new Blob(['png'], { type: 'image/png' })),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], })),
    devices: vi.fn(async () => ({ inputs: ['synthetic-mic'], outputs: ['synthetic-speaker'] })),
    listModels: vi.fn(async () => ({ baseUrl: 'http://127.0.0.1:9/v1', models: ['qwen3', 'llama3'] })),    voice: vi.fn(async () => ({ input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } })),
    memory: vi.fn(async () => []),
    recall: vi.fn(async () => []),
    editMemory: vi.fn(async () => {}),
    deleteMemory: vi.fn(async () => {}),
    memoryRevisions: vi.fn(async () => []),
    settings: vi.fn(async () => ({
      input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40 },
      output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
      asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
      tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
      model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096 },
    desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
    })),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => ({
      settings: { ...(await stubApi().settings()), ...patch, model: { ...patch.model, apiKeySet: false } },
      consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
      voice: { input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } },
      compaction: null,
    })),
    rebuild: vi.fn(async () => ({ sessionItems: 0, sessionLastSeq: 0, memoryIndexed: 0, queueLogEntries: 0 })),
    ...overrides,
  }
}

describe('summarizeQueue', () => {
  it('counts what the badge claims, from the same view the queue screen shows', () => {
    const view: QueueView = {
      headSeq: 3,
      runningItemId: 'q2',
      depth: 2,
      items: [
        { itemId: 'q1', seq: 1, source: 'Text', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'a' },
        { itemId: 'q2', seq: 2, source: 'Text', status: 'Running', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'b' },
        { itemId: 'q3', seq: 3, source: 'Schedule', status: 'Failed', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 1, error: 'x', payload: 'c' },
      ],
    }

    expect(summarizeQueue(view)).toEqual({ depth: 2, pending: 2, failed: 1, runningItemId: 'q2' })
  })
})

describe('top bar', () => {
  const summary: QueueSummary = { depth: 1, pending: 1, failed: 1, runningItemId: 'q-0184' }

  it('shows blocked work without opening the queue view', () => {
    const wrapper = mount(TopBar, {
      props: {
        viewLabel: '会话',
        context: '会话 #128 · assistant',
        summary,
        collapsed: false,
        connected: true,
        connectionError: '',
        theme: 'auto',
      },
    })

    const badge = wrapper.find('[data-test="blocked"]').text()
    expect(badge).toContain('队列 1')
    expect(badge).toContain('失败 1')
    expect(badge).toContain('执行中 q-0184')
  })

  it('marks a failure in the error colour and a lost connection explicitly', () => {
    const failing = mount(TopBar, {
      props: { viewLabel: '会话', context: '', summary, collapsed: false, connected: true, connectionError: '', theme: 'auto' },
    })
    expect(failing.find('[data-test="blocked"]').classes()).toContain('badge-error')

    const offline = mount(TopBar, {
      props: { viewLabel: '会话', context: '', summary: null, collapsed: false, connected: false, connectionError: 'token 失效（401）', theme: 'auto' },
    })
    expect(offline.find('[data-test="topbar-disconnected"]').exists()).toBe(true)
  })

  it('asks the shell to cycle the theme rather than deciding itself', async () => {
    const wrapper = mount(TopBar, {
      props: { viewLabel: '会话', context: '', summary: null, collapsed: false, connected: true, connectionError: '', theme: 'auto' },
    })

    await wrapper.find('[data-test="theme-toggle"]').trigger('click')

    expect(wrapper.emitted('cycle-theme')).toHaveLength(1)
  })
})

describe('side bar', () => {
  it('reports a rebuild report and emits the destructive action', async () => {
    const wrapper = mount(SideBar, {
      props: {
        items: [{ key: 'session', label: '会话', icon: '◈' }],
        active: 'session',
        collapsed: false,
        rebuild: { sessionItems: 4, sessionLastSeq: 6, memoryIndexed: 1, queueLogEntries: 3 },
      },
    })

    expect(wrapper.find('[data-test="sidebar-rebuild-report"]').text()).toContain('会话 4')

    await wrapper.find('[data-test="sidebar-rebuild"]').trigger('click')
    expect(wrapper.emitted('rebuild')).toHaveLength(1)
  })

  it('marks the active view and hides labels when collapsed', () => {
    const wrapper = mount(SideBar, {
      props: {
        items: [
          { key: 'session', label: '会话', icon: '◈' },
          { key: 'queue', label: '队列', icon: '☰' },
        ],
        active: 'queue',
        collapsed: true,
        rebuild: null,
      },
    })

    expect(wrapper.find('[data-test="tab-队列"]').classes()).toContain('active')
    expect(wrapper.find('[data-test="tab-会话"]').classes()).not.toContain('active')
    expect(wrapper.find('[data-test="sidebar"]').classes()).toContain('is-collapsed')
  })
})

describe('composer', () => {
  it('delivers the typed text as text and clears the box', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('  打开书房的灯  ')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    // No source picker: what arrives from this box is a text message, and the third argument stays unset.
    expect(api.submitInput).toHaveBeenCalledWith('打开书房的灯', 'text', undefined, [])
    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('')
    expect(wrapper.emitted('sent')).toHaveLength(1)
  })

  it('attaches images, previews them, and sends their ids', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    const file = new File(['png'], 'lamp.png', { type: 'image/png' })
    const input = wrapper.find<HTMLInputElement>('[data-test="composer-attach-input"]')
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await flushPromises()

    expect(api.uploadMedia).toHaveBeenCalledWith(file)
    expect(wrapper.find('[data-test="composer-attachment"]').exists()).toBe(true)

    // An attachment alone is enough to send: a picture with no caption is still a message.
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('', 'text', undefined, ['m1'])
    expect(wrapper.find('[data-test="composer-attachment"]').exists()).toBe(false)
  })

  it('drops an attachment without sending it', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    const file = new File(['png'], 'lamp.png', { type: 'image/png' })
    const input = wrapper.find<HTMLInputElement>('[data-test="composer-attach-input"]')
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await flushPromises()

    await wrapper.find('[data-test="composer-detach"]').trigger('click')
    expect(wrapper.find('[data-test="composer-attachment"]').exists()).toBe(false)
    expect(api.submitInput).not.toHaveBeenCalled()
  })

  it('offers the approval gear, and only when the desktop is on', async () => {
    const api = stubApi()
    const stats = { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }
    const consumer = { running: true, reason: '运行中', model: 'm', stats, tools: [] }

    // Off: there is nothing to approve, so there is nothing to choose.
    const off = mount(Composer, { props: { api, consumer, approval: null } })
    await flushPromises()
    expect(off.find('[data-test=composer-approval]').exists()).toBe(false)

    const on = mount(Composer, { props: { api, consumer, approval: 'all' } })
    await flushPromises()

    const select = on.find<HTMLSelectElement>('[data-test=composer-approval]')
    expect(select.element.value).toBe('all')
    expect(select.findAll('option').map((option) => option.text())).toEqual([
      '审批：不审批',
      '审批：危险指令审批',
      '审批：全部审批',
    ])

    // The choice is the shell's to apply: the composer only reports it.
    await select.setValue('dangerous')
    expect(on.emitted('approval')?.[0]).toEqual(['dangerous'])
  })

  it('says which tools the model has, because off is not inferable', async () => {
    const api = stubApi()
    const stats = { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }

    // Nothing registered: the model cannot ask, and a desktop request just gets text back.
    const none = mount(Composer, {
      props: { api, consumer: { running: true, reason: '运行中', model: 'm', stats, tools: [] } },
    })
    await flushPromises()
    expect(none.find('[data-test=composer-tools]').text()).toContain('桌面未启用')

    // With the capability on, the line names the tools and says which one needs approval.
    const some = mount(Composer, {
      props: {
        api,
        consumer: {
          running: true,
          reason: '运行中',
          model: 'm',
          stats,
          tools: [
            { name: 'desktop_observe', decision: 'allow' },
            { name: 'desktop_act', decision: 'ask' },
          ],
        },
      },
    })
    await flushPromises()

    const line = some.find('[data-test="composer-tools"]').text()
    expect(line).toContain('desktop_observe')
    expect(line).toContain('desktop_act（需批准）')
  })

  it('refuses to send an empty box', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('   ')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).not.toHaveBeenCalled()
  })

  it('shows the same queue numbers the top bar badge shows', () => {
    const summary: QueueSummary = { depth: 1, pending: 1, failed: 1, runningItemId: 'q-0184' }
    const wrapper = mount(Composer, { props: { api: stubApi(), summary } })

    const strip = wrapper.find('[data-test="composer-strip"]').text()
    expect(strip).toContain('队列 1')
    expect(strip).toContain('执行中 q-0184')
  })

  it('explains why a delivered message will not move', () => {
    const consumer: ConsumerState = {
      running: false,
      reason: '未配置模型：缺少 PARTNER_LLM_BASE_URL',
      model: null,
      stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 },
      tools: [],
    }

    const wrapper = mount(Composer, { props: { api: stubApi(), consumer } })

    // A queue nothing executes looks exactly like a busy one without this.
    expect(wrapper.find('[data-test="composer-consumer"]').text()).toContain('未配置模型')
    expect(wrapper.find('[data-test="composer-strip"]').text()).toContain('未执行')
  })

  it('surfaces a delivery failure instead of swallowing it', async () => {
    const api = stubApi({ submitInput: vi.fn(async () => Promise.reject(new Error('401'))) })
    const wrapper = mount(Composer, { props: { api } })

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('x')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(wrapper.emitted('failed')?.[0]).toEqual(['Error: 401'])
  })
})

describe('inspector', () => {
  const revisions = [
    { at: '2026-09-26T10:00:01+00:00', actor: 'human', body: 'first pass' },
    { at: '2026-09-26T10:00:02+00:00', actor: 'human', body: 'second pass' },
  ]

  it('restores an older revision into the editor rather than editing history', async () => {
    const wrapper = mount(Inspector, {
      props: { testPrefix: 'session', title: '编辑选中事件（追加修订，原文保留）', target: '#1 · user', draft: 'second pass', revisions },
    })

    await wrapper.findAll('[data-test="inspector-restore"]')[0]?.trigger('click')

    expect(wrapper.find<HTMLTextAreaElement>('[data-test="session-draft"]').element.value).toBe('first pass')
    // The restore is a draft change; only saving appends anything.
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('keeps the memory hooks and offers delete only where it means something', () => {
    const memory = mount(Inspector, {
      props: { testPrefix: 'memory', title: '编辑选中记忆', target: '未选中', draft: '', revisions: [], deleteLabel: '删除' },
    })

    expect(memory.find('[data-test="memory-target"]').text()).toBe('未选中')
    expect(memory.find('[data-test="memory-draft"]').exists()).toBe(true)
    expect(memory.find('[data-test="memory-delete"]').exists()).toBe(true)

    const session = mount(Inspector, {
      props: { testPrefix: 'session', title: '编辑选中事件', target: '未选中', draft: '', revisions: [] },
    })

    expect(session.find('[data-test="session-draft"]').exists()).toBe(true)
    expect(session.find('[data-test="memory-delete"]').exists()).toBe(false)
  })
})

describe('status bar', () => {
  it('names the model when the queue is being executed', () => {
    const wrapper = mount(StatusBar, {
      props: {
        connected: true,
        connectionError: '',
        endpoint: 'http://127.0.0.1:5280',
        summary: { depth: 0, pending: 0, failed: 0, runningItemId: null },
        consumer: {
          running: true,
          reason: '运行中',
          model: 'qwen3',
          stats: { completed: 3, failed: 0, canceled: 0, skipped: 0, handled: 3 },
          tools: [],
        },
        version: 'v0.1.0',
      },
    })

    expect(wrapper.find('[data-test="status-consumer"]').text()).toBe('消费中 · qwen3')
  })

  it('says the queue is not being executed, and why', () => {
    const wrapper = mount(StatusBar, {
      props: {
        connected: true,
        connectionError: '',
        endpoint: 'http://127.0.0.1:5280',
        summary: null,
        consumer: {
          running: false,
          reason: '未配置模型：缺少 PARTNER_LLM_BASE_URL',
          model: null,
          stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 },
          tools: [],
        },
        version: 'v0.1.0',
      },
    })

    const text = wrapper.find('[data-test="status-consumer"]').text()
    expect(text).toContain('未执行')
    expect(text).toContain('PARTNER_LLM_BASE_URL')
  })
})

describe('markdown body', () => {
  it('renders the structures the session log actually uses', () => {
    const wrapper = mount(MarkdownBody, {
      props: {
        body: [
          '## 结果',
          '',
          '> 引用一行，带 `code`',
          '',
          '- 第一项',
          '- 第二项',
          '',
          '| 列 | 值 |',
          '| --- | --- |',
          '| a | 1 |',
          '',
          '```json',
          '{"ok":true}',
          '```',
        ].join('\n'),
      },
    })

    expect(wrapper.find('h4').text()).toBe('结果')
    expect(wrapper.find('blockquote').text()).toContain('引用一行')
    expect(wrapper.find('blockquote code').text()).toBe('code')
    expect(wrapper.findAll('ul li')).toHaveLength(2)
    expect(wrapper.find('table th').text()).toBe('列')
    expect(wrapper.find('table td').text()).toBe('a')
    expect(wrapper.find('pre code').text()).toBe('{"ok":true}')
  })

  it('shows HTML as text instead of interpreting it', () => {
    // Session bodies are model output and this view can drive the desktop, so nothing here is v-html.
    const wrapper = mount(MarkdownBody, { props: { body: '<img src=x onerror="alert(1)">' } })

    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('<img src=x onerror="alert(1)">')
  })

  it('renders emphasis and inline code without breaking plain text', () => {
    const wrapper = mount(MarkdownBody, { props: { body: 'plain **bold** and *em* and `code`' } })

    expect(wrapper.find('strong').text()).toBe('bold')
    expect(wrapper.find('em').text()).toBe('em')
    expect(wrapper.find('code').text()).toBe('code')
    expect(wrapper.text()).toContain('plain')
  })
})

describe('session view composer wiring', () => {
  it('reports a delivery as queued rather than as a new session row', async () => {
    const api = stubApi()
    const wrapper = mount(SessionView, { props: { api } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('hello')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('hello', 'text', undefined, [])
    // The queue is what changed, so the view says so instead of pretending a turn ran.
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('已入队')
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })
})
