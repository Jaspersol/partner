/**
 * Roving tabindex for lists (P4 #26).
 *
 * <para>
 * The problem this exists for: a listbox of a few hundred options, each with `tabindex="0"`, makes a keyboard
 * user press Tab once per row to get past it. On a session of five hundred events that is five hundred presses,
 * which is the same as saying the list cannot be reached by keyboard at all.
 * </para>
 *
 * <para>
 * The fix is the standard one — the list is a single tab stop, and arrow keys move within it. Everything else
 * about the interaction is unchanged: Enter and Space still activate, focus still follows.
 * </para>
 *
 * <para>
 * This module is a **pure function on purpose**. The arithmetic of "which row does this key mean" is the part
 * worth testing exhaustively, and a pure function is the only version of it that can be tested without a layout
 * engine — the same reason `scripts/e2e-shell.mjs` exists for the things this cannot cover.
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
 * <para>
 * Browsers send `Enter`, `Escape`, `' '`. `@vue/test-utils`'s `trigger('keydown.enter')` sends `enter`, and
 * `keydown.space` sends `space`. Vue's own `.enter` template modifier hid that difference by comparing hyphenated
 * forms; now that the handler is ours instead of a template modifier's, absorbing it is ours to do. Lower-casing
 * once, here, is cheaper than a comparison at every call site and than remembering which spelling this one uses.
 * </para>
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
 * Movement **clamps rather than wraps**. Wrapping is right for a menu and wrong for a log: a reader who has
 * reached the newest event and presses Down again is not asking for the oldest one, they are asking whether
 * there is anything below. Answering that with a jump to the top of a five-hundred-row conversation is the
 * single most disorienting thing a list can do.
 * </para>
 *
 * <para>
 * A horizontal rail wraps instead, because a rail has no "below" — Left from the first item landing on the
 * last is the behaviour every tab bar has.
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
 * <para>
 * The list grows under the reader — that is the whole point of a live session log — and the index has to
 * follow. Without this, one new event at the bottom would push the focused row off the end and drop the reader
 * back at the top, losing their place in a conversation that is still going.
 * </para>
 */
export function clampIndex(index: number, count: number): number {
  if (count <= 0) return 0
  return Math.min(count - 1, Math.max(0, index))
}