import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'

/**
 * The shell itself.
 *
 * <para>
 * A view that is written in the template but never imported still type-checks — Vue's compiler resolves
 * template names loosely, and an unresolved one degrades to a bare custom element that renders nothing at
 * all. The result is a page that is blank rather than red, so only mounting the real thing catches it.
 * </para>
 */

const SETTINGS = {
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
  asr: {
    baseUrl: null,
    model: 'qwen3-asr',
    language: 'Chinese',
    connectTimeoutMs: 2000,
    readTimeoutMs: 3000,
    sampleRate: 16000,
  },
  tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
  model: {
    baseUrl: null,
    model: null,
    apiKeySet: false,
    timeoutSeconds: 120,
    contextTokens: 32768,
    maxTokens: 4096,
    supportsImages: false,
    reasoningEffort: 'medium',
    reasoningGears: ['off', 'low', 'medium', 'high'],
  },
  desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
  mcp: { enabled: false, servers: [], allowedTools: [] },
  skills: { disabled: [] },
}

const CONSUMER = {
  running: false,
  reason: '未启用',
  model: null,
  stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 },
  tools: [],
}

const SESSION = {
  lastSeq: 0,
  context: {
    usedTokens: 0,
    budgetTokens: 28672,
    contextTokens: 32768,
    maxTokens: 4096,
    keptItems: 0,
    foldedItems: 0,
    fraction: 0,
  },
  items: [],
}

// A queue with a turn in flight, so the composer's interrupt button exists in the shell tests.
const RUNNING_ITEM = {
  itemId: 'q-0184',
  seq: 1,
  source: 'Text',
  status: 'Running',
  enqueuedAt: '2026-10-01T10:00:00Z',
  startedAt: '2026-10-01T10:00:00Z',
  finishedAt: null,
  attempt: 1,
  error: null,
  payload: 'hi',
}

const QUEUE_RUNNING = {
  headSeq: 1,
  runningItemId: 'q-0184',
  depth: 1,
  items: [RUNNING_ITEM],
}

const QUEUE = { headSeq: 0, runningItemId: null, depth: 0, items: [] }

function respond(path: string): unknown {
  if (path.includes('/api/settings')) return SETTINGS
  if (path.includes('/api/consumer')) return CONSUMER
  if (path.includes('/api/session')) return SESSION
  if (path.includes('/api/approvals')) return { timeoutSeconds: 120, pending: [] }
  if (path.includes('/api/queue')) return QUEUE
  if (path.includes('/api/skills')) return { skills: [], warnings: [] }
  if (path.includes('/api/voice/devices')) return { inputs: [], outputs: [] }
  if (path.includes('/api/voice')) {
    return {
      input: { running: false, reason: '未启用', device: null, count: 0 },
      output: { running: false, reason: '未启用', device: null, count: 0 },
    }
  }
  return {}
}

describe('app shell', () => {
  let warnings: string[]

  // Every App registers a window-level keydown listener, and an unmounted one keeps its listener: without this,
  // the shortcuts of a test's shell also answer for the shells of the tests before it. That produced a Ctrl+K
  // assertion that was really measuring a stale component, and it would have kept doing so for whichever
  // assertion came next.
  enableAutoUnmount(afterEach)

  beforeEach(() => {
    // A component that is named in a template but never imported is not a type error — Vue's compiler
    // resolves template names loosely and degrades to a bare custom element, so the page renders blank
    // rather than red. The only report is the runtime warning, and it is the one worth failing on.
    warnings = []
    vi.spyOn(console, 'warn').mockImplementation((...args: unknown[]) => {
      warnings.push(args.map(String).join(' '))
    })

    window.__PARTNER_TOKEN__ = 'test-token'
    window.__PARTNER_PORT__ = 5280
    globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
      return new Response(JSON.stringify(respond(path)), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as unknown as typeof fetch
  })

  afterEach(() => {
    vi.restoreAllMocks()
    delete window.__PARTNER_TOKEN__
    delete window.__PARTNER_PORT__
  })

  it('renders every page in the sidebar without a single unresolved component', async () => {
    const wrapper = mount(App)
    await flushPromises()

    const tabs = wrapper.findAll('[data-test^=tab-]')
    // Five, not six: the queue is the composer's list now, not a page of its own. The rail is shorter
    // because there is one less place to be.
    expect(tabs.map((node) => node.text().replace(/[^\p{L}]/gu, ''))).toEqual(['会话', '记忆', 'MCP', '技能', '设置'])

    for (const tab of tabs) {
      await tab.trigger('click')
      await flushPromises()
    }

    expect(warnings).toEqual([])
  })

  it('says why it cannot start when there is no token, instead of asking the server anyway', async () => {
    delete window.__PARTNER_TOKEN__
    const wrapper = mount(App)
    await flushPromises()

    expect(wrapper.find('[data-test=connection-error]').text()).toContain('未找到访问令牌')
    expect(globalThis.fetch).not.toHaveBeenCalled()
  })

  it('switches views with the number row, but never while the reader is typing', async () => {
    // The rule that matters is what the digits must NOT steal: they are the characters a person types into
    // the composer, and a shortcut that swallows half of a message is worse than no shortcut at all.
    const wrapper = mount(App)
    await flushPromises()

    const active = () => wrapper.find('.nav li.active').text()

    window.dispatchEvent(new KeyboardEvent('keydown', { key: '2' }))
    await flushPromises()
    expect(active()).toContain('记忆')

    // Back to the session view, which is the one with a composer, and try the same digit from inside it.
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '1' }))
    await flushPromises()

    //
    // Dispatched *on the input*, not on the window: a KeyboardEvent dispatched on window has
    // `target === window`, which is not a field, so the "am I typing" question gets the wrong answer and the
    // test would pass or fail for the wrong reason.
    const composer = wrapper.find('[data-test="composer-input"]')
    expect(composer.exists()).toBe(true)

    composer.element.dispatchEvent(new KeyboardEvent('keydown', { key: '3', bubbles: true }))
    await flushPromises()
    expect(active()).toContain('会话')

    // Out of the field again, and the same digit works.
    ;(document.activeElement as HTMLElement | null)?.blur()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '3' }))
    await flushPromises()
    expect(active()).toContain('MCP')
  })

  it('leaves every Ctrl chord that is not ours alone', async () => {
    // Ctrl+Tab, Ctrl+W and the rest belong to the browser and the OS. Swallowing them because the handler was
    // written as "if ctrl, do our thing" is how an app takes over the user's computer.
    const wrapper = mount(App)
    await flushPromises()

    const active = () => wrapper.find('.nav li.active').text()
    const before = active()

    for (const key of ['w', 't', 'r', '1']) {
      const event = new KeyboardEvent('keydown', { key, ctrlKey: true, cancelable: true })
      window.dispatchEvent(event)
      expect(event.defaultPrevented).toBe(false)
    }

    await flushPromises()
    expect(active()).toBe(before)
  })

  it('takes Ctrl+K and Ctrl+B, and says so on the controls they belong to', async () => {
    const wrapper = mount(App)
    await flushPromises()

    // Ctrl+B: the rail, which is the thing the button next to it does.
    const toggle = () => wrapper.find('[data-test="sidebar-toggle"]')
    expect(toggle().attributes('title')).toContain('Ctrl+B')

    const collapsedBefore = toggle().attributes('aria-label')
    const event = new KeyboardEvent('keydown', { key: 'b', ctrlKey: true, cancelable: true })
    window.dispatchEvent(event)
    await flushPromises()
    expect(event.defaultPrevented).toBe(true)
    expect(toggle().attributes('aria-label')).not.toBe(collapsedBefore)

    // Ctrl+K: the search box of whichever view is showing — and that is the session view's.
    expect(wrapper.find('[data-test="session-query"]').attributes('title')).toBe('Ctrl+K')

    const search = wrapper.find('[data-test="session-query"]').element as HTMLInputElement
    search.focus()
    const searchEvent = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true })
    window.dispatchEvent(searchEvent)
    await flushPromises()
    expect(searchEvent.defaultPrevented).toBe(true)
  })

  it('does nothing for Ctrl+K on a view that has no search box', async () => {
    // Only two of the five views search. Pretending otherwise would move focus nowhere and look broken.
    const wrapper = mount(App)
    await flushPromises()

    await wrapper.find('[data-test="tab-MCP"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="mcp-enabled"]').exists()).toBe(true)
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true })
    window.dispatchEvent(event)
    await flushPromises()

    // The shortcut does not fire rather than firing at nothing.
    expect(event.defaultPrevented).toBe(false)
  })

  it('keeps the stop button through a failed read, and says the read failed', async () => {
    // A regression from adding 中断运行: the refresh handler cleared the queue state on failure, and the stop
    // button is derived from it — so one transient hiccup made the *stop* control vanish while the turn was
    // still running, with no way to get it back but waiting for the next poll.
    //
    // A blocked-work indicator that blinks out on one failed poll is not a blocked-work indicator, so the last
    // good read is kept and flagged rather than erased.
    let queueReads = 0
    globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
      const path = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url

      // First poll succeeds with a turn in flight; every poll after it fails.
      if (path.includes('/api/queue')) {
        queueReads++
        if (queueReads > 1) throw new TypeError('network down')
        return new Response(JSON.stringify(QUEUE_RUNNING), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }

      return new Response(JSON.stringify(respond(path)), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }) as unknown as typeof fetch

    //  so the fake clock still moves in real time:  is built on a
    // , and a clock frozen outright would hang the very flush this test depends on.
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const wrapper = mount(App)
      await flushPromises()

      // First poll: a turn is running, so the stop button is there and nothing is flagged.
      expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="status-stale"]').exists()).toBe(false)

      // The next poll fails — driven through the shell's own five-second timer rather than by reaching into
      // it, because the timing is part of what is being asserted.
      await vi.advanceTimersByTimeAsync(6000)
      await flushPromises()

      // The stop button survives…
      expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(true)
      // …and the status bar says why what is on screen might not be current.
      expect(wrapper.find('[data-test="status-stale"]').exists()).toBe(true)
      // The badge keeps what it last knew rather than blanking: 阻塞可见 outranks freshness.
      expect(wrapper.find('[data-test="blocked"]').text()).toContain('队列 1')

      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('never offers the sidebar toggle when the window is too narrow for it to act', async () => {
    const original = window.ResizeObserver
    // Both the shell (`.app`) and each view (`.body`) open an observer, so every callback is kept and fired:
    // a single slot would be overwritten by whichever component happened to mount last.
    const observers: Array<() => void> = []
    window.ResizeObserver = class {
      constructor(callback: () => void) {
        observers.push(callback)
      }
      observe(): void {}
      disconnect(): void {}
      unobserve(): void {}
    } as unknown as typeof ResizeObserver

    const fireAll = () => observers.forEach((callback) => callback())

    const app = document.createElement('div')
    app.className = 'app'
    // configurable, so the width can be changed below to simulate a real resize
    Object.defineProperty(app, 'clientWidth', { value: 1200, configurable: true })
    document.body.append(app)

    const body = document.createElement('div')
    body.className = 'body'
    Object.defineProperty(body, 'clientWidth', { value: 1200, configurable: true })
    document.body.append(body)

    const wrapper = mount(App)
    await flushPromises()

    // Wide window: the toggle is offered, and it says what pressing it will do.
    const toggle = () => wrapper.find('[data-test="sidebar-toggle"]')
    expect(toggle().exists()).toBe(true)
    expect(toggle().attributes('aria-label')).toBe('收起侧栏')

    // Narrow the window and let the observers run, as they would on a real resize.
    Object.defineProperty(app, 'clientWidth', { value: 400 })
    fireAll()
    await flushPromises()

    // Below the threshold the rail is the only state there is, so the button is not on screen at all — rather
    // than on screen, unchanged, doing nothing.
    expect(wrapper.find('[data-test="sidebar-toggle"]').exists()).toBe(false)

    // Widen it again and the button comes back with the reader's own preference intact, not reset.
    Object.defineProperty(app, 'clientWidth', { value: 1200 })
    fireAll()
    await flushPromises()

    expect(toggle().exists()).toBe(true)
    expect(toggle().attributes('aria-label')).toBe('收起侧栏')

    app.remove()
    body.remove()
    window.ResizeObserver = original
  })
})
