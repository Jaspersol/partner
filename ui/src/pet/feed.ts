/**
 * The reply feed: the pet's only source of what was said, polled from `/api/pet/feed`.
 *
 * The first poll only finds where the log is now, so the pet does not walk through yesterday's replies when it opens.
 * After that, replies above the watermark fire once each, `speaking` is reported on every edge, and a moved settings
 * revision tells the app to re-read the settings document.
 */

export interface PetFeed {
  speaking: boolean
  settingsRevision: number
  replies: { seq: number; body: string }[]
  lastError: string | null
  /** The highest session sequence the server has looked at; the next poll starts after it. */
  lastSeq: number
  /** A 「预览」 request from the settings page, or null when there is nothing new. */
  preview: { seq: number; action: string; expression: string } | null
}

interface FeedOptions {
  /**
   * `after` is the session cursor, `previewAfter` the last preview the window applied. Both travel in one request
   * because the window polls this endpoint anyway.
   */
  fetch: (after: number, previewAfter: number) => Promise<PetFeed>
  onReply: (body: string) => void
  onSpeaking: (speaking: boolean) => void
  onSettings: () => void
  onError: (message: string | null) => void
  isHidden: () => boolean
  /** Applied once per sequence: the settings page's 「预览」 reached the window. */
  onPreview: (preview: { action: string; expression: string }) => void
}

/** While nothing is being said a reply a second is plenty; while speaking the mouth needs the edge promptly. */
const IDLE_INTERVAL_MS = 1000
const SPEAKING_INTERVAL_MS = 250

export interface FeedHandle {
  stop(): void
  pollNow(): void
}

export function startFeed(options: FeedOptions): FeedHandle {
  let after = 0
  /** The newest preview sequence this window has applied, so a re-poll cannot replay one. */
  let previewAfter = 0
  let seeded = false
  let speaking = false
  let revision: number | null = null
  let stopped = false
  let inFlight = false
  let timer: ReturnType<typeof setTimeout> | undefined
  let lastError: string | null = null

  function schedule(delay: number): void {
    if (stopped) return
    clearTimeout(timer)
    // `globalThis`, not `window`: the loop is identical in the pet webview and in a node test, and `window` is
    // undefined outside a browser.
    timer = globalThis.setTimeout(poll, delay)
  }

  function reportError(message: string | null): void {
    if (message === lastError) return
    lastError = message
    options.onError(message)
  }

  function consume(feed: PetFeed): void {
    if (!seeded) {
      seeded = true
      // History is not news: start where the log ends, and say nothing about it.
      after = feed.lastSeq
      // The same rule for previews. A preview belongs to the window that was open when it was asked for; without
      // this, a window opened later would replay the last one, because its own watermark starts at zero.
      if (feed.preview !== null) previewAfter = feed.preview.seq
    } else {
      for (const reply of feed.replies) {
        if (reply.seq <= after) continue
        after = reply.seq
        options.onReply(reply.body)
      }
    }

    // The server's own cursor moves the watermark past the tool events after the last reply, so they are not
    // re-read on every poll.
    after = Math.max(after, feed.lastSeq)

    if (feed.speaking !== speaking) {
      speaking = feed.speaking
      options.onSpeaking(speaking)
    }

    if (revision === null) {
      revision = feed.settingsRevision
    } else if (feed.settingsRevision !== revision) {
      revision = feed.settingsRevision
      options.onSettings()
    }

    // A hidden window still draws nothing, so a preview it missed is played when it comes back rather than lost.
    if (feed.preview !== null && feed.preview.seq > previewAfter) {
      previewAfter = feed.preview.seq
      options.onPreview({ action: feed.preview.action, expression: feed.preview.expression })
    }

    // A load failure travels the feed back to the window that had it; the page shows nothing, so this is the
    // only place it can surface.
    reportError(feed.lastError)
  }

  function poll(): void {
    if (stopped || inFlight) return
    // A hidden window is not drawing, so polling would only cost wake-ups; the visibility handler re-polls.
    if (options.isHidden()) {
      schedule(IDLE_INTERVAL_MS)
      return
    }

    inFlight = true
    options
      .fetch(after, previewAfter)
      .then((feed) => {
        inFlight = false
        if (stopped) return
        consume(feed)
        schedule(speaking ? SPEAKING_INTERVAL_MS : IDLE_INTERVAL_MS)
      })
      .catch((error: unknown) => {
        inFlight = false
        if (stopped) return
        reportError(error instanceof Error ? error.message : String(error))
        // The API may still be starting; keep asking, just less eagerly.
        schedule(IDLE_INTERVAL_MS)
      })
  }

  void poll()

  return {
    stop: () => {
      stopped = true
      clearTimeout(timer)
      timer = undefined
    },
    pollNow: () => {
      clearTimeout(timer)
      timer = undefined
      poll()
    },
  }
}