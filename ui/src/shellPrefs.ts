/**
 * Desktop-shell preferences: launch at login and the tray icon.
 *
 * <para>
 * These belong to the shell, not to the API, so they are never part of the saved runtime settings. They go through
 * the internals object Tauri injects (like `windowChrome.ts`); outside the desktop shell there is none, and
 * `hasShellPrefs` is false so the settings page hides the group.
 * </para>
 */
interface TauriInternals {
  invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
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
