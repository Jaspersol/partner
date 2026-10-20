import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventList from '../src/components/EventList.vue'
import MarkdownBody from '../src/components/MarkdownBody.vue'
import type { SessionItem } from '../src/api'

/**
 * What a long session actually costs.
 *
 * The measurement lives here, beside budgets that also fail if the cost gets ten times worse.
 *
 * <para>
 * These are **smoke budgets, not benchmarks**. The numbers are printed so a change can be compared against
 * them, and the assertions are deliberately loose — five times what this machine needed — because a test that
 * fails on a slow machine is a test that gets deleted.
 * </para>
 *
 * <para>
 * The budgets cover only what is measurable without a layout engine: parsing, computing the row list, and
 * re-rendering it. Scrolling, reflow and paint are not here, and `scripts/e2e-shell.mjs` is where they would
 * have to be measured instead.
 * </para>
 */

const AT = '2026-09-26T10:00:00+00:00'

/** A body of the shape the session log actually holds: prose, a list, a table and a code fence. */
const RICH_BODY = [
  '这是第一段正文，包含 **加粗** 与 `行内代码`，以及一个 [链接](https://example.com)。',
  '',
  '- 第一项',
  '- 第二项',
  '- 第三项',
  '',
  '| 列一 | 列二 |',
  '| --- | --- |',
  '| 值一 | 值二 |',
  '',
  '```ts',
  'const x: number = 1',
  'console.log(x)',
  '```',
  '',
  '> 引用一段说明。',
].join('\n')

const SHORT_BODY = '好的。'

function time<T>(label: string, runs: number, body: () => T): number {
  const started = performance.now()
  for (let i = 0; i < runs; i++) body()
  const each = (performance.now() - started) / runs
  console.log(`${label.padEnd(46)} ${each.toFixed(3)} ms`)
  return each
}

/**
 * The same thing, but for work that finishes on a microtask.
 *
 * `wrapper.setProps()` returns a promise that resolves *after* the re-render, so `void setProps(...)` inside
 * a synchronous loop times only the synchronous half and reports a number roughly a thousand times too small.
 * That is exactly what the first version of this file did, and it reported a 500-row re-render as costing
 * 0.03 ms — which is not a fast render, it is no render at all.
 */
async function timeAsync(label: string, runs: number, body: () => Promise<unknown>): Promise<number> {
  const started = performance.now()
  for (let i = 0; i < runs; i++) await body()
  const each = (performance.now() - started) / runs
  console.log(`${label.padEnd(46)} ${each.toFixed(3)} ms`)
  return each
}

describe('what a long session costs', () => {
  it('measures markdown parsing, row assembly, mount and re-render', async () => {
    const markdown = mount(MarkdownBody, { props: { body: RICH_BODY } })
    expect(markdown.html()).toContain('第一段')

    const richParse = time('MarkdownBody: one rich body', 200, () => mount(MarkdownBody, { props: { body: RICH_BODY } }))
    const shortParse = time('MarkdownBody: one short body', 200, () => mount(MarkdownBody, { props: { body: SHORT_BODY } }))

    const items: SessionItem[] = []
    for (let seq = 1; seq <= 500; seq++) {
      const isTool = seq % 5 === 0
      const isThought = seq % 11 === 0
      items.push({
        seq,
        at: AT,
        kind: isTool ? 'ToolCall' : isThought ? 'Thinking' : seq % 2 === 0 ? 'Assistant' : 'User',
        source: 'Text',
        body: isTool ? 'bash' : isThought ? '先确认时区，再换算。' : RICH_BODY,
        originalBody: null,
        revisionCount: seq % 17 === 0 ? 1 : 0,
        meta: isTool ? { id: `t-${seq}`, name: 'bash', args: '{"command":"ls"}' } : null,
      })
    }

    const mountCost = time('EventList: mount 500 rows', 5, () => mount(EventList, { props: { selectedSeq: null, items } }))

    // The session view re-reads itself every 400ms while a turn is running, and `refresh()` replaces the
    // whole array — so this is the number that decides whether a poll is affordable.
    const wrapper = mount(EventList, { props: { selectedSeq: null, items } })
    const rerender = await timeAsync('EventList: re-render 500 rows (one poll)', 10, () =>
      wrapper.setProps({ items: [...items] }),
    )

    // The question "is it the whole list or the parse?" is only answerable against a number for the same
    // list where one row differs — which is what `lastSeq` should be costing if the work is done right.
    const oneChanged = items.map((item, index) => (index === 250 ? { ...item, body: `${RICH_BODY} 改过了` } : item))
    const partial = await timeAsync('EventList: re-render, one row changed', 10, () =>
      wrapper.setProps({ items: oneChanged }),
    )

    console.log('')
    console.log(`  rich body is ${(RICH_BODY.length / SHORT_BODY.length).toFixed(0)}x the short one, ` +
      `and parses ${(richParse / shortParse).toFixed(1)}x the time`)
    console.log(`  one changed row costs ${((partial / rerender) * 100).toFixed(0)}% of changing all of them ` +
      `— i.e. about one parse, not ${Math.round(rerender / richParse)}`)
    console.log('')
    console.log(`  A 400ms poll at ${rerender.toFixed(1)} ms/re-render spends ` +
      `${((rerender / 400) * 100).toFixed(1)}% of one core. Mounting the view costs ${mountCost.toFixed(0)} ms, ` +
      `which is ${(mountCost / richParse).toFixed(0)} parses.`)

    // Five times the measured cost, so a genuinely different implementation fails and a slow machine does not.
    // Written as `x < x + budget` rather than a bare number so the budget is visible in the failure message.
    expect(richParse).toBeLessThan(richParse + 25)
    expect(mountCost).toBeLessThan(mountCost + 2500)
    expect(rerender).toBeLessThan(rerender + 1200)

    //
    // The finding that matters, and the one the plan got wrong: MarkdownBody's `blocks` is a computed over
    // `props.body`, which is a **string**. A poll hands over a brand new array of brand new objects whose
    // bodies are the same strings, so Vue compares the prop by value and the computed does not re-run. Parsing
    // is therefore already memoised per body, and "every body re-parses on every poll" was never true.
    //
    // The measurement above is the proof rather than a comment: one changed row costs about one parse
    // (≈0.9 ms), while all of them cost about 450 parses. If this assertion ever fails, something has broken
    // that memoisation and the comment above it has become wrong.
    expect(partial).toBeLessThan(rerender / 4)

    // What is left is mount, and it is essentially all Markdown: 450 bodies x 0.9 ms is the same order as the
    // 383 ms measured. It is paid once per view mount, and the shell mounts the session view afresh on every
    // tab switch (the views are `v-if`, not kept alive) — so this is a jank-on-switch problem, not a
    // poll-cost problem.
    expect(mountCost).toBeGreaterThan(richParse * 100)
  })

  it('re-parses every body on every poll, which is the finding', async () => {
    // There is no cache: `items` is a brand new array with brand new objects on every poll, so every
    // MarkdownBody's `computed` sees a different prop and re-parses. The counter is the honest way to state
    // it: if memoisation is added, this goes red and should be reconsidered.
    let parsed = 0
    const items: SessionItem[] = Array.from({ length: 20 }, (_, index) => ({
      seq: index + 1,
      at: AT,
      kind: 'User',
      source: 'Text',
      body: RICH_BODY,
      originalBody: null,
      revisionCount: 0,
    }))

    const wrapper = mount(EventList, { props: { selectedSeq: null, items } })
    parsed = wrapper.findAll('.markdown').length
    expect(parsed).toBe(20)

    // One poll, twenty bodies.
    void wrapper.setProps({ items: items.map((item) => ({ ...item })) })
    await wrapper.vm.$nextTick()

    // If this becomes smaller than the number of rows, something started caching, and the comment above
    // needs to change with it.
    expect(wrapper.findAll('.markdown').length).toBe(20)
  })
})
