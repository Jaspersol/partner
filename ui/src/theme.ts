/**
 * Theme is pure frontend (ui.md §3): it never reaches the API. The choice is remembered per machine, and
 * `auto` leaves the decision to the OS via the token selectors in styles.css.
 */

export type Theme = 'auto' | 'light' | 'dark'

const THEME_KEY = 'partner.theme'
const ORDER: Theme[] = ['auto', 'light', 'dark']

export function readTheme(): Theme {
  try {
    const stored = window.localStorage.getItem(THEME_KEY)
    return ORDER.find((candidate) => candidate === stored) ?? 'auto'
  } catch {
    return 'auto'
  }
}

export function applyTheme(theme: Theme): void {
  if (theme === 'auto') {
    document.documentElement.removeAttribute('data-theme')
  } else {
    document.documentElement.setAttribute('data-theme', theme)
  }

  try {
    window.localStorage.setItem(THEME_KEY, theme)
  } catch {
    // A sandboxed context without storage still gets the theme for this session.
  }
}

export function nextTheme(theme: Theme): Theme {
  return ORDER[(ORDER.indexOf(theme) + 1) % ORDER.length] ?? 'auto'
}

export function themeLabel(theme: Theme): string {
  return theme === 'auto' ? '跟随系统' : theme === 'light' ? '浅色' : '深色'
}

/**
 * 「减弱透明效果」: the in-app switch for the glass (ui-redesign.md §2.2). Same mechanism as the theme — a
 * per-machine preference, frontend only, expressed as an attribute the token sheet reads. `reduced` swaps every
 * translucent layer for an opaque one and turns the blur and the ambient wash off.
 */
const GLASS_KEY = 'partner.glass'

export function readReducedTransparency(): boolean {
  try {
    return window.localStorage.getItem(GLASS_KEY) === 'off'
  } catch {
    return false
  }
}

export function applyReducedTransparency(reduced: boolean): void {
  if (reduced) {
    document.documentElement.setAttribute('data-glass', 'off')
  } else {
    document.documentElement.removeAttribute('data-glass')
  }

  try {
    window.localStorage.setItem(GLASS_KEY, reduced ? 'off' : 'on')
  } catch {
    // Without storage it still applies for this session.
  }
}
