import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PetInput, type ClickZone, type CursorReport, type InputHost } from '../src/pet/input'

function harness() {
  const element = document.createElement('canvas')
  element.getBoundingClientRect = () => ({ top: 10, height: 200 } as DOMRect)
  const state = { hidden: false, alpha: 0, hit: null as ClickZone | null }
  const host: InputHost = {
    alphaAt: () => state.alpha,
    cursor: vi.fn(async () => ({ cursorX: 10, cursorY: 10, inside: true, width: 200, height: 200, scale: 1 })),
    setPassthrough: vi.fn(async () => {}),
    moveBy: vi.fn(async () => {}),
    visibleBounds: () => ({ top: 20, height: 160 }),
    isHidden: () => state.hidden,
    hitZone: () => state.hit,
    headBottom: () => 0.25,
    faceBottom: () => 0.5,
    action: vi.fn(), look: vi.fn(), scene: vi.fn(), notify: vi.fn(),
    flingThreshold: () => 1000,
    clickThroughEnabled: () => true,
  }
  return { input: new PetInput(element, host), host, state }
}

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

describe('pet input', () => {
  it('reconciles a rebuilt input with an already passthrough native window', async () => {
    let native = false
    const commands: boolean[] = []
    const old = harness()
    vi.mocked(old.host.setPassthrough).mockImplementation(async (on) => { commands.push(on); native = on })
    await old.input['poll']()
    await Promise.resolve()
    old.input.dispose()
    const next = harness()
    next.host.clickThroughEnabled = () => false
    vi.mocked(next.host.setPassthrough).mockImplementation(async (on) => { commands.push(on); native = on })
    await next.input['poll']()
    await Promise.resolve()
    expect(commands).toEqual([true, false])
    expect(native).toBe(false)
    next.input.dispose()
  })

  it.each([false, true])('serializes a rebuilt input behind an old command (old failure: %s)', async (fails) => {
    let native = false
    const commands: boolean[] = []
    const pending = Promise.withResolvers<void>()
    const old = harness()
    vi.mocked(old.host.setPassthrough).mockImplementation(async (on) => {
      commands.push(on)
      await pending.promise
      native = on
    })
    await old.input['poll']()
    old.input.dispose()
    const next = harness()
    next.host.clickThroughEnabled = () => false
    vi.mocked(next.host.setPassthrough)
      .mockImplementationOnce(async (on) => { commands.push(on); throw new Error('IPC') })
      .mockImplementation(async (on) => { commands.push(on); native = on })
    await next.input['poll']()
    expect(commands).toEqual([true])
    if (fails) pending.reject(new Error('old IPC'))
    else pending.resolve()
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
    expect(commands).toEqual([true, false])
    await next.input['poll']()
    await Promise.resolve()
    expect(commands).toEqual([true, false, false])
    expect(native).toBe(false)
    next.input.dispose()
  })

  it('retries a failed passthrough command on the next poll', async () => {
    const { input, host } = harness()
    vi.mocked(host.setPassthrough).mockRejectedValueOnce(new Error('IPC')).mockResolvedValue(undefined)
    await input['poll']()
    await Promise.resolve()
    await input['poll']()
    await Promise.resolve()
    expect(host.setPassthrough).toHaveBeenCalledTimes(2)
    expect(host.setPassthrough).toHaveBeenLastCalledWith(true)
    input.dispose()
  })

  it('serializes commands and applies a newer drag target after an old command', async () => {
    const { input, host } = harness()
    const pending = Promise.withResolvers<void>()
    vi.mocked(host.setPassthrough).mockReturnValueOnce(pending.promise).mockResolvedValue(undefined)
    await input['poll']()
    void input['setPassthrough'](false)
    expect(host.setPassthrough).toHaveBeenCalledTimes(1)
    pending.resolve()
    await Promise.resolve()
    expect(host.setPassthrough).toHaveBeenLastCalledWith(false)
    input.dispose()
  })

  it('keeps a pressed and dragged pet interactive while cursor polls continue', async () => {
    const { input, host } = harness()
    const element = input['element']
    element.setPointerCapture = vi.fn()
    await input['poll']()
    await Promise.resolve()
    input['handleDown']({ button: 0, screenX: 0, screenY: 0, timeStamp: 1000, pointerId: 1 } as PointerEvent)
    input['handleMove']({ screenX: 10, screenY: 0, timeStamp: 1010 } as PointerEvent)
    await Promise.resolve()
    await input['poll']()
    expect(host.setPassthrough).toHaveBeenLastCalledWith(false)
    expect(host.setPassthrough).toHaveBeenCalledTimes(2)
    input.dispose()
  })

  it('does not query the cursor while hidden', async () => {
    const { input, host, state } = harness()
    state.hidden = true
    input.visibilityChanged()
    await input['poll']()
    expect(host.cursor).not.toHaveBeenCalled()
    input.dispose()
  })

  it('keeps cursor requests single-flight and drops hidden or disposed results', async () => {
    const { input, host, state } = harness()
    const pending = Promise.withResolvers<CursorReport>()
    vi.mocked(host.cursor).mockReturnValueOnce(pending.promise)
    const poll = input['poll']()
    await input['poll']()
    expect(host.cursor).toHaveBeenCalledTimes(1)
    state.hidden = true
    input.visibilityChanged()
    state.hidden = false
    input.visibilityChanged()
    pending.resolve({ cursorX: 10, cursorY: 10, inside: true, width: 200, height: 200, scale: 1.5 })
    await poll
    expect(host.look).not.toHaveBeenCalled()
    expect(host.setPassthrough).not.toHaveBeenCalled()
    const next = Promise.withResolvers<CursorReport>()
    vi.mocked(host.cursor).mockReturnValueOnce(next.promise)
    const last = input['poll']()
    input.dispose()
    next.resolve({ cursorX: 0, cursorY: 0, inside: false, width: 200, height: 200, scale: 1 })
    await last
    expect(host.look).not.toHaveBeenCalled()
  })

  it('uses the visible figure rather than the padded window for fallback zones', () => {
    const { input } = harness()
    expect(input['zoneAt'](59)).toBe('head')
    expect(input['zoneAt'](60)).toBe('face')
    expect(input['zoneAt'](99)).toBe('face')
    expect(input['zoneAt'](100)).toBe('body')
    input.dispose()
  })

  it('prefers a model hit area over the fallback', () => {
    const { input, host, state } = harness()
    state.hit = 'body'
    input['registerClick']({ timeStamp: 1000, clientY: 31 } as PointerEvent)
    vi.advanceTimersByTime(250)
    expect(host.scene).toHaveBeenCalledWith('tapBody')
    input.dispose()
  })
})
