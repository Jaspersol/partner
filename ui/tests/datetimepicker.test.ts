import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import DateTimePicker from '../src/components/DateTimePicker.vue'

/**
 * Now is fixed at Tuesday 2030-01-15 10:03:30 local time, so "today", "the past" and "the first minute still
 * ahead" (10:04) are the same on every machine on every day.
 */
const NOW = new Date(2030, 0, 15, 10, 3, 30)

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(NOW)
})

afterEach(() => {
  vi.useRealTimers()
})

async function open(initial = '') {
  const wrapper = mount(DateTimePicker, {
    attachTo: document.body,
    props: {
      modelValue: initial,
      'onUpdate:modelValue': (value: string) => wrapper.setProps({ modelValue: value }),
    },
    attrs: { 'data-test': 'picker' },
  })
  await wrapper.find('[data-test="picker"]').trigger('click')
  return wrapper
}

const value = (wrapper: Awaited<ReturnType<typeof open>>) => wrapper.props('modelValue') as string

describe('date-time picker', () => {
  it('asks for a choice until one is made, and forwards the form\'s own attributes to the button', async () => {
    const wrapper = mount(DateTimePicker, { props: { modelValue: '' }, attrs: { 'data-test': 'picker' } })

    expect(wrapper.find('[data-test="picker"]').text()).toBe('选择日期和时间')
    expect(wrapper.find('[data-test="datetime-panel"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('opens on the current month with the past disabled and today marked', async () => {
    const wrapper = await open()

    expect(wrapper.find('[data-test="datetime-month"]').text()).toBe('2030年1月')
    // Jan 1st 2030 is a Tuesday and weeks start on Monday: one blank cell before it.
    expect(wrapper.findAll('.dtp-blank')).toHaveLength(1)

    expect((wrapper.find('[data-test="datetime-day-2030-01-14"]').element as HTMLButtonElement).disabled).toBe(true)
    const today = wrapper.find('[data-test="datetime-day-2030-01-15"]')
    expect((today.element as HTMLButtonElement).disabled).toBe(false)
    expect(today.attributes('aria-current')).toBe('date')
    expect((wrapper.find('[data-test="datetime-day-2030-01-16"]').element as HTMLButtonElement).disabled).toBe(false)
    wrapper.unmount()
  })

  it('a day with no time yet starts at 09:00 and the trigger names the whole moment', async () => {
    const wrapper = await open()

    await wrapper.find('[data-test="datetime-day-2030-01-20"]').trigger('click')

    expect(value(wrapper)).toBe('2030-01-20T09:00')
    expect(wrapper.find('[data-test="picker"]').text()).toContain('2030年1月20日 周日 09:00')
    wrapper.unmount()
  })

  it('today at a time that has already passed becomes the first minute still ahead', async () => {
    const wrapper = await open()

    await wrapper.find('[data-test="datetime-day-2030-01-15"]').trigger('click')

    // 09:00 is behind us; 10:03:30 means 10:04 is the first minute that is not.
    expect(value(wrapper)).toBe('2030-01-15T10:04')
    wrapper.unmount()
  })

  it('on today the hours and minutes that are behind us cannot be picked, and on another day they can', async () => {
    const wrapper = await open('2030-01-15T10:10')
    const disabled = (name: string) => (wrapper.find(`[data-test="${name}"]`).element as HTMLButtonElement).disabled

    expect(disabled('datetime-hour-9')).toBe(true)
    expect(disabled('datetime-hour-10')).toBe(false)
    // Still in the 10 o'clock hour: 10:00 is gone, 10:05 is not.
    expect(disabled('datetime-minute-0')).toBe(true)
    expect(disabled('datetime-minute-5')).toBe(false)

    await wrapper.find('[data-test="datetime-day-2030-01-16"]').trigger('click')
    expect(value(wrapper)).toBe('2030-01-16T10:10')
    expect(disabled('datetime-hour-9')).toBe(false)
    expect(disabled('datetime-minute-0')).toBe(false)
    wrapper.unmount()
  })

  it('picking an hour with no day yet means today, and an hour that moves a time into the past is lifted', async () => {
    const wrapper = await open()

    await wrapper.find('[data-test="datetime-hour-14"]').trigger('click')
    expect(value(wrapper)).toBe('2030-01-15T14:00')

    await wrapper.find('[data-test="datetime-minute-30"]').trigger('click')
    expect(value(wrapper)).toBe('2030-01-15T14:30')
    wrapper.unmount()
  })

  it('shortcuts: an hour from now, tomorrow morning, next Monday', async () => {
    const wrapper = await open()

    await wrapper.find('[data-test="datetime-preset-hour"]').trigger('click')
    expect(value(wrapper)).toBe('2030-01-15T11:03')
    // 11:03 is not on the five-minute grid, so the column grows a row for it rather than hiding the choice.
    expect(wrapper.find('[data-test="datetime-minute-3"]').attributes('aria-pressed')).toBe('true')

    await wrapper.find('[data-test="datetime-preset-tomorrow"]').trigger('click')
    expect(value(wrapper)).toBe('2030-01-16T09:00')

    // The 15th is a Tuesday, so the next Monday is the 21st.
    await wrapper.find('[data-test="datetime-preset-monday"]').trigger('click')
    expect(value(wrapper)).toBe('2030-01-21T09:00')
    wrapper.unmount()
  })

  it('moves between months but never back before this one', async () => {
    const wrapper = await open()
    const previous = () => wrapper.find('[data-test="datetime-prev"]').element as HTMLButtonElement

    expect(previous().disabled).toBe(true)

    await wrapper.find('[data-test="datetime-next"]').trigger('click')
    expect(wrapper.find('[data-test="datetime-month"]').text()).toBe('2030年2月')
    expect(previous().disabled).toBe(false)

    await wrapper.find('[data-test="datetime-day-2030-02-03"]').trigger('click')
    expect(value(wrapper)).toBe('2030-02-03T09:00')

    await previous().click()
    await flushPromises()
    expect(wrapper.find('[data-test="datetime-month"]').text()).toBe('2030年1月')
    wrapper.unmount()
  })

  it('opens on the month of an existing choice and marks the chosen day', async () => {
    const wrapper = await open('2030-03-09T18:45')

    expect(wrapper.find('[data-test="datetime-month"]').text()).toBe('2030年3月')
    expect(wrapper.find('[data-test="datetime-day-2030-03-09"]').attributes('aria-pressed')).toBe('true')
    expect(wrapper.find('[data-test="datetime-hour-18"]').attributes('aria-pressed')).toBe('true')
    expect(wrapper.find('[data-test="datetime-minute-45"]').attributes('aria-pressed')).toBe('true')
    wrapper.unmount()
  })

  it('done is off until something is chosen, and closes the panel', async () => {
    const wrapper = await open()
    const done = () => wrapper.find('[data-test="datetime-done"]').element as HTMLButtonElement

    expect(done().disabled).toBe(true)

    await wrapper.find('[data-test="datetime-day-2030-01-20"]').trigger('click')
    expect(done().disabled).toBe(false)

    await wrapper.find('[data-test="datetime-done"]').trigger('click')
    expect(wrapper.find('[data-test="datetime-panel"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('closes on Escape and on a press outside, and keeps the choice', async () => {
    const wrapper = await open('2030-01-20T09:00')

    await wrapper.find('[data-test="datetime-panel"]').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[data-test="datetime-panel"]').exists()).toBe(false)

    await wrapper.find('[data-test="picker"]').trigger('click')
    expect(wrapper.find('[data-test="datetime-panel"]').exists()).toBe(true)

    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    await flushPromises()
    expect(wrapper.find('[data-test="datetime-panel"]').exists()).toBe(false)
    expect(value(wrapper)).toBe('2030-01-20T09:00')
    wrapper.unmount()
  })

  it('arrow keys walk the grid, and never into the past', async () => {
    const wrapper = await open('2030-01-16T09:00')
    const day = (key: string) => wrapper.find(`[data-test="datetime-day-${key}"]`)

    // Left from the 16th is the 15th (today): allowed. Left again would be the 14th: refused.
    await day('2030-01-16').trigger('keydown', { key: 'ArrowLeft' })
    await flushPromises()
    expect(document.activeElement?.getAttribute('data-day')).toBe('2030-01-15')

    await day('2030-01-15').trigger('keydown', { key: 'ArrowLeft' })
    await flushPromises()
    expect(document.activeElement?.getAttribute('data-day')).toBe('2030-01-15')

    // Down is a week: the 15th → the 22nd.
    await day('2030-01-15').trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()
    expect(document.activeElement?.getAttribute('data-day')).toBe('2030-01-22')
    wrapper.unmount()
  })
})
