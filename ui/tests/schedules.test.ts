import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ApiError, type PartnerApiSurface, type ScheduleInput, type ScheduleTaskView } from '../src/api'
import SchedulesView from '../src/views/SchedulesView.vue'

/**
 * The timed-tasks page against a stub API. Only the five schedule methods matter here, so the stub is just those
 * cast to the full surface: the view cannot reach anything else, and a call to it would fail loudly.
 */
function task(over: Partial<ScheduleTaskView> = {}): ScheduleTaskView {
  return {
    id: 'sch-1',
    kind: 'cron',
    describe: 'cron 0 9 * * *',
    cron: '0 9 * * *',
    intervalMinutes: null,
    at: null,
    payload: '总结昨天的待办',
    status: 'active',
    nextAt: '2030-01-02T01:00:00Z',
    lastAt: null,
    attempt: 0,
    lastError: null,
    ...over,
  }
}

async function mountView(overrides: Partial<PartnerApiSurface> = {}) {
  const api = {
    schedules: vi.fn(async () => [] as ScheduleTaskView[]),
    createSchedule: vi.fn(async (input: ScheduleInput) => task({ payload: input.payload })),
    pauseSchedule: vi.fn(async () => task({ status: 'inactive' })),
    resumeSchedule: vi.fn(async () => task()),
    deleteSchedule: vi.fn(async () => true),
    ...overrides,
  } as unknown as PartnerApiSurface

  const wrapper = mount(SchedulesView, { props: { api } })
  await flushPromises()
  return { wrapper, api }
}

describe('schedules view', () => {
  it('says there is nothing yet only after the first read has answered', async () => {
    let release: (tasks: ScheduleTaskView[]) => void = () => {}
    const pending = new Promise<ScheduleTaskView[]>((resolve) => (release = resolve))
    const wrapper = mount(SchedulesView, {
      props: { api: { schedules: vi.fn(() => pending) } as unknown as PartnerApiSurface },
    })

    // Not known yet is a different answer from nothing: a confident "no tasks" here would be wrong.
    expect(wrapper.find('[data-test="schedule-empty"]').exists()).toBe(false)
    expect(wrapper.find('[data-test="schedule-pending"]').exists()).toBe(true)

    release([])
    await flushPromises()
    expect(wrapper.find('[data-test="schedule-empty"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('lists a task with its rule in words, its state and its times', async () => {
    const { wrapper } = await mountView({
      schedules: vi.fn(async () => [
        task(),
        task({ id: 'sch-2', kind: 'interval', cron: null, intervalMinutes: 120, payload: '喝水', status: 'inactive', nextAt: null }),
      ]),
    })

    const items = wrapper.findAll('[data-test="schedule-item"]')
    expect(items).toHaveLength(2)
    expect(items[0]?.find('[data-test="schedule-rule"]').text()).toBe('cron 0 9 * * *')
    expect(items[0]?.find('[data-test="schedule-state"]').text()).toBe('运行中')
    expect(items[0]?.find('[data-test="schedule-next"]').exists()).toBe(true)

    // 120 minutes reads as hours; a paused task has no "next".
    expect(items[1]?.find('[data-test="schedule-rule"]').text()).toBe('每 2 小时')
    expect(items[1]?.find('[data-test="schedule-state"]').text()).toBe('已暂停')
    expect(items[1]?.find('[data-test="schedule-next"]').exists()).toBe(false)
    expect(wrapper.find('[data-test="schedule-summary"]').text()).toBe('2 个任务 · 1 个在运行')
    wrapper.unmount()
  })

  it('a one-off that already ran is "已执行" and cannot be paused or resumed', async () => {
    const { wrapper } = await mountView({
      schedules: vi.fn(async () => [
        task({ kind: 'once', cron: null, at: '2020-01-01T00:00:00Z', status: 'inactive', nextAt: null, lastAt: '2020-01-01T00:00:00Z' }),
      ]),
    })

    expect(wrapper.find('[data-test="schedule-state"]').text()).toBe('已执行')
    expect(wrapper.find('[data-test="schedule-toggle"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('creates a cron task from the form, clears it, and re-reads the list', async () => {
    const schedules = vi.fn(async () => [] as ScheduleTaskView[])
    const { wrapper, api } = await mountView({ schedules })

    await wrapper.find('[data-test="schedule-example"]').trigger('click')
    await wrapper.find('[data-test="schedule-payload"]').setValue('  早报  ')
    await wrapper.find('[data-test="schedule-add"]').trigger('click')
    await flushPromises()

    expect(api.createSchedule).toHaveBeenCalledWith({ kind: 'cron', cron: '0 9 * * *', payload: '早报' })
    expect((wrapper.find('[data-test="schedule-payload"]').element as HTMLTextAreaElement).value).toBe('')
    expect(schedules).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('turns an interval and its unit into minutes', async () => {
    const { wrapper, api } = await mountView()

    await wrapper.find('[data-value="interval"]').trigger('click')
    await wrapper.find('[data-test="schedule-interval"]').setValue(3)
    await wrapper.find('[data-test="schedule-unit"]').setValue('hours')
    await wrapper.find('[data-test="schedule-payload"]').setValue('站起来走走')
    await wrapper.find('[data-test="schedule-add"]').trigger('click')
    await flushPromises()

    expect(api.createSchedule).toHaveBeenCalledWith({ kind: 'interval', intervalMinutes: 180, payload: '站起来走走' })
    wrapper.unmount()
  })

  it('sends a one-off time as an instant', async () => {
    const { wrapper, api } = await mountView()

    await wrapper.find('[data-value="once"]').trigger('click')
    await wrapper.find('[data-test="schedule-at"]').trigger('click')
    await wrapper.find('[data-test="datetime-preset-tomorrow"]').trigger('click')
    await wrapper.find('[data-test="datetime-done"]').trigger('click')
    await wrapper.find('[data-test="schedule-payload"]').setValue('开会')
    await wrapper.find('[data-test="schedule-add"]').trigger('click')
    await flushPromises()

    const sent = (api.createSchedule as ReturnType<typeof vi.fn>).mock.calls[0]?.[0] as ScheduleInput
    expect(sent.kind).toBe('once')
    // The picker holds local wall-clock time; the server gets the instant it means.
    const today = new Date()
    expect(sent.at).toBe(new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1, 9, 0).toISOString())
    wrapper.unmount()
  })

  it('refuses an empty message or an empty rule before asking the server', async () => {
    const { wrapper, api } = await mountView()

    await wrapper.find('[data-test="schedule-add"]').trigger('click')
    expect(wrapper.find('[data-test="schedule-form-error"]').text()).toContain('内容')

    await wrapper.find('[data-test="schedule-payload"]').setValue('x')
    await wrapper.find('[data-test="schedule-add"]').trigger('click')
    expect(wrapper.find('[data-test="schedule-form-error"]').text()).toContain('时刻')

    expect(api.createSchedule).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it("shows the server's own reason without the status prefix, and keeps what was typed", async () => {
    const createSchedule = vi.fn(async () => {
      throw new ApiError(400, 'cron 表达式不对：需要 5 段')
    })
    const { wrapper } = await mountView({ createSchedule })

    await wrapper.find('[data-test="schedule-cron"]').setValue('bad')
    await wrapper.find('[data-test="schedule-payload"]').setValue('留着')
    await wrapper.find('[data-test="schedule-add"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test="schedule-form-error"]').text()).toBe('cron 表达式不对：需要 5 段')
    expect((wrapper.find('[data-test="schedule-payload"]').element as HTMLTextAreaElement).value).toBe('留着')
    wrapper.unmount()
  })

  it('pauses an active task and resumes a paused one', async () => {
    const { wrapper, api } = await mountView({
      schedules: vi.fn(async () => [task(), task({ id: 'sch-2', status: 'inactive', nextAt: null })]),
    })

    const toggles = wrapper.findAll('[data-test="schedule-toggle"]')
    expect(toggles.map((node) => node.text())).toEqual(['暂停', '恢复'])

    await toggles[0]?.trigger('click')
    await toggles[1]?.trigger('click')
    await flushPromises()

    expect(api.pauseSchedule).toHaveBeenCalledWith('sch-1')
    expect(api.resumeSchedule).toHaveBeenCalledWith('sch-2')
    wrapper.unmount()
  })

  it('deleting takes two clicks: the first only arms it', async () => {
    const { wrapper, api } = await mountView({ schedules: vi.fn(async () => [task()]) })

    const button = wrapper.find('[data-test="schedule-delete"]')
    await button.trigger('click')
    expect(api.deleteSchedule).not.toHaveBeenCalled()
    expect(button.text()).toContain('确认删除')

    await button.trigger('click')
    await flushPromises()
    expect(api.deleteSchedule).toHaveBeenCalledWith('sch-1')
    wrapper.unmount()
  })
})
