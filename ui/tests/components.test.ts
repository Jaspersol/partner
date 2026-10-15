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
import Icon from '../src/components/Icon.vue'
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

/**
 * Opens one row's action menu, the way a reader does.
 *
 * The row's three actions went behind a single affordance in S4, so anything that operates them has to reach
 * through it first. Doing that from a helper rather than repeating the two clicks keeps each test reading as
 * what it is about — arming, retracting, recall — instead of about how to get at the button.
 */
async function openRowMenu(wrapper: ReturnType<typeof mount>, index: number): Promise<void> {
  await wrapper.findAll('li')[index]?.find('[data-test=session-row-menu]').trigger('click')
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

  it('selects a normal event on click', async () => {
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 3 }] } })
    await wrapper.find('li').trigger('click')
    expect(wrapper.emitted('select')?.[0]).toEqual([3])
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

  it('drops tool cards entirely in results-only mode, and puts them back', async () => {
    const items = [
      { ...baseItem, seq: 1, body: 'ask' },
      { ...baseItem, seq: 2, kind: 'ToolCall', body: 'bash', meta: { id: 'a', name: 'bash', args: '{}' } },
      { ...baseItem, seq: 3, kind: 'ToolResult', body: 'out', meta: { id: 'a', name: 'bash', ok: 'true' } },
      { ...baseItem, seq: 4, kind: 'Assistant', body: 'done' },
    ]

    const wrapper = mount(EventList, { props: { selectedSeq: null, items } })
    expect(wrapper.findAll('[data-tool]')).toHaveLength(1)

    await wrapper.setProps({ resultsOnly: true })
    expect(wrapper.findAll('[data-tool]')).toHaveLength(0)
    expect(wrapper.text()).toContain('ask')
    expect(wrapper.text()).toContain('done')

    await wrapper.setProps({ resultsOnly: false })
    expect(wrapper.findAll('[data-tool]')).toHaveLength(1)
  })

  it('marks a failed turn as an error row rather than ordinary system text', () => {
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
    expect(rows[0]?.classes()).toContain('is-error')
    expect(rows[0]?.find('[data-test=session-error-mark]').exists()).toBe(true)

    // A system line that is not a failure must not borrow the error styling: the marker has to mean something.
    expect(rows[1]?.classes()).not.toContain('is-error')
  })

  it('says out loud that an action is armed, because a renamed button is not an announcement', async () => {
    // Arming swaps 「删除此条」 for 「确认删除此条」 under the reader's hands. A control whose accessible name
    // changed is not announced, so without this the second press looks like a button that stopped working.
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 5 }] } })

    const announcer = () => wrapper.find('[data-test="session-arm-announcer"]')
    expect(announcer().attributes('aria-live')).toBe('polite')
    expect(announcer().text()).toBe('')

    await openRowMenu(wrapper, 0)
    await wrapper.find('[data-test="session-row-delete"]').trigger('click')
    expect(announcer().text()).toContain('再次按 Enter 确认')
    // It says what will happen *and* that it has not happened yet — the second half is the whole point of
    // arming, and a reader who missed the first half would otherwise assume the row is already gone.
    expect(announcer().text()).toContain('不会撤销')

    await wrapper.find('[data-test="session-row-retract"]').trigger('click')
    expect(announcer().text()).toContain('此条及之后')
    // Not a duplicate: the retract arming replaces the delete one rather than both being pending.
    expect(announcer().text().match(/已准备/g)).toHaveLength(1)

    // Disarming goes quiet again.
    await wrapper.find('[data-test="session-row-cancel"]').trigger('click')
    expect(announcer().text()).toBe('')
  })

  it('asks twice before deleting, and arms each action separately', async () => {
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 5 }] } })

    // The row's actions live behind one affordance now (S4), so the test opens the menu the way a reader
    // does. What it still asserts is unchanged: arming reaches nothing, a second press on the *same* action
    // confirms, and arming one action does not arm the other.
    await openRowMenu(wrapper, 0)

    await wrapper.find('[data-test=session-row-delete]').trigger('click')
    expect(wrapper.emitted('remove')).toBeUndefined()
    expect(wrapper.find('[data-test=session-row-delete]').text()).toContain('确认删除')

    // Arming "删除此条" must not confirm "删除此处及之后": two different amounts of history.
    expect(wrapper.find('[data-test=session-row-retract]').text()).toContain('删除此处及之后')
    expect(wrapper.find('[data-test=session-row-retract]').text()).not.toContain('确认')

    await wrapper.find('[data-test=session-row-delete]').trigger('click')
    expect(wrapper.emitted('remove')?.[0]).toEqual([5])
  })

  it('truncates from a row without touching the rows before it', async () => {
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 5 }] } })

    await openRowMenu(wrapper, 0)
    await wrapper.find('[data-test=session-row-retract]').trigger('click')
    expect(wrapper.emitted('retract')).toBeUndefined()

    await wrapper.find('[data-test=session-row-retract]').trigger('click')
    expect(wrapper.emitted('retract')?.[0]).toEqual([5])
    expect(wrapper.emitted('remove')).toBeUndefined()
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
    // spoken message is not something they can resend by hand. Each row is opened in turn, because "absent
    // from the menu" is now the claim being made rather than "absent from the row".
    const rows = wrapper.findAll('li')
    for (const [index, expected] of [true, false, false, false].entries()) {
      await openRowMenu(wrapper, index)
      expect(rows[index]?.find('[data-test=session-restore]').exists()).toBe(expected)
    }
  })

  it('puts a recalled message back in the box without touching the server', async () => {
    const wrapper = mount(EventList, {
      props: { selectedSeq: null, items: [{ ...baseItem, seq: 5, kind: 'User', source: 'Text', body: '打错了' }] },
    })

    await openRowMenu(wrapper, 0)
    await wrapper.find('[data-test=session-restore]').trigger('click')
    expect(wrapper.emitted('restore')?.[0]).toEqual([5])
    expect(wrapper.emitted('remove')).toBeUndefined()
    expect(wrapper.emitted('retract')).toBeUndefined()
  })

  it('shows one open menu at a time, and closes the open one when it is re-pressed', async () => {
    // A screen of twenty open menus is a screen of noise: every row would show the same three actions
    // floating over a different body of text.
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...baseItem, seq: 1, kind: 'User', source: 'Text', body: 'one' },
          { ...baseItem, seq: 2, kind: 'Assistant', source: 'Text', body: 'two' },
        ],
      },
    })

    await openRowMenu(wrapper, 0)
    expect(wrapper.findAll('.row-menu')).toHaveLength(1)

    await openRowMenu(wrapper, 1)
    expect(wrapper.findAll('.row-menu')).toHaveLength(1)
    expect(wrapper.findAll('li')[1]?.find('.row-menu').exists()).toBe(true)

    // Re-pressing the same row closes it, and drops any arming rather than leaving a half-armed retraction
    // waiting for the next click.
    await wrapper.findAll('li')[1]?.find('[data-test=session-row-menu]').trigger('click')
    expect(wrapper.findAll('.row-menu')).toHaveLength(0)
  })

  it('does not confirm an action the reader only looked at', async () => {
    // Opening the menu and closing it must leave nothing armed: the next click on 删除此条 has to arm first,
    // or a reader who dismissed a menu by accident would then have to press twice for no stated reason.
    const wrapper = mount(EventList, { props: { selectedSeq: null, items: [{ ...baseItem, seq: 5 }] } })

    await openRowMenu(wrapper, 0)
    await wrapper.find('[data-test=session-row-delete]').trigger('click')
    expect(wrapper.find('[data-test=session-row-delete]').text()).toContain('确认删除')

    await wrapper.find('[data-test=session-row-menu]').trigger('click')
    expect(wrapper.findAll('.row-menu')).toHaveLength(0)

    await openRowMenu(wrapper, 0)
    expect(wrapper.find('[data-test=session-row-delete]').text()).not.toContain('确认')
    expect(wrapper.emitted('remove')).toBeUndefined()
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

  it('keeps a thinking row out of the way in results-only mode', () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        resultsOnly: true,
        items: [
          { ...baseItem, seq: 1, body: '问' },
          { ...baseItem, seq: 2, kind: 'Thinking', body: '想' },
          { ...baseItem, seq: 3, kind: 'Assistant', body: '答' },
        ],
      },
    })

    expect(wrapper.find('[data-test="session-thinking"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('问')
    expect(wrapper.text()).toContain('答')
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
      input: { enabled: false, deviceId: null, silenceThreshold: 0.03, utteranceGapMs: 1200, wakeTimeoutMs: 5000, sentenceGapMs: 450, windowSeconds: 8, asrStream: false, maxSplitChars: 40, wakeTone: true },
      output: { enabled: false, deviceId: null, voice: null, speed: 1, gapMs: 0, drainMs: 400, sampleRate: 24000, muteDuringTts: true },
      asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
      tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
      local: { tts: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] }, kws: { enabled: false, command: null, arguments: [], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] } },
      model: { baseUrl: null, model: null, apiKeySet: false, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: ['off', 'low', 'medium', 'high'] },
      desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] },
      mcp: { enabled: false, servers: [], allowedTools: [] },
      skills: { disabled: [] },
    })),
    skills: vi.fn(async () => ({ skills: [], warnings: [] })),
    saveSettings: vi.fn(async (patch: RuntimeSettings) => ({
      settings: { ...(await stubApi().settings()), ...patch, model: { ...patch.model, apiKeySet: false } },
      consumer: { running: false, reason: '未配置模型', model: null, stats: { completed: 0, failed: 0, canceled: 0, skipped: 0, handled: 0 }, tools: [], },
      voice: { input: { running: false, reason: '未启用', device: null, count: 0, rms: 0, ambientRms: 0, thresholdMisconfigured: false, awake: false }, output: { running: false, reason: '未启用', device: null, count: 0 } },
      compaction: null,
    })),
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
  it('reads the queue out without pretending it leads anywhere', async () => {
    // It was a button once, pointing at a queue page. That page is gone: the queue is the composer's own list,
    // one click from where a reader already stands. A control that cannot act is worse than an absent one, so
    // the badge is a readout — and it is the announced form that still says a failure out loud.
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
    expect(badge.text()).toContain('队列 1')
    expect(badge.text()).toContain('失败 1')
    expect(badge.text()).toContain('执行中 q-0184')
    // The failure is still said, in the channel a screen reader hears.
    expect(wrapper.find('[data-test="blocked-announcer"]').text()).toContain('失败')
  })

  it('drops the failure count once the queue no longer has one to report', async () => {
    // The queue keeps only what is still in play, so a settled failure is no longer counted here — it is read
    // in the conversation, where the failed turn wrote an error event. The badge must not claim zero means
    // "nothing went wrong", so it simply stops showing a number it no longer has.
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

    const badge = wrapper.find('[data-test="blocked"]')
    expect(badge.text()).not.toContain('失败')
    expect(badge.classes()).not.toContain('badge-error')
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

    const picker = wrapper.find<HTMLSelectElement>('[data-test="composer-gear"]')
    expect(picker.element.value).toBe('medium')
    expect(picker.findAll('option').map((node) => node.attributes('value'))).toEqual(['off', 'low', 'medium', 'high'])

    await picker.setValue('high')
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

    await wrapper.find<HTMLSelectElement>('[data-test="composer-gear"]').setValue('high')
    await wrapper.setProps({ gears: ['low', 'medium', 'high'], defaultGear: 'low' })

    expect(wrapper.find<HTMLSelectElement>('[data-test="composer-gear"]').element.value).toBe('low')
  })

  it('still offers a default that is not on the menu rather than silently sending something else', async () => {
    // The list is a menu, not a constraint: a hand-edited settings file can hold a default the editor does
    // not list, and dropping it from the picker would send a gear nobody chose.
    const api = stubApi()
    const wrapper = mount(Composer, {
      props: { api, gears: ['low', 'high'], defaultGear: 'turbo' },
    })
    await flushPromises()

    expect(wrapper.find<HTMLSelectElement>('[data-test="composer-gear"]').element.value).toBe('turbo')
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

  it('unfolds the queue behind the badge, in the order the queue runs', async () => {
    const items: QueueItem[] = [
      { itemId: 'q3', seq: 3, source: 'Text', status: 'Failed', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 1, error: '端点拒绝', payload: 'third' },
      { itemId: 'q2', seq: 2, source: 'Text', status: 'Queued', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'second' },
      { itemId: 'q1', seq: 1, source: 'Text', status: 'Running', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'first' },
    ]
    const summary = summarizeQueue({ headSeq: 3, runningItemId: 'q1', depth: 2, items })
    const wrapper = mount(Composer, { props: { api: stubApi(), summary, queueItems: items } })

    expect(wrapper.find('[data-test="composer-queue-list"]').exists()).toBe(false)
    await wrapper.find('[data-test="composer-queue-toggle"]').trigger('click')

    const rows = wrapper.findAll('[data-test="composer-queue-item"]')
    expect(rows.map((row) => row.attributes('data-status'))).toEqual(['Running', 'Queued', 'Failed'])
    // "失败 1" says something is wrong and nothing about what; the row has to carry the reason.
    expect(rows[2]?.text()).toContain('端点拒绝')
  })

  it('answers /status with the list and never delivers it as a message', async () => {
    const items: QueueItem[] = [
      { itemId: 'q1', seq: 1, source: 'Text', status: 'Running', enqueuedAt: '', startedAt: null, finishedAt: null, attempt: 0, error: null, payload: 'working' },
    ]
    const api = stubApi()
    const wrapper = mount(Composer, { props: { api, summary: summarizeQueue({ headSeq: 1, runningItemId: 'q1', depth: 1, items }), queueItems: items } })

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('/status')
    await wrapper.find('[data-test="composer-input"]').trigger('keydown.enter')
    await flushPromises()

    expect(api.submitInput).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test="composer-queue-list"]').exists()).toBe(true)
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

  it('announces its own two-step confirmation', async () => {
    const wrapper = mount(Inspector, {
      props: { testPrefix: 'session', title: 't', target: '#1', draft: 'x', revisions: [] },
    })

    const announcer = () => wrapper.find('[data-test="inspector-arm-announcer"]')
    expect(announcer().text()).toBe('')

    await wrapper.find('[data-test="session-delete"]').trigger('click')
    expect(announcer().text()).toContain('再次按下确认')

    await wrapper.find('[data-test="session-delete-cancel"]').trigger('click')
    expect(announcer().text()).toBe('')
  })

  it('offers a resize handle only where resizing would mean something', () => {
    // A panel floating over the list has nothing beside it to take width from. Leaving a handle there would be
    // a control that cannot act — the same defect the sidebar toggle had for years.
    const column = mount(Inspector, {
      props: { testPrefix: 'session', title: 't', target: '', draft: '', revisions: [], resizable: true },
    })
    expect(column.find('[data-test="inspector-resizer"]').exists()).toBe(true)

    const overlay = mount(Inspector, {
      props: { testPrefix: 'session', title: 't', target: '', draft: '', revisions: [], resizable: false },
    })
    expect(overlay.find('[data-test="inspector-resizer"]').exists()).toBe(false)
  })

  it('resizes from the keyboard, in steps, inside the bounds it declares', async () => {
    const wrapper = mount(Inspector, {
      props: { testPrefix: 'session', title: 't', target: '', draft: '', revisions: [], resizable: true },
    })

    const handle = wrapper.find('[data-test="inspector-resizer"]')
    expect(handle.attributes('role')).toBe('separator')
    expect(handle.attributes('aria-orientation')).toBe('vertical')
    expect(handle.attributes('aria-valuemin')).toBe('16')
    expect(handle.attributes('aria-valuemax')).toBe('26')
    // The value is only meaningful once the reader has set one; before that, the clamp in styles.css owns it.
    expect(handle.attributes('aria-valuenow')).toBeUndefined()

    // Arrow keys are the only way a reader without a pointer resizes anything, so they move it — and they move
    // the panel's *track*, which is what the reader is dragging. On a panel docked to the right, ArrowLeft
    // widens it (its left edge travels left) and ArrowRight narrows it.
    await handle.trigger('keydown', { key: 'ArrowLeft' })
    expect(handle.attributes('aria-valuenow')).toBe('21')

    await handle.trigger('keydown', { key: 'ArrowRight', shiftKey: true })
    expect(handle.attributes('aria-valuenow')).toBe('17')

    // Both ends clamp. Without this a reader could hold an arrow key into a width that hides the list, or one
    // so narrow the revision rows stop being readable.
    for (let i = 0; i < 20; i++) await handle.trigger('keydown', { key: 'ArrowRight', shiftKey: true })
    expect(wrapper.find('[data-test="inspector-resizer"]').attributes('aria-valuenow')).toBe('16')

    for (let i = 0; i < 20; i++) await handle.trigger('keydown', { key: 'ArrowLeft', shiftKey: true })
    expect(wrapper.find('[data-test="inspector-resizer"]').attributes('aria-valuenow')).toBe('26')

    // Anything else is not a resize and must not move it.
    await handle.trigger('keydown', { key: 'Enter' })
    expect(wrapper.find('[data-test="inspector-resizer"]').attributes('aria-valuenow')).toBe('26')
  })
})

describe('status bar', () => {
  it('names the model when the queue is being executed', () => {
    const wrapper = mount(StatusBar, {
      props: {
        connected: true,
        connectionError: '',
        endpoint: 'http://127.0.0.1:5280',
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
    expect(healthy.find('[data-test="status-announcer"]').text()).toBe('')

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
  it('reports a delivery as queued rather than as a new session row', async () => {
    const api = stubApi()
    const wrapper = mount(SessionView, { props: { api } })
    await flushPromises()

    await wrapper.find<HTMLTextAreaElement>('[data-test="composer-input"]').setValue('hello')
    await wrapper.find('[data-test="composer-send"]').trigger('click')
    await flushPromises()

    expect(api.submitInput).toHaveBeenCalledWith('hello', 'text', { media: [], reasoningEffort: '' })

    // "已入队 · 等待执行" is the whole truth here. The queue is strict FIFO, so every message waits its turn;
    // wording that claimed a message jumped ahead would be promising something the runtime does not do.
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('已入队')
    expect(wrapper.find('[data-test="session-notice"]').text()).toContain('等待执行')
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
