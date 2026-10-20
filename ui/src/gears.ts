/**
 * Thinking-gear words, as a reader sees them.
 *
 * The stored value is always the original word (that is what goes on the wire as `reasoning_effort`); only the
 * six common ones get a Chinese name. An unknown word is shown verbatim, like `sessionKind.ts` does for unknown
 * kinds.
 */
import { ref } from 'vue'

/** Names the reader typed in settings (value → text). Reactive, so a saved rename shows up without a reload. */
const custom = ref<Record<string, string>>({})

export function setGearLabels(labels: Record<string, string> | undefined | null): void {
  custom.value = { ...(labels ?? {}) }
}

const NAMES: Record<string, string> = {
  off: '关闭',
  low: '低',
  medium: '中',
  high: '高',
  xhigh: '极高',
  max: '最大',
}

export function gearName(gear: string): string {
  return custom.value[gear] ?? NAMES[gear] ?? gear
}

/** Trimmed and lower case, so `HIGH` and `high ` are one gear (mirrors `ReasoningEfforts.Normalize`). */
export function normalizeGear(value: string): string {
  return value.trim().toLowerCase()
}
