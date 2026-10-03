import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ThinkingRow from '../src/components/ThinkingRow.vue'
import { sharedTickSubscribers } from '../src/clock'

/**
 * One clock for the whole app (P4 #30).
 *
 * <para>
 * The two consumers each used to own a one-second interval, so a turn that thought in three stretches while a
 * queue item ran had four timers firing in the same millisecond to display the same second count several times
 * over. The shared clock is started by the first caller and stopped by the last.
 * </para>
 *
 * <para>
 * `vi.getTimerCount()` is the assertion that matters: it counts timers rather than reasoning about them, so it
 * stays true if the implementation is rewritten.
 * </para>
 */

const startedAt = new Date().toISOString()

describe('the shared clock', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('runs one timer for two consumers, and stops when neither wants it', () => {
    vi.useFakeTimers()

    // `vi.getTimerCount()` counts everything in the process, and it is not a clean measure here: mounting a
    // plain component with no timers of its own already takes the count from 0 to 1 and leaves it there after
    // unmount. So the absolute number measures Vue, and only the *delta* says anything about this module —
    // adding a second consumer must not add a timer. The exact figure that is ours is the subscriber count.
    const first = mount(ThinkingRow, { props: { body: '第一段', running: true, since: startedAt } })
    const afterFirst = vi.getTimerCount()
    expect(sharedTickSubscribers()).toBe(1)
    // Starting the clock is the one timer this mount added on top of the harness's own.
    expect(afterFirst).toBeGreaterThan(0)

    // A second stretch of reasoning. The event log renders every one of them at once, so this is the ordinary
    // case, not a corner: before the change each row ran its own interval, all firing in the same millisecond
    // to display the same second count.
    const second = mount(ThinkingRow, { props: { body: '第二段', running: true, since: startedAt } })

    expect(vi.getTimerCount()).toBe(afterFirst)
    expect(sharedTickSubscribers()).toBe(2)

    first.unmount()
    expect(vi.getTimerCount()).toBe(afterFirst)

    second.unmount()
    // Last one out turns it off: a component that unmounts mid-render cannot leave a timer behind, because the
    // timer was never its. Subscribers is the figure this module owns; the timer count cannot be compared to
    // the baseline, because the harness's own timer is in there too.
    expect(sharedTickSubscribers()).toBe(0)
  })

  it('advances both of them from the same tick', () => {
    vi.useFakeTimers()

    const first = mount(ThinkingRow, { props: { body: '第一段', running: true, since: startedAt } })
    const second = mount(ThinkingRow, { props: { body: '第二段', running: true, since: startedAt } })

    vi.advanceTimersByTime(3000)
    // Both read the same clock, so they cannot drift apart — which is the other half of why they share one.
    expect(first.text()).toContain('已思考 00:00')
    expect(second.text()).toContain('已思考 00:00')
  })

  it('shows nothing once the work is over, rather than a frozen number', async () => {
    vi.useFakeTimers()
    const wrapper = mount(ThinkingRow, { props: { body: '想完了', running: true, since: startedAt } })

    vi.advanceTimersByTime(5000)
    expect(wrapper.text()).toContain('已思考')

    // A finished row is in the log, not in progress. A clock left running over it would be reporting a duration
    // nobody is waiting for.
    await wrapper.setProps({ running: false })
    expect(wrapper.text()).not.toContain('已思考')
  })
})
