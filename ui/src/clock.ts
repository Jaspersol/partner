import { onBeforeUnmount, readonly, ref, watch, type Ref } from 'vue'

/**
 * One clock for the whole app (P4 #30).
 *
 * <para>
 * Two components need a ticking `Date.now()`: the reasoning row's elapsed timer and the queue's running timer.
 * Each used to run its own one-second `setInterval`, started on mount and cleared on unmount — which sounds
 * cheap until the reasoning list holds several rows: a turn that thinks in three stretches ran three timers
 * that all fired in the same millisecond, a few hundred milliseconds apart from each other, to produce three
 * numbers that were identical by construction.
 * </para>
 *
 * <para>
 * So: one timer, started when the first subscriber arrives and stopped when the last one leaves. A component
 * that unmounts mid-render cannot leave a timer behind, because the timer is not its.
 * </para>
 */

const subscribers = new Set<() => void>()
const now = ref(Date.now())
let timer: ReturnType<typeof setInterval> | null = null

/**
 * One second, because both consumers display `mm:ss` and neither has a use for finer grain — a display value
 * ticking 1000 times a minute to show the same digits twice is just a way to keep a laptop fan on.
 */
const TICK_MS = 1000

/** The timer, started when the first subscriber arrives and stopped when the last one leaves. */
function startTimer(): void {
  if (timer !== null) return
  now.value = Date.now()
  timer = setInterval(() => {
    now.value = Date.now()
    for (const notify of subscribers) notify()
  }, TICK_MS)
}

function stopTimer(): void {
  if (timer === null) return
  clearInterval(timer)
  timer = null
}

/**
 * A shared `Date.now()` that ticks once a second while at least one caller wants it.
 *
 * <para>
 * Read-only on purpose: a caller that wrote to this would be redefining the clock for every other caller, and
 * "make the timer show a different number" is not a thing anyone should be able to do.
 * </para>
 *
 * <para>
 * <c>wants</c> is a getter rather than a boolean, so a caller can stop wanting without unmounting. A finished
 * reasoning row stays in the log forever, so subscribing for its whole lifetime would keep a timer running for
 * every conversation that ever thought out loud.
 * </para>
 *
 * @param wants whether this caller needs the clock right now
 * @returns the current instant, updated on every tick
 */
export function useSharedTick(wants: () => boolean = () => true): Readonly<Ref<number>> {
  let subscribed = false

  const notify = () => {
    // Reactive under the tick, not through a watcher: writing to a ref is enough for every computed that read
    // it, and it avoids having to re-read it here.
    void now.value
  }

  const setSubscribed = (on: boolean) => {
    if (on === subscribed) {
      return
    }

    subscribed = on
    if (on) {
      subscribers.add(notify)
      startTimer()
      return
    }

    subscribers.delete(notify)
    if (subscribers.size === 0) {
      stopTimer()
    }
  }

  watch(wants, setSubscribed, { immediate: true })

  onBeforeUnmount(() => setSubscribed(false))

  return readonly(now) as Readonly<Ref<number>>
}

/** How many callers currently hold the clock. Exposed for tests, not for features. */
export function sharedTickSubscribers(): number {
  return subscribers.size
}
