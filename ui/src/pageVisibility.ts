export interface TauriInternals {
  invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
  transformCallback(callback: (event: { payload?: unknown }) => void): number
}

export function watchPageVisibility(internals: TauriInternals | null): () => boolean {
  let hidden = false
  if (internals !== null) {
    const handler = internals.transformCallback((event) => {
      if (typeof event.payload === 'boolean') {
        hidden = !event.payload
      }
    })
    void internals.invoke('plugin:event|listen', {
      event: 'partner:visibility',
      target: { kind: 'Any' },
      handler,
    })
  }

  return () => hidden
}

function tauriInternals(): TauriInternals | null {
  const value: unknown = Reflect.get(window, '__TAURI_INTERNALS__')
  if (typeof value !== 'object' || value === null) {
    return null
  }
  if (!('invoke' in value) || typeof value.invoke !== 'function') {
    return null
  }
  if (!('transformCallback' in value) || typeof value.transformCallback !== 'function') {
    return null
  }
  return value as TauriInternals
}

const pageHidden = watchPageVisibility(tauriInternals())

export function isPageHidden(): boolean {
  return pageHidden()
}
