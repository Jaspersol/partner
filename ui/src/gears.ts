/**
 * Thinking-gear words, as a reader sees them.
 *
 * The stored value is always the original word (that is what goes on the wire as `reasoning_effort`); only the
 * six common ones get a Chinese name. An unknown word is shown verbatim, like `sessionKind.ts` does for unknown
 * kinds.
 */
import { ref } from 'vue'
import { t, tr } from './i18n'

/** Names the reader typed in settings (value → text). Reactive, so a saved rename shows up without a reload. */
const custom = ref<Record<string, string>>({})

export function setGearLabels(labels: Record<string, string> | undefined | null): void {
  custom.value = { ...(labels ?? {}) }
}

const NAMES: Record<string, string> = {
  off: tr('关闭'),
  low: tr('低'),
  medium: tr('中'),
  high: tr('高'),
  xhigh: tr('极高'),
  max: tr('最大'),
}

export function gearName(gear: string): string {
  const name = NAMES[gear]
  return custom.value[gear] ?? (name === undefined ? gear : t(name))
}

/** Trimmed and lower case, so `HIGH` and `high ` are one gear (mirrors `ReasoningEfforts.Normalize`). */
export function normalizeGear(value: string): string {
  return value.trim().toLowerCase()
}
