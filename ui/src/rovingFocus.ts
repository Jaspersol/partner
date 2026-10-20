/**
 * Roving tabindex for lists.
 *
 * <para>
 * A listbox of a few hundred options, each with `tabindex="0"`, makes a keyboard user press Tab once per row.
 * Instead the list is a single tab stop and arrow keys move within it; Enter and Space still activate.
 * </para>
 *
 * <para>
 * Pure functions on purpose: "which row does this key mean" is testable without a layout engine.
 * </para>
 */

export interface RovingOptions {
  /** The axis the list runs along. Horizontal is for the view rail; the rest are vertical. */
  axis?: 'vertical' | 'horizontal'
  /** Page size for PageUp/PageDown. */
  page?: number
}

/**
 * Normalises a key name.
 *
 * Browsers send `Enter`, `Escape`, `' '`, while `@vue/test-utils` sends `enter` / `space`. Lower-casing once
 * here avoids a comparison at every call site.
 */
export function normalizeKey(key: string): string {
  return key.toLowerCase()
}

/** True for the two names that mean "activate the focused row". */
export function isActivationKey(key: string): boolean {
  const normalized = normalizeKey(key)
  return normalized === 'enter' || normalized === ' ' || normalized === 'space' || normalized === 'spacebar'
}

/**
 * The row a key press moves to, or `null` when the key is none of ours.
 *
 * <para>
 * Movement **clamps rather than wraps** for vertical lists: a reader at the newest event pressing Down again is
 * asking whether there is anything below, not for a jump to the top of a long conversation.
 * </para>
 *
 * <para>
 * A horizontal rail wraps instead, because a rail has no "below" — Left from the first item landing on the last
 * is the behaviour every tab bar has.
 * </para>
 */
export function rovingTarget(
  from: number,
  count: number,
  key: string,
  options: RovingOptions = {},
): number | null {
  if (count <= 0) return null

  const axis = options.axis ?? 'vertical'
  const page = options.page ?? 10
  const wrap = axis === 'horizontal'
  const k = normalizeKey(key)

  const down = axis === 'vertical' ? 'arrowdown' : 'arrowright'
  const up = axis === 'vertical' ? 'arrowup' : 'arrowleft'

  if (k === 'home') return 0
  if (k === 'end') return count - 1

  let step: number | null = null
  if (k === down) step = 1
  else if (k === up) step = -1
  else if (k === 'pagedown') step = page
  else if (k === 'pageup') step = -page
  if (step === null) return null

  const next = from + step
  if (wrap) return ((next % count) + count) % count
  return Math.min(count - 1, Math.max(0, next))
}

/**
 * Keeps a roving index inside a list whose length just changed.
 *
 * Without this, one new event at the bottom would push the focused row off the end and drop the reader back at
 * the top, losing their place in a conversation that is still going.
 */
export function clampIndex(index: number, count: number): number {
  if (count <= 0) return 0
  return Math.min(count - 1, Math.max(0, index))
}