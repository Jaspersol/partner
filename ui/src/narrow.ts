import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * The two widths at which the layout changes shape, and the only place they are written down.
 *
 * <para>
 * `styles.css` expresses both as `@container` queries, but a container query cannot be *read* — and two things
 * need to know the answer: whether the inspector is a column or an overlay, and whether the sidebar can be
 * opened at all. Change these numbers alongside the queries.
 * </para>
 *
 * <para>
 * Separate thresholds on purpose: the inspector goes overlay while there is still room for a rail; the rail
 * goes rail-only when opening it would leave the content region too narrow to read a row in.
 * </para>
 */

/** The inspector stops being a column and becomes an overlay at or below this. Mirrors `@container body (max-width: 64rem)`. */
export const OVERLAY_AT_REM = 64

/** The sidebar becomes a rail permanently at or below this. Mirrors `@container shell (max-width: 52rem)`. */
export const RAIL_AT_REM = 52

/** Pixels per rem, which is how a container query's `rem` is compared against a measured pixel width. */
function remSize(): number {
  const size = Number.parseFloat(getComputedStyle(document.documentElement).fontSize)
  return Number.isFinite(size) && size > 0 ? size : 16
}

function watchWidth(selector: string, thresholdRem: number, apply: (narrow: boolean) => void): () => void {
  let observer: ResizeObserver | null = null

  const measure = () => {
    const host = document.querySelector(selector)
    if (host === null) return
    apply(host.clientWidth / remSize() <= thresholdRem)
  }

  onMounted(() => {
    measure()
    const host = document.querySelector(selector)
    if (host === null) return

    observer = new ResizeObserver(measure)
    observer.observe(host)
  })

  onBeforeUnmount(() => {
    observer?.disconnect()
    observer = null
  })

  return measure
}

/** True when the inspector is a floating overlay rather than a column beside the list. */
export function useOverlayMode() {
  const isOverlay = ref(false)
  watchWidth('.body', OVERLAY_AT_REM, (narrow) => {
    isOverlay.value = narrow
  })
  return { isOverlay }
}

/**
 * True when the window is too narrow for the sidebar to be opened at all: below the threshold the rail is the
 * only state there is, so the toggle button is hidden rather than offering something it cannot deliver.
 */
export function useRailOnlyMode() {
  const railOnly = ref(false)
  watchWidth('.app', RAIL_AT_REM, (narrow) => {
    railOnly.value = narrow
  })
  return { railOnly }
}