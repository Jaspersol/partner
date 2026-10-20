/**
 * The window buttons the native title bar would otherwise provide.
 *
 * <para>
 * Calls the shell's window commands through the internals object Tauri injects into every webview, rather than
 * pulling in `@tauri-apps/api` for three calls. Outside the desktop shell there is no such object, and
 * `hasWindowChrome` is false, so the top bar draws no buttons.
 * </para>
 */
interface TauriInternals {
  invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
  metadata?: { currentWindow?: { label?: string } }
}

function internals(): TauriInternals | null {
  return (window as unknown as { __TAURI_INTERNALS__?: TauriInternals }).__TAURI_INTERNALS__ ?? null
}

export function hasWindowChrome(): boolean {
  return internals() !== null
}

export function windowCommand(action: 'minimize' | 'toggle_maximize' | 'close'): void {
  const tauri = internals()
  if (tauri === null) return
  void tauri.invoke(`plugin:window|${action}`, { label: tauri.metadata?.currentWindow?.label ?? 'main' })
}
