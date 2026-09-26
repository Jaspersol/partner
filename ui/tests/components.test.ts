import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { ConsumerState, PartnerApiSurface, QueueSummary, RuntimeSettings } from '../src/api'
import Composer from '../src/components/Composer.vue'
import Inspector from '../src/components/Inspector.vue'
import MarkdownBody from '../src/components/MarkdownBody.vue'
import SideBar from '../src/components/SideBar.vue'
import StatusBar from '../src/components/StatusBar.vue'
import TopBar from '../src/components/TopBar.vue'
import SessionView from '../src/views/SessionView.vue'
import { summarizeQueue } from '../src/api'
import type { QueueView } from '../src/api'

/**
 * The pieces the shell is built from. Each is mounted with props, so a failure points at the component
 * rather than at the app.
 */

function stubApi(overrides: Partial<PartnerApiSurface> = {}): PartnerApiSurface {
  return {
    session: vi.fn(async () => ({ lastSeq: 0, items: [] })),
    editSession: vi.fn(async () => {}),
    sessionRevisions: vi.fn(async () => []),
    queue: vi.fn(async () => ({ headSeq: 0, runningItemId: null, depth: 0, items: [] })),
    cancel: vi.fn(async () => true),
    submitInput: vi.fn(async () => ({ itemId: 'in-1', seq: 1, status: 'Queued', deduplicated: false })),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } })),
    voice: vi.fn(async () => ({ input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } })),
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
      model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120 },
    })),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => ({
      settings: { ...(await stubApi().settings()), ...patch, model: { ...patch.model, apiKeySet: false } },
      consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 } },
      voice: { input: { running: false, reason: '未启用', device: null, count: 0 }, output: { running: false, reason: '未启用', device: null, count: 0 } },
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
  it('delivers the typed text with the chosen source and clears the box', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('  打开书房的灯  ')
    await wrapper.find<HTMLSelectElement>('[data-test="composer-source"]').setValue('voice')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('打开书房的灯', 'voice')
    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('')
    expect(wrapper.emitted('sent')).toHaveLength(1)
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

    expect(api.submitInput).toHaveBeenCalledWith('hello', 'text')
    // The queue is what changed, so the view says so instead of pretending a turn ran.
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('已入队')
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })
})
