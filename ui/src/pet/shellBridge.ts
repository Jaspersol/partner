/**
 * The pet window's only route to the shell, which moves, resizes and makes the window click-through natively. It
 * uses the internals object Tauri injects (as `windowChrome.ts` and `shellPrefs.ts` do) rather than pulling in
 * `@tauri-apps/api`. Outside the desktop shell `petShell()` answers null and the page is a plain web page.
 */

interface TauriInternals {
  invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
  transformCallback(callback: (event: { payload?: unknown }) => void): number
}

/** What `pet_cursor` answers: the cursor in window-local physical pixels. */
interface PetCursor {
  cursorX: number
  cursorY: number
  inside: boolean
  width: number
  height: number
  scale: number
}

export interface PetShell {
  cursor(): Promise<PetCursor>
  setPassthrough(on: boolean): Promise<void>
  moveBy(dx: number, dy: number): Promise<void>
  setSize(width: number, height: number): Promise<void>
  /** The pet's own always-on-top switch; the shell opens the window on top, so it has to be told otherwise. */
  setAlwaysOnTop(on: boolean): Promise<void>
  /** Calls `listener` with each visibility boolean the shell emits on `partner:pet`. */
  onVisibility(listener: (visible: boolean) => void): void
}

function internals(): TauriInternals | null {
  const value: unknown = Reflect.get(window, '__TAURI_INTERNALS__')
  if (typeof value !== 'object' || value === null) return null
  if (!('invoke' in value) || typeof (value as TauriInternals).invoke !== 'function') return null
  return value as TauriInternals
}

export function petShell(): PetShell | null {
  const tauri = internals()
  if (tauri === null) return null

  return {
    cursor: async () => {
      const report = (await tauri.invoke('pet_cursor')) as Partial<PetCursor> | null
      return {
        cursorX: Number(report?.cursorX ?? 0),
        cursorY: Number(report?.cursorY ?? 0),
        inside: report?.inside === true,
        width: Number(report?.width ?? 0),
        height: Number(report?.height ?? 0),
        scale: Number(report?.scale ?? 1) || 1,
      }
    },
    setPassthrough: async (on) => {
      await tauri.invoke('pet_set_passthrough', { enabled: on })
    },
    moveBy: async (dx, dy) => {
      // Physical pixels, rounded: the command takes i32 and a fractional pixel would be truncated anyway.
      await tauri.invoke('pet_move_by', { dx: Math.round(dx), dy: Math.round(dy) })
    },
    setSize: async (width, height) => {
      await tauri.invoke('pet_set_size', { width, height })
    },
    setAlwaysOnTop: async (on) => {
      await tauri.invoke('pet_always_on_top', { enabled: on })
    },
    onVisibility: (listener) => {
      if (typeof tauri.transformCallback !== 'function') return
      const handler = tauri.transformCallback((event) => {
        if (typeof event.payload === 'boolean') listener(event.payload)
      })
      void tauri.invoke('plugin:event|listen', {
        event: 'partner:pet',
        target: { kind: 'Any' },
        handler,
      })
    },
  }
}

/**
 * Turns an absolute path into an asset-protocol URL, encoding each path segment and keeping the slashes. Tauri's own
 * `convertFileSrc` encodes the whole path as one segment, and easy-live2d resolves a model's textures and moc
 * relative to the model URL's last slash, so those would be requested from the protocol root and refused.
 */
export function convertFileSrc(path: string): string {
  const normalized = path.replace(/\\/g, '/')
  return `http://asset.localhost/${normalized.split('/').map((segment) => encodeURIComponent(segment)).join('/')}`
}