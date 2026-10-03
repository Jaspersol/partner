import { mount } from '@vue/test-utils'
import EventList from '../src/components/EventList.vue'
import { describe, expect, it } from 'vitest'
import { clampIndex, isActivationKey, normalizeKey, rovingTarget } from '../src/rovingFocus'

/**
 * `rovingFocus` is the arithmetic of "which row does this key mean". It is a pure function precisely so that
 * this can be exhaustive without a layout engine — the same reason `scripts/e2e-shell.mjs` exists for the
 * things that cannot be covered here.
 */
describe('roving focus', () => {
  it('moves one row at a time and stops at both ends', () => {
    expect(rovingTarget(0, 5, 'ArrowDown')).toBe(1)
    expect(rovingTarget(3, 5, 'ArrowDown')).toBe(4)
    // Clamps rather than wrapping: a reader who has reached the newest event and presses Down again is asking
    // whether anything is below, not asking for the oldest one in a five-hundred-row conversation.
    expect(rovingTarget(4, 5, 'ArrowDown')).toBe(4)
    expect(rovingTarget(1, 5, 'ArrowUp')).toBe(0)
    expect(rovingTarget(0, 5, 'ArrowUp')).toBe(0)
  })

  it('jumps to the two ends', () => {
    expect(rovingTarget(3, 5, 'Home')).toBe(0)
    expect(rovingTarget(3, 5, 'End')).toBe(4)
    // …and from there, still clamps rather than falling off.
    expect(rovingTarget(4, 5, 'End')).toBe(4)
  })

  it('pages ten rows at a time by default', () => {
    expect(rovingTarget(0, 100, 'PageDown')).toBe(10)
    expect(rovingTarget(95, 100, 'PageDown')).toBe(99)
    expect(rovingTarget(20, 100, 'PageUp')).toBe(10)
  })

  it('wraps on a horizontal rail, because a rail has no "below"', () => {
    // Every tab bar wraps: overshooting six items should bring the reader back round, not stop them dead.
    expect(rovingTarget(5, 6, 'ArrowRight', { axis: 'horizontal' })).toBe(0)
    expect(rovingTarget(0, 6, 'ArrowLeft', { axis: 'horizontal' })).toBe(5)
    // …while the same keys on a vertical list mean "there is nothing there".
    expect(rovingTarget(5, 6, 'ArrowDown', { axis: 'vertical' })).toBe(5)
    expect(rovingTarget(0, 6, 'ArrowUp', { axis: 'vertical' })).toBe(0)
  })

  it('leaves every other key alone', () => {
    // Returning null is what lets the caller tell "not mine" from "moved to zero", and it is what stops this
    // from swallowing Tab, typing, or the browser's own shortcuts.
    for (const key of ['Enter', ' ', 'a', 'Tab', 'F5', 'Home ', 'arrowdown ']) {
      expect(rovingTarget(1, 5, key)).toBeNull()
    }
  })

  it('says nothing when there is nothing to move through', () => {
    expect(rovingTarget(0, 0, 'ArrowDown')).toBeNull()
    expect(rovingTarget(0, 1, 'ArrowDown')).toBe(0)
  })

  it('does not care whether the key is spelled the way a browser or a test spells it', () => {
    // Browsers send `Enter`, `Escape`, `' '`. `@vue/test-utils`'s `trigger('keydown.enter')` sends `enter`.
    // Vue's own `.enter` template modifier hid that by comparing hyphenated forms; now that the handler is
    // ours, absorbing it is ours to do — and forgetting once would have meant "Enter stopped working in tests".
    expect(normalizeKey('ArrowDown')).toBe(normalizeKey('arrowdown'))
    for (const spelling of ['Enter', 'enter', 'ENTER']) {
      expect(isActivationKey(spelling)).toBe(true)
    }
    for (const spelling of [' ', 'space', 'Spacebar']) {
      expect(isActivationKey(spelling)).toBe(true)
    }
    expect(isActivationKey('ArrowDown')).toBe(false)
    expect(isActivationKey('Tab')).toBe(false)
  })

  it('follows a list that grew underneath the reader', () => {
    // The session log grows while it is being read. Without this, one new event at the bottom would push the
    // focused row off the end and drop the reader back at the top of the history.
    expect(clampIndex(5, 6)).toBe(5)
    expect(clampIndex(5, 3)).toBe(2)
    expect(clampIndex(-1, 5)).toBe(0)
    expect(clampIndex(2, 0)).toBe(0)
  })
})

describe('a roved list in the real components', () => {
  it('is a single tab stop, whatever its length', async () => {
    // The defect: every row carried `tabindex="0"`, so a keyboard reader pressed Tab once per event to reach
    // the composer — five hundred presses on a list that is still growing while they read it.
    const items = Array.from({ length: 50 }, (_, index) => ({
      seq: index + 1,
      at: '2026-09-26T10:00:00+00:00',
      kind: 'User',
      source: 'Text',
      body: `message ${index + 1}`,
      originalBody: null,
      revisionCount: 0,
    }))

    const wrapper = mount(EventList, { props: { selectedSeq: null, items } })

    const tabbable = wrapper.findAll('li').filter((row) => row.attributes('tabindex') === '0')
    expect(tabbable).toHaveLength(1)
  })

  it('moves the tab stop with the arrows rather than leaving it behind', async () => {
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', body: 'one', originalBody: null, revisionCount: 0 },
          { seq: 2, at: '2026-09-26T10:00:01+00:00', kind: 'User', source: 'Text', body: 'two', originalBody: null, revisionCount: 0 },
          { seq: 3, at: '2026-09-26T10:00:02+00:00', kind: 'User', source: 'Text', body: 'three', originalBody: null, revisionCount: 0 },
        ],
      },
    })

    const indexOfTabbable = () =>
      wrapper.findAll('li').findIndex((row) => row.attributes('tabindex') === '0')

    expect(indexOfTabbable()).toBe(0)
    await wrapper.findAll('li')[0]?.trigger('keydown', { key: 'ArrowDown' })
    expect(indexOfTabbable()).toBe(1)
    await wrapper.findAll('li')[1]?.trigger('keydown', { key: 'End' })
    expect(indexOfTabbable()).toBe(2)
    await wrapper.findAll('li')[2]?.trigger('keydown', { key: 'ArrowDown' })
    expect(indexOfTabbable()).toBe(2)
  })

  it('does not lose the reader when the log grows under the focused row', async () => {
    const base = { at: '2026-09-26T10:00:00+00:00', kind: 'User', source: 'Text', originalBody: null, revisionCount: 0 }
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          { ...base, seq: 1, body: 'one' },
          { ...base, seq: 2, body: 'two' },
        ],
      },
    })

    await wrapper.findAll('li')[1]?.trigger('keydown', { key: 'ArrowDown' })
    await wrapper.setProps({
      items: [
        { ...base, seq: 1, body: 'one' },
        { ...base, seq: 2, body: 'two' },
        { ...base, seq: 3, body: 'three' },
      ],
    })
    await wrapper.vm.$nextTick()

    // Still the second row, not thrown back to the first by an event arriving.
    expect(wrapper.findAll('li').findIndex((row) => row.attributes('tabindex') === '0')).toBe(1)
  })

  it('does not claim to be a listbox of choices when two thirds of it cannot be chosen', () => {
    // A tool card and a stretch of reasoning are part of the conversation but are not things one selects:
    // clicking them does nothing and they have no sequence to edit. As `role="undefined"` children of a
    // listbox that is invalid markup, and a screen reader renders it as an unpredictable number of unnamed items.
    const wrapper = mount(EventList, {
      props: {
        selectedSeq: null,
        items: [
          {
            seq: 1,
            at: '2026-09-26T10:00:00+00:00',
            kind: 'ToolCall',
            source: 'text',
            body: 'bash',
            originalBody: null,
            revisionCount: 0,
            meta: { id: 'a', name: 'bash', args: '{}' },
          },
          {
            seq: 2,
            at: '2026-09-26T10:00:01+00:00',
            kind: 'Thinking',
            source: 'text',
            body: 'hmm',
            originalBody: null,
            revisionCount: 0,
          },
        ],
      },
    })

    expect(wrapper.find('[data-test="session-list"]').attributes('role')).toBe('list')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(0)
  })
})

