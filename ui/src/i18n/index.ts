/**
 * Interface language. Pure frontend like the theme: remembered per machine, never sent to the API.
 *
 * `zh-CN` is the source language and the fallback: a key missing from another table shows its Chinese text
 * rather than a bare key. With nothing stored the app starts in Chinese, as it always has.
 */
import { ref } from 'vue'
import zhCN, { type MessageKey } from './zh-CN'
import zhTW from './zh-TW'
import en from './en'

export type Locale = 'zh-CN' | 'zh-TW' | 'en'
export type { MessageKey }

export const LOCALES: readonly Locale[] = ['zh-CN', 'zh-TW', 'en']

/** Each language is named in itself, so it stays findable whatever the current language is. */
export const LOCALE_NAMES: Record<Locale, string> = {
  'zh-CN': '简体中文',
  'zh-TW': '繁體中文',
  en: 'English',
}

const LOCALE_KEY = 'partner.locale'
const TABLES: Record<Locale, Record<string, string>> = { 'zh-CN': zhCN, 'zh-TW': zhTW, en }

function isLocale(value: unknown): value is Locale {
  return LOCALES.includes(value as Locale)
}

export function readLocale(): Locale {
  try {
    const stored = window.localStorage.getItem(LOCALE_KEY)
    return isLocale(stored) ? stored : 'zh-CN'
  } catch {
    return 'zh-CN'
  }
}

export const locale = ref<Locale>(readLocale())

function reflect(next: Locale): void {
  if (typeof document !== 'undefined') document.documentElement.setAttribute('lang', next)
}

export function setLocale(next: Locale): void {
  locale.value = next
  reflect(next)
  try {
    window.localStorage.setItem(LOCALE_KEY, next)
  } catch {
    // Without storage the choice still holds for this session.
  }
}

/** Applies the stored language to the document; call once at start-up. */
export function initLocale(): void {
  reflect(locale.value)
}

/**
 * Looks a message up and fills `{name}` placeholders. Reads `locale`, so a template or computed that calls it
 * re-renders when the language changes.
 *
 * The key is either a dotted id from `zh-CN.ts` or the Chinese source text itself. Chinese text is its own
 * zh-CN message; the other languages translate it in `zh-TW.ts` / `en.ts`. Anything missing falls back to the key,
 * so an untranslated string shows its Chinese rather than blank. tests/i18n.test.ts checks every literal.
 */
export function t(key: string, params?: Record<string, string | number | null | undefined>): string {
  const template = TABLES[locale.value][key] ?? (zhCN as Record<string, string>)[key] ?? key
  if (params === undefined) return template
  return template.replace(/\{(\w+)\}/g, (whole, name: string) => (name in params ? String(params[name] ?? '') : whole))
}

/**
 * Marks a Chinese string as translatable without translating it yet. For module-level constants, which are built
 * once: they keep the source text and the code that shows them calls `t()` on it.
 */
export function tr(source: string): string {
  return source
}
