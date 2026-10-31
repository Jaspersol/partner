import { describe, expect, it, vi } from 'vitest'
import { watchPageVisibility, type TauriInternals } from '../src/pageVisibility'

describe('page visibility', () => {
  it('tracks the shell event and registers one listener', () => {
    let callback: (event: { payload?: unknown }) => void = () => {}
    const invoke = vi.fn(async () => 0)
    const internals: TauriInternals = {
      invoke,
      transformCallback: (next) => {
        callback = next
        return 1
      },
    }

    const isHidden = watchPageVisibility(internals)

    expect(isHidden()).toBe(false)
    expect(invoke).toHaveBeenCalledWith('plugin:event|listen', {
      event: 'partner:visibility',
      target: { kind: 'Any' },
      handler: 1,
    })

    callback({ payload: false })
    expect(isHidden()).toBe(true)

    callback({ payload: true })
    expect(isHidden()).toBe(false)

    callback({ payload: 'ignored' })
    expect(isHidden()).toBe(false)
  })
})
