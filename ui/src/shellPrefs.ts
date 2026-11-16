/**
 * Desktop-shell preferences: launch at login, the tray icon, and whether the pet window is showing.
 *
 * <para>
 * These belong to the shell, not to the API, so they are never part of the saved runtime settings. They go through
 * the internals object Tauri injects (like `windowChrome.ts`); outside the desktop shell there is none, and
 * `hasShellPrefs` is false so the settings page hides the group.
 * </para>
 */
interface TauriInternals {
  invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
  transformCallback(callback: (event: { payload?: unknown }) => void): number
}

function internals(): TauriInternals | null {
  return (window as unknown as { __TAURI_INTERNALS__?: TauriInternals }).__TAURI_INTERNALS__ ?? null
}

export function hasShellPrefs(): boolean {
  return internals() !== null
}

export async function getAutostart(): Promise<boolean> {
  const tauri = internals()
  return tauri === null ? false : (await tauri.invoke('plugin:autostart|is_enabled')) === true
}

export async function setAutostart(enabled: boolean): Promise<void> {
  await internals()?.invoke(enabled ? 'plugin:autostart|enable' : 'plugin:autostart|disable')
}

export async function getTrayEnabled(): Promise<boolean> {
  const tauri = internals()
  return tauri === null ? false : (await tauri.invoke('get_tray_enabled')) === true
}

export async function setTrayEnabled(enabled: boolean): Promise<void> {
  await internals()?.invoke('set_tray_enabled', { enabled })
}

/**
 * Whether the pet window is showing. A shell preference beside the tray one, not a settings field: the shell
 * decides at start-up whether to create the window, before the API answers.
 */
export async function getPetEnabled(): Promise<boolean> {
  const tauri = internals()
  return tauri === null ? false : (await tauri.invoke('get_pet_enabled')) === true
}

/**
 * Whether this shell was built with the pet. A distributed build compiles the pet commands out for licensing
 * reasons and says so through the injected `__PARTNER_PET__` flag. Outside the shell the answer is yes: the Core
 * and model controls go through the API, so the section still has something real to show.
 */
export function petSupported(): boolean {
  const flag = Reflect.get(window, '__PARTNER_PET__')
  return typeof flag === 'boolean' ? flag : true
}

export async function setPetEnabled(enabled: boolean): Promise<void> {
  await internals()?.invoke('set_pet_enabled', { enabled })
}

/** Puts the pet back where it opens the first time. Nothing to do outside the shell. */
export async function resetPetPosition(): Promise<void> {
  await internals()?.invoke('pet_reset_position')
}

/**
 * Subscribes to the shell's "the pet window is showing" event, which the tray switch and a close of the pet window
 * emit as well as the settings page's own toggle. Answers the unsubscribe function; outside the shell it is a
 * no-op, like {@link watchPageVisibility}, because the settings page is also served as a plain web page.
 */
export function watchPetShowing(listener: (enabled: boolean) => void): () => void {
  const tauri = internals()
  if (tauri === null || typeof tauri.transformCallback !== 'function') return () => undefined

  const handler = tauri.transformCallback((event) => {
    if (typeof event.payload === 'boolean') listener(event.payload)
  })
  // `plugin:event|unlisten` takes the event id `listen` resolves to, not the callback handle. A stop that comes
  // before the id arrives is remembered so the listener is still removed.
  let eventId: number | null = null
  let dropped = false
  void tauri
    .invoke('plugin:event|listen', {
      event: 'partner:pet',
      target: { kind: 'Any' },
      handler,
    })
    .then((id) => {
      if (typeof id !== 'number') return
      if (dropped) void tauri.invoke('plugin:event|unlisten', { event: 'partner:pet', eventId: id })
      else eventId = id
    })
    .catch(() => undefined)

  return () => {
    if (eventId !== null) {
      void tauri.invoke('plugin:event|unlisten', { event: 'partner:pet', eventId })
      eventId = null
    } else {
      dropped = true
    }
  }
}

/**
 * Hands a verified installer to the shell, which starts it and quits: the installer replaces the files this
 * process is running from, so it cannot stay alive. The path is the one the API's download state reported.
 */
export async function installUpdate(installerPath: string): Promise<void> {
  const tauri = internals()
  if (tauri === null) {
    throw new Error('not in the desktop shell')
  }

  await tauri.invoke('install_update', { path: installerPath })
}
