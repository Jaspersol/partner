import { mount } from '@vue/test-utils'
import { flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { ConsumerState, PartnerApiSurface, QueueSummary, RuntimeSettings, WebView } from '../src/api'
import ApprovalBanner from '../src/components/ApprovalBanner.vue'
import Composer from '../src/components/Composer.vue'
import MarkdownBody from '../src/components/MarkdownBody.vue'
import SideBar from '../src/components/SideBar.vue'
import StatusBar from '../src/components/StatusBar.vue'
import TopBar from '../src/components/TopBar.vue'
import EventList from '../src/components/EventList.vue'
import Icon from '../src/components/Icon.vue'
import HintTip from '../src/components/HintTip.vue'
import { ICONS } from '../src/icons'
import { toolAppearance } from '../src/toolAppearance'
import SessionView from '../src/views/SessionView.vue'
import { summarizeQueue } from '../src/api'
import type { QueueItem, QueueView } from '../src/api'

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

  it('reads a timed-task request as when and what, not as a desktop action', async () => {
    const create = {
      ...pending,
      toolName: 'schedule_create',
      arguments: '{"message":"提醒我喝水","kind":"interval","intervalMinutes":120}',
    }
    const wrapper = mount(ApprovalBanner, { props: { pending: [create], desktop } })

    expect(wrapper.find('.title').text()).toBe('模型请求创建定时任务')
    const what = wrapper.find('[data-test="approval-schedule"]').text()
    expect(what).toContain('每 2 小时')
    expect(what).toContain('提醒我喝水')

    // None of the desktop wording: no window to keep acting on, and the interval's `kind` is not an action.
    expect(wrapper.find('[data-test="approval-hint"]').text()).not.toContain('窗口')
    expect(wrapper.find('.kind').exists()).toBe(false)
  })

  it('says a one-off given as minutes from now, and what a delete removes', async () => {
    const once = { ...pending, toolName: 'schedule_create', arguments: '{"message":"站起来","kind":"once","delayMinutes":90}' }
    expect(
      mount(ApprovalBanner, { props: { pending: [once], desktop } }).find('[data-test="approval-schedule"]').text(),
    ).toContain('90 分钟后，只一次')

    const remove = { ...pending, toolName: 'schedule_delete', arguments: '{"id":"sch-1a2b3c4d"}' }
    const wrapper = mount(ApprovalBanner, { props: { pending: [remove], desktop } })
    expect(wrapper.find('.title').text()).toBe('模型请求删除定时任务')
    expect(wrapper.find('[data-test="approval-schedule"]').text()).toContain('sch-1a2b3c4d')
  })

  it('still shows unreadable timed-task arguments rather than hiding the question', async () => {
    const broken = { ...pending, toolName: 'schedule_create', arguments: 'not json' }
    const wrapper = mount(ApprovalBanner, { props: { pending: [broken], desktop } })

    expect(wrapper.find('[data-test="approval-schedule"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="approval-allow"]').exists()).toBe(true)
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

  it('says whose question it is when a sub-agent is asking', async () => {
    // A sub-agent's approval arrives while the person may be looking at something else. Without the origin it
    // reads as the main agent acting, which is a different thing to allow.
    const wrapper = mount(ApprovalBanner, {
      props: { pending: [{ ...pending, origin: '子 agent「桌面操控」：打开网易云放歌' }], desktop },
    })

    const origin = wrapper.find('[data-test="approval-origin"]')
    expect(origin.exists()).toBe(true)
    expect(origin.text()).toContain('来自 子 agent「桌面操控」：打开网易云放歌')
  })

  it('says nothing about an origin when the main agent is asking', async () => {
    const wrapper = mount(ApprovalBanner, { props: { pending: [{ ...pending, origin: null }], desktop } })

    expect(wrapper.find('[data-test="approval-origin"]').exists()).toBe(false)
  })
})

describe('status bar job indicator', () => {
  const job = {
    id: 'job-1',
    agentId: 'desktop',
    agentName: '桌面操控',
    scenario: 'desktop',
    goal: '打开网易云放歌',
    status: 'running',
    round: 2,
    maxRounds: 5,
    startedAt: '2026-10-18T10:00:00+00:00',
    finishedAt: null,
    category: null,
    summary: null,
    error: null,
    rounds: [],
  }

  it('names the running job and offers to stop it', async () => {
    const wrapper = mount(StatusBar, {
      props: { connected: true, connectionError: '', endpoint: 'http://127.0.0.1:5280', consumer: null, version: 'v0.1.0', job },
    })

    expect(wrapper.find('[data-test="status-job"]').text()).toContain('子任务进行中 · 桌面操控 · 第 2/5 轮')
    await wrapper.find('[data-test="status-job-cancel"]').trigger('click')
    expect(wrapper.emitted('cancel-job')?.[0]).toEqual(['job-1'])
  })

  it('renders nothing when nothing is delegated', () => {
    const wrapper = mount(StatusBar, {
      props: { connected: true, connectionError: '', endpoint: 'http://127.0.0.1:5280', consumer: null, version: 'v0.1.0', job: null },
    })

    expect(wrapper.find('[data-test="status-job"]').exists()).toBe(false)
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

  it('does not cross the wires between two calls that happen to share a name', () => {
    // The pairing map used to hold call-id -> the card *object* and wrote into it. It now holds a position and
    // replaces the entry there. That is only correct if positions cannot be reused for something else, and the
    // case that would catch a mistake is two calls of the same tool interleaved with results in between: a name
    // is not an identity, only the id in meta is.
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'ToolCall', body: 'bash', meta: { id: 'a', name: 'bash', args: '{"command":"ls"}' } },
          { ...baseItem, seq: 2, kind: 'ToolCall', body: 'bash', meta: { id: 'b', name: 'bash', args: '{"command":"pwd"}' } },
          { ...baseItem, seq: 3, kind: 'ToolResult', body: 'the pwd output', meta: { id: 'b', ok: 'false' } },
          { ...baseItem, seq: 4, kind: 'ToolResult', body: 'the ls output', meta: { id: 'a', ok: 'true' } },
        ],
      },
    })

    const cards = wrapper.findAll('[data-tool]')
    expect(cards).toHaveLength(2)

    // Each card kept its own arguments and received its own result — the second result landed on the first
    // call, which is the failure mode a position-based map is capable of.
    expect(cards[0]?.text()).toContain('ls')
    expect(cards[0]?.text()).toContain('the ls output')
    expect(cards[0]?.attributes('data-status')).toBe('ok')

    expect(cards[1]?.text()).toContain('pwd')
    expect(cards[1]?.text()).toContain('the pwd output')
    expect(cards[1]?.attributes('data-status')).toBe('failed')
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

  it('marks the reply that is still being written, and keeps it out of the inspector', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [{ ...baseItem, seq: 0, kind: 'AssistantStreaming', streaming: true, body: '正在' }],
      },
    })

    // The row says a reply is arriving by its own appearance — the class the scoped CSS hangs the blinking caret
    // off. There is no label beside it announcing it, on purpose.
    expect(wrapper.find('li').classes()).toContain('streaming')

    // Clicking it must not try to select seq 0: there is nothing to edit yet.
    await wrapper.find('li').trigger('click')
    expect(wrapper.emitted('select')).toBeUndefined()
  })

  it('opens a row from the keyboard, and keeps the streaming placeholder out of the tab order', async () => {
    // The list is the master of a master–detail pair. It was click-only: no tabindex, no key handler,
    // so nothing in the session log could be reached without a pointer.
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'Human', body: 'first' },
          { ...baseItem, seq: 0, kind: 'AssistantStreaming', streaming: true, body: '正在' },
        ],
      },
    })

    const rows = wrapper.findAll('li')
    expect(rows[0]?.attributes('tabindex')).toBe('0')
    // seq 0 is not in the log yet, so there is nothing to select or edit behind it.
    expect(rows[1]?.attributes('tabindex')).toBe('-1')

    await rows[0]?.trigger('keydown.enter')
    expect(wrapper.emitted('select')?.[0]).toEqual([1])

    await rows[0]?.trigger('keydown.space')
    expect(wrapper.emitted('select')).toHaveLength(2)
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

  it('does not react to a click on a message', async () => {
    // A click used to select the row and name it in the top bar. There is nothing behind a selection any more,
    // so the message is just text.
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 3 }] } })
    await wrapper.find('li').trigger('click')
    expect(wrapper.emitted('select')).toBeUndefined()
  })

  it('names the kind rather than printing the enum', () => {
    // ui.md §5.2 asks for a word, and asks for a word rather than a colour because colour is already spoken
    // for in this view: 已编辑 and 已失效 own it. `ToolCall` on screen is an internal spelling leaking out.
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'User' },
          { ...baseItem, seq: 2, kind: 'Assistant' },
          { ...baseItem, seq: 3, kind: 'Schedule' },
          { ...baseItem, seq: 4, kind: 'Summary' },
        ],
      },
    })

    const meta = wrapper.findAll('.meta').map((row) => row.text())
    expect(meta[0]).toContain('用户')
    expect(meta[1]).toContain('助手')
    expect(meta[2]).toContain('调度')
    expect(meta[3]).toContain('摘要')

    // The wire value is still there for tests and for anyone reading the markup — both are wanted.
    expect(wrapper.findAll('li').map((row) => row.attributes('data-kind'))).toEqual([
      'User',
      'Assistant',
      'Schedule',
      'Summary',
    ])
  })

  it('falls through to the raw kind when it is one it does not know', () => {
    // An unrecognised kind is exactly the one a reader needs to see, because it is the one they cannot guess —
    // the same rule toolAppearance.ts follows for an unrecognised tool name.
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, items: [{ ...baseItem, seq: 1, kind: 'SomethingNew' }] },
    })

    expect(wrapper.find('.meta').text()).toContain('SomethingNew')
  })

  it('files each tool under a family so a turn can be scanned by colour, not read line by line', () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'ToolCall', body: 'bash', meta: { id: 'a', name: 'bash', args: '{}' } },
          { ...baseItem, seq: 2, kind: 'ToolCall', body: 'grep', meta: { id: 'b', name: 'grep', args: '{}' } },
          { ...baseItem, seq: 3, kind: 'ToolCall', body: 'mystery_tool', meta: { id: 'c', name: 'mystery_tool', args: '{}' } },
        ],
      },
    })

    const families = wrapper.findAll('[data-tool]').map((card) => card.attributes('data-tool-family'))
    expect(families).toEqual(['shell', 'search', 'control'])

    // An unrecognised tool still gets an icon: it is the one a reader cannot guess, so it must not vanish.
    // The assertion is on geometry rather than on text — the icon used to be a character, so "it has a label"
    // was the strongest thing that could be said about it. Now that it is an SVG, the stronger claim is
    // available and worth making: it drew paths, and it drew the neutral fallback rather than nothing.
    const unknown = wrapper.find('[data-tool=mystery_tool]')
    const glyph = unknown.find('.icon svg')
    expect(glyph.exists()).toBe(true)
    expect(glyph.attributes('data-icon')).toBe('diamond')
    expect(glyph.findAll('path').map((path) => path.attributes('d')).filter(Boolean).length).toBeGreaterThan(0)
    // …and it is decorative, because the tool's name is right next to it in text.
    expect(glyph.attributes('aria-hidden')).toBe('true')
  })

  it('keeps the colour channel about the tool and the status channel about the outcome', () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'ToolCall', body: 'bash', meta: { id: 'a', name: 'bash', args: '{}' } },
          { ...baseItem, seq: 2, kind: 'ToolResult', body: 'boom', meta: { id: 'a', name: 'bash', ok: 'false' } },
        ],
      },
    })

    // A failed shell call is still a shell call; only its outcome changed. Conflating the two would leave it
    // indistinguishable from a successful one of a different family.
    const card = wrapper.find('[data-tool=bash]')
    expect(card.attributes('data-status')).toBe('failed')
    expect(card.attributes('data-tool-family')).toBe('shell')
  })

  it('keeps a tool card in the list, one line per call, paired with its result', async () => {
    const items = [
      { ...baseItem, seq: 1, body: 'ask' },
      { ...baseItem, seq: 2, kind: 'ToolCall', body: 'bash', meta: { id: 'a', name: 'bash', args: '{}' } },
      { ...baseItem, seq: 3, kind: 'ToolResult', body: 'out', meta: { id: 'a', name: 'bash', ok: 'true' } },
      { ...baseItem, seq: 4, kind: 'Assistant', body: 'done' },
    ]

    const wrapper = mount(EventList, { props: { selectedSeq: null, items } })

    // The turn is finished, so the card is folded with the rest of its process.
    expect(wrapper.findAll('[data-tool]')).toHaveLength(0)
    await wrapper.find('[data-test="session-fold"]').trigger('click')

    expect(wrapper.findAll('[data-tool]')).toHaveLength(1)
    expect(wrapper.text()).toContain('ask')
    expect(wrapper.text()).toContain('done')
  })

  it('renders a failed turn as ordinary system text, indistinguishable from any other system line', () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'System', body: 'error: 端点返回 400' },
          { ...baseItem, seq: 2, kind: 'System', body: 'consumer started' },
        ],
      },
    })

    const rows = wrapper.findAll('li')
    expect(rows[0]?.text()).toContain('error: 端点返回 400')
    expect(rows[1]?.text()).toContain('consumer started')

    // Nothing marks the failure out from the line under it: the row carries no error class and no error mark.
    expect(rows[0]?.classes()).not.toContain('is-error')
    expect(rows[0]?.find('[data-test=session-error-mark]').exists()).toBe(false)
    expect(rows[1]?.classes()).not.toContain('is-error')
  })

  it('asks twice before deleting, and withdrawing needs no arming', async () => {
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, latestFromSeq: 1, items: [{ ...baseItem, seq: 5 }] },
    })

    // Arming reaches nothing, and says so where it happens: the button under the reader's finger renames itself
    // and 取消 appears beside it. That pairing is the whole announcement.
    await wrapper.find('[data-test="session-row-delete-inline"]').trigger('click')
    expect(wrapper.emitted('remove')).toBeUndefined()
    expect(wrapper.find('[data-test="session-row-delete-inline"]').text()).toContain('确认删除')
    expect(wrapper.find('[data-test="session-row-cancel"]').exists()).toBe(true)

    await wrapper.find('[data-test="session-row-delete-inline"]').trigger('click')
    expect(wrapper.emitted('remove')?.[0]).toEqual([5])
  })

  it('withdraws on one press, and keeps 取消 for the delete only', async () => {
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, latestFromSeq: 1, items: [{ ...baseItem, seq: 5 }] },
    })

    await wrapper.find('[data-test=session-row-withdraw-inline]').trigger('click')
    expect(wrapper.emitted('withdraw')?.[0]).toEqual([5])
    expect(wrapper.emitted('remove')).toBeUndefined()
    // Nothing is armed, so there is nothing to take back: no row offers 取消. The withdrawal stands until the
    // reader sends a new message, which is the server's rule and not a button here.
    expect(wrapper.find('[data-test=session-row-cancel]').exists()).toBe(false)
  })

  it('offers delete only for rows of the latest conversation', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        latestFromSeq: 3,
        items: [
          { ...baseItem, seq: 1, kind: 'Assistant', body: 'older' },
          { ...baseItem, seq: 3, body: 'latest' },
          { ...baseItem, seq: 5, kind: 'Assistant', body: 'reply' },
        ],
      },
    })

    const rows = wrapper.findAll('li')
    expect(rows[0]?.find('[data-test=session-row-delete-inline]').exists()).toBe(false)
    expect(rows[1]?.find('[data-test=session-row-delete-inline]').exists()).toBe(true)
    expect(rows[2]?.find('[data-test=session-row-delete-inline]').exists()).toBe(true)

    // And there is no second place it could hide: the row's actions are the only place they live.
    expect(wrapper.findAll('.row-menu')).toHaveLength(0)
  })

  it('has one 撤回 per row, and no duplicate of it anywhere', () => {
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, latestFromSeq: 1, items: [{ ...baseItem, seq: 5 }] },
    })

    expect(wrapper.findAll('[data-test=session-row-withdraw-inline]')).toHaveLength(1)
    expect(wrapper.find('[data-test=session-row-withdraw]').exists()).toBe(false)
    expect(wrapper.find('.row-menu').exists()).toBe(false)
  })

  it('offers no delete while a withdrawal is pending or a turn is running', async () => {
    const pending = mount(EventList, {
      props: { selectedSeq: null, latestFromSeq: 1, withdrawn: true, items: [{ ...baseItem, seq: 5 }] },
    })
    expect(pending.find('[data-test=session-row-delete-inline]').exists()).toBe(false)

    const busy = mount(EventList, {
      props: { selectedSeq: null, latestFromSeq: 1, busy: true, items: [{ ...baseItem, seq: 5 }] },
    })
    expect(busy.find('[data-test=session-row-delete-inline]').exists()).toBe(false)
  })

  it('offers recall only for the reader\'s own typed messages', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'User', source: 'Text', body: 'mine' },
          { ...baseItem, seq: 2, kind: 'Assistant', source: 'Text', body: 'theirs' },
          { ...baseItem, seq: 3, kind: 'User', source: 'Schedule', body: 'a cron job' },
          { ...baseItem, seq: 4, kind: 'User', source: 'Voice', body: 'spoken' },
        ],
      },
    })

    // Restoring an assistant reply would send it as the human's words on the next turn, and a scheduled or
    // spoken message is not something they can resend by hand.
    const rows = wrapper.findAll('li')
    for (const [index, expected] of [true, false, false, false].entries()) {
      expect(rows[index]?.find('[data-test=session-row-restore]').exists()).toBe(expected)
    }
  })

  it('puts a recalled message back in the box without touching the server', async () => {
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, items: [{ ...baseItem, seq: 5, kind: 'User', source: 'Text', body: '打错了' }] },
    })

    expect(wrapper.find('[data-test=session-row-restore]').text()).toContain('复用')
    await wrapper.find('[data-test=session-row-restore]').trigger('click')
    expect(wrapper.emitted('restore')?.[0]).toEqual([5])
    expect(wrapper.emitted('remove')).toBeUndefined()
    expect(wrapper.emitted('withdraw')).toBeUndefined()
  })

  it('arms one row at a time, so a second row cannot inherit the first one\'s confirmation', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        latestFromSeq: 1,
        items: [
          { ...baseItem, seq: 1, kind: 'User', source: 'Text', body: 'one' },
          { ...baseItem, seq: 2, kind: 'Assistant', source: 'Text', body: 'two' },
        ],
      },
    })

    const rows = wrapper.findAll('li')
    await rows[0]?.find('[data-test=session-row-delete-inline]').trigger('click')
    expect(rows[0]?.find('[data-test=session-row-delete-inline]').text()).toContain('确认删除')

    // Arming the second row moves the confirmation rather than leaving two armed at once.
    await rows[1]?.find('[data-test=session-row-delete-inline]').trigger('click')
    expect(rows[0]?.find('[data-test=session-row-delete-inline]').text()).toBe('删除')
    expect(rows[1]?.find('[data-test=session-row-delete-inline]').text()).toContain('确认删除')
    expect(wrapper.emitted('remove')).toBeUndefined()
  })

  it('lets Escape back out of an armed delete, because cancel is not only a button', async () => {
    // Armed and then abandoned is the state a reader most needs out of, and the pointer may already be
    // nowhere near the row: the keyboard path has to reach it too.
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, latestFromSeq: 1, items: [{ ...baseItem, seq: 5 }] },
    })

    await wrapper.find('[data-test=session-row-delete-inline]').trigger('click')
    await wrapper.find('li').trigger('keydown', { key: 'Escape' })

    expect(wrapper.find('[data-test=session-row-delete-inline]').text()).toBe('删除')
    expect(wrapper.emitted('remove')).toBeUndefined()
    // And the next press arms again rather than confirming what Escape just cancelled.
    await wrapper.find('[data-test=session-row-delete-inline]').trigger('click')
    expect(wrapper.emitted('remove')).toBeUndefined()
  })

  it('shows no source chip on an Agent row, because that is the main agent replying', () => {
    // Sub-agent reports are filtered out by the server; an Agent-source row left in the view is the main
    // agent's answer to one, and labelling it 「子 agent」 would name the wrong speaker.
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'Assistant', source: 'Agent', body: '收到汇报' },
          { ...baseItem, seq: 2, kind: 'User', source: 'Text', body: 'mine' },
        ],
      },
    })

    const rows = wrapper.findAll('li')
    expect(rows[0]?.find('.source').exists()).toBe(false)
    expect(rows[0]?.text()).not.toContain('子 agent')
    // A known source still gets its word.
    expect(rows[1]?.find('.source').text()).toBe('文本')
  })

  it('shows recorded reasoning folded, and live reasoning open with a clock', () => {
    const recorded = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [{ ...baseItem, seq: 4, kind: 'Thinking', body: '先确认时区\n再换算', at: '2026-09-26T10:00:05+00:00' }],
      },
    })

    // Done thinking: there for a reader to consult, not something to re-read on every scrollback.
    const row = recorded.find('[data-test="session-thinking"]')
    expect(row.attributes('open')).toBeUndefined()
    expect(row.find('summary').text()).toContain('思考')
    expect(row.find('summary').text()).toContain('先确认时区')
    expect(recorded.find('[data-test="session-thinking-elapsed"]').exists()).toBe(false)

    // Still thinking: open, and saying how long, because otherwise the wait reads as a hang.
    const live = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          {
            ...baseItem,
            seq: 0,
            kind: 'ThinkingStreaming',
            streaming: true,
            body: '先确认时区',
            reasoning: '先确认时区',
            reasoningSince: new Date(Date.now() - 4000).toISOString(),
          },
        ],
      },
    })

    const liveRow = live.find('[data-test="session-thinking"]')
    expect(liveRow.attributes('open')).toBeDefined()
    expect(liveRow.find('[data-test="session-thinking-elapsed"]').text()).toMatch(/已思考 00:0[34]/)
  })

  it('starts the clock from the row the server dated, not from when the client noticed', () => {
    // The server is the one that knows when the model began thinking; a client-side "now" would say 00:00 on
    // every row and make a three-minute wait look instantaneous.
    const started = new Date(Date.now() - 75000)
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          {
            ...baseItem,
            seq: 0,
            kind: 'ThinkingStreaming',
            streaming: true,
            body: '先确认时区',
            at: started.toISOString(),
          },
        ],
      },
    })

    const elapsed = wrapper.find('[data-test="session-thinking-elapsed"]').text()
    expect(elapsed).toMatch(/已思考 01:1[45]/)
  })

  it('keeps a thinking row as its own row rather than folding it into the answer', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, body: '问' },
          { ...baseItem, seq: 2, kind: 'Thinking', body: '想' },
          { ...baseItem, seq: 3, kind: 'Assistant', body: '答' },
        ],
      },
    })

    // The turn is over, so its reasoning is folded away until it is asked for — and when it is, it is its own
    // row, not text inside the answer.
    expect(wrapper.find('[data-test="session-thinking"]').exists()).toBe(false)
    await wrapper.find('[data-test="session-fold"]').trigger('click')

    expect(wrapper.find('[data-test="session-thinking"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('问')
    expect(wrapper.text()).toContain('答')
  })

  describe('a finished turn', () => {
    const turn = [
      { ...baseItem, seq: 1, body: '查一下天气' },
      { ...baseItem, seq: 2, kind: 'System', body: '- 用户在上海', meta: { label: 'recall' } },
      { ...baseItem, seq: 3, kind: 'Thinking', body: '先看位置' },
      { ...baseItem, seq: 4, kind: 'Assistant', body: '我先查一下', source: 'Text' },
      { ...baseItem, seq: 5, kind: 'ToolCall', body: 'web_search', meta: { id: 'a', name: 'web_search', args: '{}' } },
      { ...baseItem, seq: 6, kind: 'ToolResult', body: '晴', meta: { id: 'a', name: 'web_search', ok: 'true' } },
      { ...baseItem, seq: 7, kind: 'Assistant', body: '今天晴，25 度', source: 'Text' },
    ]

    it('shows only the question and the answer, with the process folded behind one line', () => {
      const wrapper = mount(EventList, { props: { selectedSeq: null, items: turn } })

      expect(wrapper.text()).toContain('查一下天气')
      expect(wrapper.text()).toContain('今天晴，25 度')
      // Thinking, recall, the call and the text said on the way to it are all folded.
      expect(wrapper.text()).not.toContain('我先查一下')
      expect(wrapper.text()).not.toContain('先看位置')
      expect(wrapper.find('[data-tool]').exists()).toBe(false)
      expect(wrapper.find('[data-test="session-recall"]').exists()).toBe(false)

      const fold = wrapper.find('[data-test="session-fold"]')
      expect(fold.attributes('aria-expanded')).toBe('false')
      expect(fold.text()).toContain('思考')
      expect(fold.text()).toContain('召回')
      expect(fold.text()).toContain('工具调用 1')
      expect(fold.text()).toContain('中间输出 1')
    })

    it('opens to the whole process on a click, and folds again on the next', async () => {
      const wrapper = mount(EventList, { props: { selectedSeq: null, items: turn } })
      const fold = () => wrapper.find('[data-test="session-fold"]')

      await fold().trigger('click')
      expect(fold().attributes('aria-expanded')).toBe('true')
      expect(wrapper.text()).toContain('我先查一下')
      expect(wrapper.find('[data-tool]').exists()).toBe(true)
      expect(wrapper.find('[data-test="session-recall"]').exists()).toBe(true)
      expect(wrapper.find('[data-test="session-thinking"]').exists()).toBe(true)
      expect(wrapper.text()).toContain('今天晴，25 度')

      await fold().trigger('click')
      expect(fold().attributes('aria-expanded')).toBe('false')
      expect(wrapper.find('[data-tool]').exists()).toBe(false)
    })

    it('stays open for a turn that is still running, and folds once it is over', async () => {
      const wrapper = mount(EventList, { props: { selectedSeq: null, items: turn, busy: true } })

      // The answer may still be growing and its process is what the reader is watching happen.
      expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(false)
      expect(wrapper.find('[data-tool]').exists()).toBe(true)
      expect(wrapper.text()).toContain('我先查一下')

      await wrapper.setProps({ busy: false })
      expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(true)
      expect(wrapper.find('[data-tool]').exists()).toBe(false)
    })

    it('folds the earlier turns while the latest one is still running', () => {
      const second = [
        { ...baseItem, seq: 8, body: '再查一次' },
        { ...baseItem, seq: 9, kind: 'Thinking', body: '重新想' },
        { ...baseItem, seq: 10, kind: 'ToolCall', body: 'web_search', meta: { id: 'b', name: 'web_search', args: '{}' } },
      ]
      const wrapper = mount(EventList, { props: { selectedSeq: null, items: [...turn, ...second], busy: true } })

      expect(wrapper.findAll('[data-test="session-fold"]')).toHaveLength(1)
      expect(wrapper.find('[data-test="session-thinking"]').exists()).toBe(true)
      expect(wrapper.findAll('[data-tool]')).toHaveLength(1)
    })

    it('does not hide a turn that never reached an answer', () => {
      const wrapper = mount(EventList, {
        props: {
          selectedSeq: null,
          items: [
            { ...baseItem, seq: 1, body: '查' },
            { ...baseItem, seq: 2, kind: 'Thinking', body: '想' },
            { ...baseItem, seq: 3, kind: 'ToolCall', body: 'web_search', meta: { id: 'a', name: 'web_search', args: '{}' } },
            { ...baseItem, seq: 4, kind: 'System', body: 'error: 端点返回 400' },
          ],
        },
      })

      // Whatever happened before the failure is the evidence for it.
      expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(false)
      expect(wrapper.find('[data-tool]').exists()).toBe(true)
    })

    it('folds a hand-off and the reply to its report into one piece of work with one answer', () => {
      // The sub-agent's report is hidden from the view, so the main agent's reply to it follows its earlier
      // 「已交给 …」 directly. The reader sees one question and the last thing said; the rest is the process.
      const wrapper = mount(EventList, {
        props: {
          selectedSeq: null,
          items: [
            { ...baseItem, seq: 1, body: '帮我读文件' },
            { ...baseItem, seq: 2, kind: 'ToolCall', body: 'agent_delegate', meta: { id: 'a', name: 'agent_delegate', args: '{}' } },
            { ...baseItem, seq: 3, kind: 'ToolResult', body: '已交给', meta: { id: 'a', name: 'agent_delegate', ok: 'true' } },
            { ...baseItem, seq: 4, kind: 'Assistant', body: '已经交给子 agent 了' },
            { ...baseItem, seq: 5, kind: 'Thinking', body: '看报告' },
            { ...baseItem, seq: 6, kind: 'Assistant', body: '文件里写的是……' },
          ],
        },
      })

      expect(wrapper.findAll('[data-test="session-fold"]')).toHaveLength(1)
      expect(wrapper.text()).toContain('帮我读文件')
      expect(wrapper.text()).toContain('文件里写的是……')
      expect(wrapper.text()).not.toContain('已经交给子 agent 了')
      expect(wrapper.find('[data-test="session-thinking"]').exists()).toBe(false)
      expect(wrapper.find('[data-tool]').exists()).toBe(false)
    })

    it('waits while work is still going on, even though the model has stopped talking', async () => {
      // The main agent said 「已交给 …」 and its run is over, but the sub-agent is working and its report is still
      // to be answered. That is not a finished turn, so nothing is folded yet.
      const items = [
        { ...baseItem, seq: 1, body: '打开网易云音乐' },
        { ...baseItem, seq: 2, kind: 'Thinking', body: '交给桌面子 agent' },
        { ...baseItem, seq: 3, kind: 'ToolCall', body: 'agent_delegate', meta: { id: 'a', name: 'agent_delegate', args: '{}' } },
        { ...baseItem, seq: 4, kind: 'ToolResult', body: '已交给', meta: { id: 'a', name: 'agent_delegate', ok: 'true' } },
        { ...baseItem, seq: 5, kind: 'Assistant', body: '已经交给桌面操控子 agent 了，稍等结果回来' },
      ]
      const wrapper = mount(EventList, { props: { selectedSeq: null, items, busy: false, active: true } })

      expect(wrapper.find('[data-test="session-fold"]').exists()).toBe(false)
      expect(wrapper.find('[data-tool]').exists()).toBe(true)
      expect(wrapper.text()).toContain('稍等结果回来')

      // The report is answered and everything is over: now it folds, and only the last answer is left.
      await wrapper.setProps({
        items: [...items, { ...baseItem, seq: 6, kind: 'Assistant', body: '网易云音乐已经打开' }],
        active: false,
      })
      expect(wrapper.findAll('[data-test="session-fold"]')).toHaveLength(1)
      expect(wrapper.text()).toContain('网易云音乐已经打开')
      expect(wrapper.text()).not.toContain('稍等结果回来')
      expect(wrapper.find('[data-tool]').exists()).toBe(false)
    })
  })
})

/**
 * The pieces the shell is built from. Each is mounted with props, so a failure points at the component
 * rather than at the app.
 */

function stubApi(overrides: Partial<PartnerApiSurface> = {}): PartnerApiSurface {
  return {
    session: vi.fn(async () => ({ lastSeq: 0, conversation: { latestFromSeq: null, withdrawn: null }, items: [], context: { usedTokens: 0, budgetTokens: 28672, contextTokens: 32768, maxTokens: 4096, keptItems: 0, foldedItems: 0, fraction: 0 } })),
    editSession: vi.fn(async () => {}),
    deleteLatest: vi.fn(async () => ({ removedFrom: 1, removedTo: 1, purgedEvents: 1, purgedRevisions: 0, purgedMemories: 0, eventSeq: 2 })),
    withdrawFrom: vi.fn(async () => ({ withdrawnFrom: 1, withdrawnTo: 1, conversations: 1, events: 1 })),
    cancelWithdrawal: vi.fn(async () => {}),
    sessionRevisions: vi.fn(async () => []),
    queue: vi.fn(async () => ({ headSeq: 0, runningItemId: null, depth: 0, items: [] })),
    cancel: vi.fn(async () => true),
    submitInput: vi.fn(async () => ({ itemId: 'in-1', seq: 1, status: 'Queued', deduplicated: false })),
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
    testVoice: vi.fn(async (kind: string) => ({ ok: true, detail: `可达${kind}`, latencyMs: 3 })),
    enginePackage: vi.fn(async () => { throw new Error('no engine module in this fixture') }),
    downloadEnginePackage: vi.fn(),
    cancelEnginePackage: vi.fn(),
    setEngineRoot: vi.fn(),
    mediaBlob: vi.fn(async () => new Blob(['png'], { type: 'image/png' })),
    consumer: vi.fn(async () => ({ running: true, reason: '已注入模型', model: 'stub', stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], })),
    devices: vi.fn(async () => ({ inputs: ['synthetic-mic'], outputs: ['synthetic-speaker'] })),
    memoryAll: vi.fn(async () => []),
    setMemoryTier: vi.fn(async () => {}),
    setMemoryScope: vi.fn(async () => {}),
    createMemory: vi.fn(async () => 'm-manual-new'),
    memory: vi.fn(async () => []),
    listModels: vi.fn(async () => ({ baseUrl: 'http://127.0.0.1:9/v1', models: ['qwen3', 'llama3'] })),
    voice: vi.fn(async () => ({ input: { running: false, reason: '未启用', device: null, count: 0, rms: 0, ambientRms: 0, thresholdMisconfigured: false, awake: false }, output: { running: false, reason: '未启用', device: null, count: 0 } })),
    recall: vi.fn(async () => []),
    editMemory: vi.fn(async () => {}),
    deleteMemory: vi.fn(async () => {}),
    memoryRevisions: vi.fn(async () => []),
    settings: vi.fn(async () => ({
      input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, wakeTimeoutMs: 5000, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40, wakeTone: true, bargeIn: false },
      output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
      asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
      tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
      local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
      model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
      desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
      mcp: { enabled: false, servers: [], allowedTools: [] },
      skills: { disabled: [] },
      agents: { main: { prompt: null }, subAgents: [] },
      files: { enabled: false, roots: [] },
      web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] },
    })),
    skills: vi.fn(async () => ({ skills: [], warnings: [] })),
    web: vi.fn(async (): Promise<WebView> => ({
      enabled: true,
      proxy: null,
      timeBudgetSeconds: 12,
      maxResults: 8,
      engines: [],
      healthPath: 'D:/workspace/web/health.json',
    })),
    agents: vi.fn(async () => ({
      main: { prompt: null, defaultPrompt: '', effectivePrompt: '', promptTokens: 0 },
      subAgents: [],
      scenarios: [],
      problems: [],
      limits: { maxRounds: 10, defaultMaxRounds: 5, promptTokens: 8192 },
      files: { enabled: false, roots: [] },
      desktopEnabled: false,
      webEnabled: false,
    })),
    saveAgents: vi.fn(async (agents) => ({
      agents: {
        main: { ...agents.main, defaultPrompt: '', effectivePrompt: '', promptTokens: 0 },
        subAgents: [],
        scenarios: [],
        problems: [],
        limits: { maxRounds: 10, defaultMaxRounds: 5, promptTokens: 8192 },
        files: { enabled: false, roots: [] },
        desktopEnabled: false,
      webEnabled: false,
      },
      consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
    })),
    agentJobs: vi.fn(async () => []),
    cancelAgentJob: vi.fn(async () => true),
    schedules: vi.fn(async () => []),
    createSchedule: vi.fn(),
    pauseSchedule: vi.fn(),
    resumeSchedule: vi.fn(),
    deleteSchedule: vi.fn(async () => true),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => {
      // The settings response carries the agents section with sub-agent keys masked, so the stub mirrors the
      // server's shape rather than handing the stored one back verbatim.
      const stored = await stubApi().settings()
      return {
        settings: {
          ...stored,
          ...patch,
          agents: {
            main: patch.agents.main,
            subAgents: patch.agents.subAgents.map((agent) => ({
              ...agent,
              model: agent.model === null ? null : { ...agent.model, apiKeySet: Boolean(agent.model.apiKey) },
            })),
          },
          model: { ...patch.model, apiKeySet: false },
        },
        consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
        voice: { input: { running: false, reason: '未启用', device: null, count: 0, rms: 0, ambientRms: 0, thresholdMisconfigured: false, awake: false }, output: { running: false, reason: '未启用', device: null, count: 0 } },
        compaction: null,
      }
    }),
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
  it('reads a failure out without pretending it leads anywhere', async () => {
    // It was a button once, pointing at a queue page. That page is gone: the queue is the composer's own list,
    // one click from where a reader already stands. A control that cannot act is worse than an absent one, so
    // the badge is a readout — and it is the announced form that still says a failure out loud. The depth and
    // the running item's id are not on it: 「队列 1 · 执行中 in-…」 is an internal id nobody can act on.
    const wrapper = mount(TopBar, {
      props: {
        viewLabel: '会话',
        context: '',
        summary: { depth: 1, pending: 1, failed: 1, runningItemId: 'q-0184' },
        collapsed: false,
        connected: true,
        connectionError: '',
        theme: 'auto',
      },
    })

    const badge = wrapper.find('[data-test="blocked"]')
    expect(badge.element.tagName).toBe('SPAN')
    expect(badge.text()).toBe('失败 1')
    expect(badge.text()).not.toContain('队列')
    expect(badge.text()).not.toContain('q-0184')
    // The failure is still said, in the channel a screen reader hears.
    expect(wrapper.find('[data-test="blocked-announcer"]').text()).toContain('失败')
  })

  it('shows no badge at all while nothing has failed', async () => {
    // The queue keeps only what is still in play, so a settled failure is no longer counted here — it is read
    // in the conversation, where the failed turn wrote an error event. Running or waiting work is the composer's
    // to show, so the top bar stays quiet rather than print a depth and an id.
    const wrapper = mount(TopBar, {
      props: {
        viewLabel: '会话',
        context: '',
        summary: { depth: 1, pending: 1, failed: 0, runningItemId: 'q-0184' },
        collapsed: false,
        connected: true,
        connectionError: '',
        theme: 'auto',
      },
    })

    expect(wrapper.find('[data-test="blocked"]').exists()).toBe(false)
    // An unknown queue (the read failed) is not drawn as a placeholder either.
    await wrapper.setProps({ summary: null })
    expect(wrapper.find('[data-test="blocked"]').exists()).toBe(false)
  })
  it('is not a button when there is nowhere to go', () => {
    // Disconnected, the shell shows a connection error instead of the views. A control that cannot act is
    // worse than an absent one — the same rule as the sidebar toggle at a narrow width.
    const wrapper = mount(TopBar, {
      props: {
        viewLabel: '会话',
        context: '',
        summary: { depth: 1, pending: 1, failed: 1, runningItemId: 'q-0184' },
        collapsed: false,
        connected: false,
        connectionError: 'token 失效（401）',
        theme: 'auto',
      },
    })

    expect(wrapper.find('[data-test="blocked"]').element.tagName).not.toBe('BUTTON')
  })


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
    expect(badge).toContain('失败 1')
    expect(badge).not.toContain('执行中')
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

  it('speaks the queue through a live region, and says nothing while the counts merely drift', async () => {
    // The badge is polled every five seconds whether or not anything happened. Announcing the badge itself would
    // interrupt a screen reader on nearly every tick, so the spoken copy is a separate node that carries text
    // only when the *shape* of the queue changes.
    const wrapper = mount(TopBar, {
      props: { viewLabel: '会话', context: '', summary, collapsed: false, connected: true, connectionError: '', theme: 'auto' },
    })

    const announcer = () => wrapper.find('[data-test="blocked-announcer"]')
    expect(announcer().attributes('role')).toBe('status')
    expect(announcer().attributes('aria-live')).toBe('polite')
    expect(announcer().text()).toBe('队列阻塞：失败 1 项')

    // The depth moves and nothing new is wrong: the announcement is byte-identical, so the live region has
    // nothing to re-read even though the badge beside it did change.
    const before = announcer().text()
    await wrapper.setProps({ summary: { ...summary, pending: 2, depth: 2 } })
    expect(announcer().text()).toBe(before)

    // A second failure is a different situation, not the same one louder — it must be spoken.
    await wrapper.setProps({ summary: { ...summary, pending: 2, depth: 2, failed: 2 } })
    expect(announcer().text()).toBe('队列阻塞：失败 2 项')
  })

  it('reports a lost connection on the spoken channel too', () => {
    // The badge already says 未连接 on screen; this is the same fact for someone who is not looking at it.
    const wrapper = mount(TopBar, {
      props: { viewLabel: '会话', context: '', summary: null, collapsed: false, connected: false, connectionError: 'token 失效（401）', theme: 'auto' },
    })

    expect(wrapper.find('[data-test="blocked-announcer"]').text()).toContain('未连接')
  })
})

describe('side bar', () => {
  it('marks the active view and hides labels when collapsed', () => {
    const wrapper = mount(SideBar, {
      props: {
        items: [
          { key: 'session', label: '会话', icon: 'chat' },
          { key: 'queue', label: '队列', icon: 'queue' },
        ],
        active: 'queue',
        collapsed: true,
      },
    })

    expect(wrapper.find('[data-test="tab-队列"]').classes()).toContain('active')
    expect(wrapper.find('[data-test="tab-会话"]').classes()).not.toContain('active')
    expect(wrapper.find('[data-test="sidebar"]').classes()).toContain('is-collapsed')
  })
})

describe('composer', () => {
  const RUNNING = { depth: 1, pending: 0, failed: 0, runningItemId: 'q-0184' }

  it('offers to interrupt a running turn exactly while the box is empty', async () => {
    const api = stubApi()

    // Running, box empty: the reader has typed nothing, so there is nothing to queue, and the one action
    // that makes sense is the one that stops the turn they are watching.
    const idle = mount(Composer, { props: { api, summary: RUNNING } })
    await flushPromises()
    expect(idle.find('[data-test="composer-send"]').exists()).toBe(false)
    expect(idle.find('[data-test="composer-interrupt"]').text()).toContain('中断运行')

    // Still running, but now there is something to say. The queue is strictly FIFO, so this message is going
    // to wait regardless — and the label has to admit it rather than say 发送, which the runtime does not
    // promise. The interrupt disappears because they clearly did not want to stop the turn.
    await idle.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('再等一下')
    expect(idle.find('[data-test="composer-interrupt"]').exists()).toBe(false)
    const send = idle.find('[data-test="composer-send"]')
    expect(send.text()).toContain('加入队列')
    expect(send.attributes('disabled')).toBeUndefined()

    await idle.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('')
    expect(idle.find('[data-test="composer-interrupt"]').exists()).toBe(true)
  })

  it('says 发送 when the queue is idle, and never offers an interrupt', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api, summary: { depth: 0, pending: 0, failed: 0, runningItemId: null } } })
    await flushPromises()

    expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(false)
    expect(wrapper.find('[data-test="composer-send"]').text()).toContain('发送')
  })

  it('treats a running item id of empty string as nothing running', async () => {
    // The API models "nothing is running" as null, but an empty string is a plausible thing for a filter or a
    // projection to produce. Reading it as "running" would put an interrupt button on screen for no turn.
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api, summary: { depth: 0, pending: 0, failed: 0, runningItemId: '' } } })
    await flushPromises()

    expect(wrapper.find('[data-test="composer-interrupt"]').exists()).toBe(false)
    expect(wrapper.find('[data-test="composer-send"]').text()).toContain('发送')
  })

  it('cancels through the same API surface it sends through, and tells the shell to re-read the queue', async () => {
    // The composer already calls submitInput/uploadMedia/mediaBlob directly, so routing cancel through the
    // shell would be the odd one out. What the shell owns is the queue *state*, so it hears about this only
    // so the badge behind it stops claiming a turn is running.
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api, summary: RUNNING } })
    await flushPromises()

    await wrapper.find('[data-test="composer-interrupt"]').trigger('click')
    await flushPromises()

    expect(api.cancel).toHaveBeenCalledWith('q-0184')
    expect(wrapper.emitted('cancel')).toEqual([['q-0184']])
  })

  it('does not tell the shell the queue changed when the cancel did not happen', async () => {
    // An item that already finished is not a state change. Announcing one would make the shell re-read a
    // queue that never moved, and would put a false "已中断" in front of a reader who pressed stop on
    // something that was already done.
    const api = stubApi({ cancel: vi.fn(async () => false) })
    const wrapper = mount(Composer, { props: { api, summary: RUNNING } })
    await flushPromises()

    await wrapper.find('[data-test="composer-interrupt"]').trigger('click')
    await flushPromises()

    expect(wrapper.emitted('cancel')).toBeUndefined()
    expect(wrapper.emitted('failed')?.[0]?.[0]).toContain('已完成，无法取消')
  })

  it('reports an item that finished before the click instead of swallowing it', async () => {
    const api = stubApi({ cancel: vi.fn(async () => false) })
    const wrapper = mount(Composer, { props: { api, summary: RUNNING } })
    await flushPromises()

    await wrapper.find('[data-test="composer-interrupt"]').trigger('click')
    await flushPromises()

    expect(wrapper.emitted('failed')?.[0]?.[0]).toContain('已完成，无法取消')
  })

  it('delivers the typed text as text and clears the box', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('  打开书房的灯  ')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    // No source picker: what arrives from this box is a text message. With no gear configured the choice
    // is empty, which the API turns into "use the default" rather than "off".
    expect(api.submitInput).toHaveBeenCalledWith('打开书房的灯', 'text', { media: [], reasoningEffort: '' })
    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('')
    expect(wrapper.emitted('sent')).toHaveLength(1)
  })

  it('sends the gear chosen for this message, not the configured default', async () => {
    // The point of the picker: one message can be answered at a different level than the next one, and the
    // choice has to travel with the payload rather than being saved as a new default.
    const api = stubApi()
    const wrapper = mount(Composer, {
      props: { api, gears: ['off', 'low', 'medium', 'high'], defaultGear: 'medium' },
    })
    await flushPromises()

    // The picker is a pill that opens a segmented choice like the approval gear: its value is on the pill, its menu is the segments.
    const picker = wrapper.find('[data-test="composer-gear"]')
    expect(picker.attributes('data-value')).toBe('medium')
    expect(picker.text()).toContain('中')

    await picker.trigger('click')
    expect(wrapper.findAll('[data-test="composer-gear-pop"] .segmented button').map((node) => node.attributes('data-value'))).toEqual([
      'off',
      'low',
      'medium',
      'high',
    ])

    await wrapper.find('[data-test="composer-gear-pop"] .segmented button[data-value="high"]').trigger('click')
    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('慢慢想')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('慢慢想', 'text', { media: [], reasoningEffort: 'high' })
  })

  it('picks the default up again when settings change, instead of keeping a stale choice', async () => {
    // A settings save moves the default under the user. The next message should follow it — otherwise the
    // composer quietly keeps sending a gear the settings page no longer shows.
    const api = stubApi()
    const wrapper = mount(Composer, {
      props: { api, gears: ['low', 'medium', 'high'], defaultGear: 'medium' },
    })
    await flushPromises()

    await wrapper.find('[data-test="composer-gear"]').trigger('click')
    await wrapper.find('[data-test="composer-gear-pop"] .segmented button[data-value="high"]').trigger('click')
    expect(wrapper.find('[data-test="composer-gear"]').attributes('data-value')).toBe('high')

    await wrapper.setProps({ gears: ['low', 'medium', 'high'], defaultGear: 'low' })

    expect(wrapper.find('[data-test="composer-gear"]').attributes('data-value')).toBe('low')
  })

  it('still offers a default that is not on the menu rather than silently sending something else', async () => {
    // The list is a menu, not a constraint: a hand-edited settings file can hold a default the editor does
    // not list, and dropping it from the picker would send a gear nobody chose.
    const api = stubApi()
    const wrapper = mount(Composer, {
      props: { api, gears: ['low', 'high'], defaultGear: 'turbo' },
    })
    await flushPromises()

    expect(wrapper.find('[data-test="composer-gear"]').attributes('data-value')).toBe('turbo')

    // And it is a segment, so the current value has somewhere to be.
    await wrapper.find('[data-test="composer-gear"]').trigger('click')
    expect(wrapper.findAll('[data-test="composer-gear-pop"] .segmented button').map((node) => node.attributes('data-value'))).toEqual([
      'low',
      'high',
      'turbo',
    ])
  })

  it('offers no picker at all when no menu is configured', async () => {
    // An empty gear list means the feature is not in use; a control with one blank option is worse than
    // no control.
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    expect(wrapper.find('[data-test="composer-gear"]').exists()).toBe(false)
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

    expect(api.uploadMedia).toHaveBeenCalledWith(file, 'image/png', 'lamp.png')
    expect(wrapper.find('[data-test="composer-attachment"]').exists()).toBe(true)

    // The picture is a blob URL, not the API URL: an <img> cannot carry the token, so the bytes come back
    // through the API surface and are hung off an object URL. That is the whole reason img-src has to allow
    // blob: — see indexCsp.test.ts, which is where that dependency stops being invisible.
    const preview = wrapper.find<HTMLImageElement>('[data-test="composer-attachment"] img')
    expect(preview.exists()).toBe(true)
    expect(preview.attributes('src')).toMatch(/^blob:/)

    // An attachment alone is enough to send: a picture with no caption is still a message.
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('', 'text', { media: ['m1'], reasoningEffort: '' })
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

    const pill = on.find('[data-test=composer-approval]')
    expect(pill.attributes('data-value')).toBe('all')
    expect(pill.text()).toContain('全部')

    await pill.trigger('click')
    expect(on.findAll('[data-test=composer-approval-pop] .segmented button').map((option) => option.text())).toEqual([
      '不审批',
      '危险指令',
      '全部',
    ])

    // The choice is the shell's to apply: the composer only reports it.
    await on.find('[data-test=composer-approval-pop] .segmented button[data-value="dangerous"]').trigger('click')
    expect(on.emitted('approval')?.[0]).toEqual(['dangerous'])
  })

  it('says nothing in the status line while the queue is moving', () => {
    const stats = { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }
    const wrapper = mount(Composer, {
      props: { api: stubApi(), consumer: { running: true, reason: '运行中', model: 'm', stats, tools: [] } },
    })

    // The tool list and the queue count used to be printed here. Neither is a reason for the reader to act,
    // and a strip that says 「队列 0」 is a line about nothing — so with nothing wrong there is no strip.
    expect(wrapper.find('[data-test="composer-strip"]').exists()).toBe(false)
  })

  it('refuses to send an empty box', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('   ')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).not.toHaveBeenCalled()
  })

  it('says nothing about the queue when nothing is waiting', () => {
    const summary: QueueSummary = { depth: 1, pending: 0, failed: 1, runningItemId: 'q-0184' }
    const wrapper = mount(Composer, { props: { api: stubApi(), summary, queueItems: [] } })

    // No 「队列 0」, no running id, no empty list: the whole area is absent until something waits in it.
    expect(wrapper.find('[data-test="composer-queue"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('队列')
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

  it('shows what is waiting, oldest first, and folds it away on one press', async () => {
    const items: QueueItem[] = [
      { itemId: 'q3', seq: 3, source: 'Text', status: 'Failed', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 1, error: '端点拒绝', payload: 'third' },
      { itemId: 'q2', seq: 2, source: 'Text', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'second' },
      { itemId: 'q1', seq: 1, source: 'Text', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'first' },
    ]
    const summary = summarizeQueue({ headSeq: 3, runningItemId: null, depth: 3, items })
    const wrapper = mount(Composer, { props: { api: stubApi(), summary, queueItems: items } })

    // Waiting only, in the order the queue runs them: what failed is read where it wrote its error, and a
    // finished entry is the conversation it produced.
    const rows = wrapper.findAll('[data-test="composer-queue-item"]')
    expect(rows.map((row) => row.text())).toEqual(['firstq1', 'secondq2'])

    await wrapper.find('[data-test="composer-queue-toggle"]').trigger('click')
    expect(wrapper.find('[data-test="composer-queue-list"]').exists()).toBe(false)

    await wrapper.find('[data-test="composer-queue-toggle"]').trigger('click')
    expect(wrapper.findAll('[data-test="composer-queue-item"]')).toHaveLength(2)
  })

  it('shows a lone waiting entry without a fold button, because there is nothing to fold', () => {
    const items: QueueItem[] = [
      { itemId: 'q1', seq: 1, source: 'Voice', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'heard something' },
    ]
    const wrapper = mount(Composer, {
      props: { api: stubApi(), summary: summarizeQueue({ headSeq: 1, runningItemId: null, depth: 1, items }), queueItems: items },
    })

    expect(wrapper.find('[data-test="composer-queue-item"]').text()).toContain('heard something')
    expect(wrapper.find('[data-test="composer-queue-toggle"]').exists()).toBe(false)
  })

  it('answers /status by folding the list, and never delivers it as a message', async () => {
    const items: QueueItem[] = [
      { itemId: 'q1', seq: 1, source: 'Text', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'working' },
      { itemId: 'q2', seq: 2, source: 'Text', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'waiting' },
    ]
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api, summary: summarizeQueue({ headSeq: 2, runningItemId: null, depth: 2, items }), queueItems: items } })

    expect(wrapper.find('[data-test="composer-queue-list"]').exists()).toBe(true)

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('/status')
    await wrapper.find('[data-test="composer-input"]').trigger('keydown.enter')
    await flushPromises()

    expect(api.submitInput).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test="composer-queue-list"]').exists()).toBe(false)
    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('')
  })

  it('sends a line that merely mentions /status, because that is a sentence', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('查一下 /status 的实现')
    await wrapper.find('[data-test="composer-input"]').trigger('keydown.enter')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('查一下 /status 的实现', 'text', { media: [], reasoningEffort: '' })
  })

  it('takes a dropped file through the same door as a picked one', async () => {
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    const file = new File(['png'], 'lamp.png', { type: 'image/png' })
    await wrapper.find('[data-test="composer"]').trigger('drop', { dataTransfer: { files: [file] } })
    await flushPromises()

    expect(api.uploadMedia).toHaveBeenCalledWith(file, 'image/png', 'lamp.png')
    expect(wrapper.find('[data-test="composer-attachment"]').exists()).toBe(true)
  })

  it('takes a pasted image through the same door, and previews it', async () => {
    // Pasting a screenshot is the entrance most people use and the only one with no visible affordance, so it
    // is also the one where "nothing happened" is most likely to be the answer.
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    const file = new File(['png'], 'image.png', { type: 'image/png' })
    await wrapper.find('[data-test="composer"]').trigger('paste', { clipboardData: { files: [file] } })
    await flushPromises()

    expect(api.uploadMedia).toHaveBeenCalledWith(file, 'image/png', 'image.png')
    expect(wrapper.find<HTMLImageElement>('[data-test="composer-attachment"] img').attributes('src')).toMatch(/^blob:/)
  })

  it('names a document instead of hanging an <img> on it, and shows the name the server chose', async () => {
    const api = stubApi({
      uploadMedia: vi.fn(async () => ({
        mediaId: 'm2',
        contentType: 'application/pdf',
        // The server sanitises: what the card shows is what the model is told, never the browser's raw string.
        fileName: '季度报表.pdf',
        path: 'C:/w/media/m2__季度报表.pdf',
        bytes: 4096,
      })),
      mediaBlob: vi.fn(async () => new Blob(['pdf'], { type: 'application/pdf' })),
    })
    const wrapper = mount(Composer, { props: { api } })
    await flushPromises()

    const file = new File(['pdf'], 'a-rather-long-report-name.pdf', { type: 'application/pdf' })
    const input = wrapper.find<HTMLInputElement>('[data-test="composer-attach-input"]')
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await flushPromises()

    const card = wrapper.find('[data-test="composer-attachment"]')
    expect(card.find('img').exists()).toBe(false)
    expect(card.find('.file-name').text()).toBe('季度报表.pdf')
    // Fetching the bytes to hang them off an <img> would cost a download and render a broken picture.
    expect(api.mediaBlob).not.toHaveBeenCalled()
  })

  it('puts a recalled message back in the box so it can be edited and resent', async () => {
    const wrapper = mount(Composer, { props: { api: stubApi() } })
    await flushPromises()

    wrapper.vm.restore('打错的那句')
    await flushPromises()

    expect(wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').element.value).toBe('打错的那句')
  })
})

describe('status bar', () => {
  it('draws nothing while the connection is healthy', () => {
    // The endpoint, the model and the version used to be printed here permanently. The model now lives in the
    // composer; a healthy window has nothing to report at the foot.
    const wrapper = mount(StatusBar, {
      props: {
        connected: true,
        connectionError: '',
        endpoint: 'http://127.0.0.1:5280',
        consumer: null,
        version: 'v0.1.0',
      },
    })

    expect(wrapper.find('footer').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('ok ·')
  })

  it('stays a connection readout and leaves the queue to the top bar badge', () => {
    // ui.md §3: the badge moved to the top bar, so the footer degrades to connection state. The queue
    // numbers used to appear in three places on one screen, which made them look like three opinions.
    const wrapper = mount(StatusBar, {
      props: {
        connected: true,
        connectionError: '',
        endpoint: 'http://127.0.0.1:5280',
        consumer: {
          running: true,
          reason: '运行中',
          model: 'qwen3',
          stats: { completed: 3, failed: 2, canceled: 0, skipped: 0, handled: 5 },
          tools: [],
        },
        version: 'v0.1.0',
      },
    })

    expect(wrapper.text()).not.toContain('队列')
    expect(wrapper.text()).not.toContain('失败')
  })

  it('stays quiet on the spoken channel while the connection holds', () => {
    // The footer re-renders on every shell poll. A live region that restates `ok · endpoint` each time would
    // talk over whatever was being read, for a fact that has not changed.
    const props = {
      connected: true,
      connectionError: '',
      endpoint: 'http://127.0.0.1:5280',
      consumer: null,
      version: 'v0.1.0',
    }

    const healthy = mount(StatusBar, { props })
    expect(healthy.find('[data-test="status-announcer"]').exists()).toBe(false)

    // Dropping the endpoint is the failure that makes every other control on the screen a lie, so it speaks.
    const dead = mount(StatusBar, { props: { ...props, connected: false, connectionError: 'token 失效（401）' } })
    expect(dead.find('[data-test="status-announcer"]').text()).toContain('token 失效（401）')
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

    // `##` is an h2. It used to render as an h4, which pushed every session heading three levels down
    // the document outline and gave assistive tech a hierarchy that did not match the source.
    expect(wrapper.find('h2').text()).toBe('结果')
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

  it('parses a code span inside emphasis instead of showing its backticks', () => {
    // Model output mixes the two constantly ("**没有把 `running` 复位**"). The emphasis used to carry its
    // contents as raw text, so the backticks reached the reader verbatim.
    const wrapper = mount(MarkdownBody, { props: { body: '**没有把 `running` 复位**' } })

    expect(wrapper.find('strong code').text()).toBe('running')
    expect(wrapper.text()).not.toContain('`')
  })

  it('leaves a lone backtick pair as written rather than swallowing it', () => {
    const wrapper = mount(MarkdownBody, { props: { body: 'unbalanced ` tick' } })

    expect(wrapper.text()).toContain('unbalanced ` tick')
  })

  /**
   * A body with carriage returns must terminate, and must still be recognised as what it is.
   *
   * <para>
   * This is the hang, kept as a test because it was invisible: the whole app opened onto a session view whose
   * 84th event was a CRLF tool result, the parser stopped consuming lines at the first <c>+ </c> bullet in it,
   * and the webview's renderer then ran at a full core without ever yielding. The UI painted its first screen
   * and then stopped answering clicks — which reads as a frozen app, not as a parse bug.
   * </para>
   *
   * <para>
   * The reason a carriage return is not a whitespace detail: in JavaScript <c>.</c> does not match <c>\r</c>,
   * so with the CR left on each line every pattern ending in <c>(.*)$</c> — the list, the heading, the quote —
   * stops matching. Nothing threw and nothing looked wrong; the lines simply rendered as the wrong thing, and
   * in the one case where the unmatched line was also the line the cursor had to advance past, nothing
   * advanced it.
   * </para>
   */
  it('renders a CRLF body instead of spinning on it', () => {
    // Verbatim from session seq 1288: a `desktop_exec` failure carrying PowerShell's stderr.
    const body =
      'exit=1\n\r\n[stderr]\n字符串缺少终止符: "。\r\n' +
      '    + CategoryInfo          : ParserError: (:) [], ParentContainsErrorRecordException\r\n' +
      '    + FullyQualifiedErrorId : TerminatorExpectedAtEndOfString\r\n \r\n'

    const wrapper = mount(MarkdownBody, { props: { body } })

    // It rendered, and the two error lines are the list they always were — not a paragraph, and not nothing.
    expect(wrapper.findAll('li').length).toBeGreaterThanOrEqual(2)
    expect(wrapper.text()).toContain('CategoryInfo')
    expect(wrapper.text()).toContain('FullyQualifiedErrorId')
  })

  it('recognises every block type in a CRLF body', () => {
    // One of each construct the parser claims to handle, with the line ending Windows tools actually send.
    const body = [
      '# heading',
      '',
      '- bullet one',
      '- bullet two',
      '',
      '> quoted',
      '',
      '| a | b |',
      '| - | - |',
      '| 1 | 2 |',
      '',
      '```',
      'code line',
      '```',
      '',
      '---',
      '',
      'trailing paragraph',
    ].join('\r\n')

    const wrapper = mount(MarkdownBody, { props: { body } })

    expect(wrapper.find('h1').text()).toBe('heading')
    expect(wrapper.findAll('li').length).toBe(2)
    expect(wrapper.find('blockquote').text()).toContain('quoted')
    expect(wrapper.find('table').exists()).toBe(true)
    expect(wrapper.find('pre').text()).toContain('code line')
    expect(wrapper.find('hr').exists()).toBe(true)
    expect(wrapper.text()).toContain('trailing paragraph')
  })

  it('never stops making progress, whatever the body', () => {
    // The second half of the fix, stated as a property rather than as an example: a parser whose loop can
    // consume nothing is one line away from hanging the renderer again, whatever the next branch looks like.
    // Each of these lines is one that some branch above declines, which is exactly how a paragraph used to
    // be left with nothing to push.
    const stubborn = [
      '    + bullet that starts like a block',
      '  - indented bullet',
      '\t> tab then quote',
      '1. ordered',
      '```unclosed fence',
      '| lonely pipe |',
      '#nospace heading',
      '***',
      'a\n  + b',
      '\r\n\r\n',
      '   ',
      '```\r\n```',
      '>\r\n-\r\n',
    ]

    for (const body of stubborn) {
      const started = Date.now()
      const wrapper = mount(MarkdownBody, { props: { body } })
      expect(Date.now() - started, `hung on ${JSON.stringify(body)}`).toBeLessThan(2000)
      expect(wrapper.exists()).toBe(true)
    }
  })
})

describe('session view composer wiring', () => {
  it('delivers a message without announcing it, because the queue is where it shows up', async () => {
    const api = stubApi()
    const wrapper = mount(SessionView, { props: { api } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('hello')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('hello', 'text', { media: [], reasoningEffort: '' })

    // A line naming the item id was said on every single send and told the reader nothing they could act on:
    // the entry is visible in the queue above the box, which is the same answer said where it matters.
    expect(wrapper.find('[data-test="session-notice"]').exists()).toBe(false)
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })
})

describe('icon set', () => {
  it('draws something for every name it declares', () => {
    // A name in the union with no geometry behind it is a blank square that reaches a reader, and nothing else
    // would fail: the component renders, the other tests pass, the screen is simply wrong.
    const empty = Object.entries(ICONS)
      .filter(([, paths]) => paths.length === 0 || paths.every((d) => d.trim() === ''))
      .map(([name]) => name)

    expect(empty).toEqual([])
  })

  it('keeps every icon on the grid it is drawn on', () => {
    // A path that leaves the 24×24 viewBox is clipped by the SVG itself, so it shows up as a mysteriously
    // cut-off stroke. Cheap to catch here; invisible to any test that only looks at the DOM.
    const offGrid: string[] = []
    for (const [name, paths] of Object.entries(ICONS)) {
      for (const d of paths) {
        for (const match of d.matchAll(/[MLCHV]\s*(-?\d+(?:\.\d+)?)[ ,]+(-?\d+(?:\.\d+)?)/g)) {
          const x = Number(match[1])
          const y = Number(match[2])
          if (x < -2 || x > 26 || y < -2 || y > 26) offGrid.push(name)
        }
      }
    }
    expect(offGrid).toEqual([])
  })

  it('resolves an icon for every tool family the table can return', () => {
    // The table hands back a name, not a character, so a typo is a type error at the table and a blank box at
    // runtime. This asserts the second half.
    const names = new Set(Object.keys(ICONS))
    const families = new Set<string>()

    for (const tool of [
      'desktop_screenshot',
      'bash',
      'write',
      'search',
      'fetch',
      'memory_recall',
      'schedule',
      'agent',
      'desktop_act',
      'mystery_tool',
    ]) {
      const appearance = toolAppearance(tool)
      families.add(appearance.family)
      expect(names.has(appearance.icon)).toBe(true)
    }

    // All nine families, plus the neutral fallback for a tool nothing matched — the one a reader cannot guess.
    expect(families.size).toBe(9)
  })

  it('is decorative by default and labelled only when it carries the meaning', () => {
    // Every icon in this app sits beside or inside a control that already names itself in text. An SVG that is
    // not hidden from assistive technology is announced twice.
    const bare = mount(Icon, { props: { name: 'search' } })
    expect(bare.attributes('aria-hidden')).toBe('true')
    expect(bare.attributes('role')).toBeUndefined()
    expect(bare.findAll('path').length).toBeGreaterThan(0)

    const named = mount(Icon, { props: { name: 'search', label: '搜索' } })
    expect(named.attributes('aria-hidden')).toBeUndefined()
    expect(named.attributes('role')).toBe('img')
    expect(named.attributes('aria-label')).toBe('搜索')
  })
})

describe('hint tip', () => {
  it('marks the sentence instead of printing it, and keeps the sentence in the document', () => {
    // The whole point of the change: a settings page with twenty-seven explanations under its fields is a page
    // a reader scrolls past twice. The sentence is hidden, not deleted — which is what lets the tests that
    // already assert on this text keep working.
    const wrapper = mount(HintTip, { slots: { default: '留空表示不配置' } })

    expect(wrapper.find('[data-icon="info"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('留空表示不配置')
    expect(wrapper.find('.hint-dot').attributes('title')).toBeUndefined()
  })

  it('describes the button with the one sentence it opens', () => {
    // `aria-describedby` is why the pop is hidden with opacity rather than visibility: a visibility-hidden
    // element leaves the accessibility tree, and the description would then point at nothing.
    const wrapper = mount(HintTip, { slots: { default: '关闭会立即释放麦克风设备' } })
    const id = wrapper.find('.hint-dot').attributes('aria-describedby')

    expect(id).toBeTruthy()
    const pop = wrapper.find(`[id="${id}"]`)
    expect(pop.exists()).toBe(true)
    expect(pop.attributes('role')).toBe('tooltip')
    expect(pop.text()).toBe('关闭会立即释放麦克风设备')
  })

  it('keeps the markup a sentence came with', () => {
    // Several of these sentences carry <code> and <strong>. A `title` attribute could not, which is the other
    // reason this is an element.
    const wrapper = mount(HintTip, { slots: { default: '<code>PARTNER_LLM_MODEL</code>' } })
    expect(wrapper.find('.hint-pop code').exists()).toBe(true)
  })

  it('gives every instance its own id, so a page of them cannot cross-describe', () => {
    const wrapper = mount(
      { components: { HintTip }, template: '<div><HintTip>甲</HintTip><HintTip>乙</HintTip></div>' },
      {},
    )
    const ids = wrapper.findAll('.hint-dot').map((button) => button.attributes('aria-describedby'))

    expect(new Set(ids).size).toBe(2)
  })

  it('anchors the pop to the end when it is asked to', () => {
    // A mark at the right-hand end of a row opens leftward, or the pop runs off the panel it is in.
    const wrapper = mount(HintTip, { props: { align: 'end' }, slots: { default: '说明' } })
    expect(wrapper.find('.hint-pop').classes()).toContain('is-end')
  })

  it('is not a labelable element, so a click on its row reaches the field rather than the mark', () => {
    // A <label> forwards a click to its first labelable descendant. As a <button> the mark was that descendant,
    // so clicking a form row's name pinned the sentence open instead of focusing the field.
    const wrapper = mount(HintTip, { slots: { default: '说明' } })
    expect(wrapper.find('button').exists()).toBe(false)
    expect(wrapper.find('.hint-dot').attributes('tabindex')).toBe('0')
  })

  it('does not let a click on the mark reach the field it stands beside', () => {
    // It sits inside a <label>, where an unstopped click would move the caret into the input beside it.
    const wrapper = mount(
      { components: { HintTip }, template: '<label><input /><HintTip>说明</HintTip></label>' },
      {},
    )
    let reached = false
    wrapper.find('label').element.addEventListener('click', () => {
      reached = true
    })

    const event = new MouseEvent('click', { bubbles: true, cancelable: true })
    wrapper.find('.hint-dot').element.dispatchEvent(event)

    expect(event.defaultPrevented).toBe(true)
    expect(reached).toBe(false)
  })
})
