import { onBeforeUnmount, readonly, ref, watch, type Ref } from 'vue'

const subscribers = new Set<() => void>()
const now = ref(Date.now())
let timer: ReturnType<typeof setInterval> | null = null

/** One second: both consumers display `mm:ss`. */
const TICK_MS = 1000

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
 * Read-only: a caller that could write to it would redefine the clock for every other caller.
 * </para>
 *
 * <para>
 * `wants` is a getter rather than a boolean, so a caller can stop wanting without unmounting: a finished
 * reasoning row stays in the log forever, and subscribing for its whole lifetime would keep the timer running
 * for every conversation that ever thought out loud.
 * </para>
 *
 * @param wants whether this caller needs the clock right now
 * @returns the current instant, updated on every tick
 */
export function useSharedTick(wants: () => boolean = () => true): Readonly<Ref<number>> {
  let subscribed = false

  const notify = () => {
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

/** How many callers currently hold the clock. For tests only. */
export function sharedTickSubscribers(): number {
  return subscribers.size
}
