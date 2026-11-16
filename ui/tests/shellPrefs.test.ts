// The pet switch listens to a Tauri event; the unsubscribe has to name the event id `listen` returned, not the
// callback handle, or every re-mount of the settings page leaks a listener.
import { afterEach, describe, expect, it, vi } from 'vitest'

import { watchPetShowing } from '../src/shellPrefs'

type Listener = (event: { payload?: unknown }) => void

const shellGlobals = window as unknown as { __TAURI_INTERNALS__?: unknown }

function installInternals() {
  let callback: Listener = () => {}
  const invoke = vi.fn(async (command: string) => (command === 'plugin:event|listen' ? 42 : undefined))
  shellGlobals.__TAURI_INTERNALS__ = {
    invoke,
    transformCallback: (next: Listener) => {
      callback = next
      return 7
    },
  }
  return { invoke, fire: (payload: unknown) => callback({ payload }) }
}

describe('watchPetShowing', () => {
  afterEach(() => {
    delete shellGlobals.__TAURI_INTERNALS__
  })

  it('forwards boolean payloads and unsubscribes with the event id listen returned', async () => {
    const { invoke, fire } = installInternals()
    const seen: boolean[] = []
    const stop = watchPetShowing((enabled) => seen.push(enabled))
    expect(invoke).toHaveBeenCalledWith('plugin:event|listen', {
      event: 'partner:pet',
      target: { kind: 'Any' },
      handler: 7,
    })

    fire(true)
    fire(false)
    fire('ignored')
    expect(seen).toEqual([true, false])

    await Promise.resolve() // let the `listen` promise settle so the id is captured
    stop()
    expect(invoke).toHaveBeenCalledWith('plugin:event|unlisten', { event: 'partner:pet', eventId: 42 })
  })

  it('still unsubscribes when stopped before listen answered', async () => {
    const { invoke } = installInternals()
    const stop = watchPetShowing(() => {})
    stop() // the id has not arrived yet
    await Promise.resolve()
    expect(invoke).toHaveBeenCalledWith('plugin:event|unlisten', { event: 'partner:pet', eventId: 42 })
  })
})
