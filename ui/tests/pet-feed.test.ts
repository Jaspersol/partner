// @vitest-environment node
// The feed is a poll loop; the interval is not what this file checks, so it drives `pollNow` directly and never
// waits on a clock. A fake fetch answers from a queue and records every `after` it was asked for, which is the
// watermark the client actually sends.
import { describe, expect, it } from 'vitest'

import { startFeed, type PetFeed, type FeedHandle } from '../src/pet/feed'

/** Drains the promise chain a poll schedules, without touching a clock. */
async function settle(): Promise<void> {
  for (let index = 0; index < 8; index += 1) await Promise.resolve()
}

function harness(responses: PetFeed[]) {
  const asks: number[] = []
  const replied: string[] = []
  const previewed: { action: string; expression: string }[] = []
  let index = 0
  const options = {
    fetch: async (after: number) => {
      asks.push(after)
      const response = responses[Math.min(index, responses.length - 1)] as PetFeed
      index += 1
      return response
    },
    onReply: (body: string) => replied.push(body),
    onSpeaking: () => undefined,
    onSettings: () => undefined,
    onError: () => undefined,
    isHidden: () => false,
    onPreview: (preview: { action: string; expression: string }) => previewed.push(preview),
  }
  return { options, asks, replied, previewed }
}

async function nextPoll(handle: FeedHandle): Promise<void> {
  handle.pollNow()
  await settle()
}

function feed(overrides: Partial<PetFeed> = {}): PetFeed {
  return {
    speaking: false,
    settingsRevision: 1,
    replies: [],
    lastError: null,
    lastSeq: 0,
    preview: null,
    ...overrides,
  }
}

describe('feed watermark', () => {
  it('starts after the log end rather than replaying history', async () => {
    const { options, asks, replied } = harness([
      feed({ lastSeq: 40, replies: [{ seq: 40, body: 'old' }] }),
      feed({ lastSeq: 40 }),
    ])
    const handle = startFeed(options)
    await settle()
    await nextPoll(handle)
    handle.stop()

    expect(asks[0]).toBe(0)
    // The first poll only seeds: the reply at 40 was history, and the next ask already starts past it.
    expect(asks[1]).toBe(40)
    expect(replied).toEqual([])
  })

  it('moves past tool events that can never be replies', async () => {
    // A reply at 41, then five tool events up to 46. The cursor has to advance to 46 even though the newest reply
    // is 41, or every later poll re-reads those tool events.
    const { options, asks, replied } = harness([
      feed({ lastSeq: 40 }),
      feed({ lastSeq: 46, replies: [{ seq: 41, body: 'hello' }] }),
      feed({ lastSeq: 46 }),
    ])
    const handle = startFeed(options)
    await settle()
    await nextPoll(handle)
    await nextPoll(handle)
    handle.stop()

    expect(replied).toEqual(['hello'])
    expect(asks.at(-1)).toBe(46)
  })
})

describe('preview channel', () => {
  it('applies each preview once, ignoring one outstanding when the window opens', async () => {
    const { options, previewed } = harness([
      // The first poll seeds the preview watermark too: a preview outstanding when the window opens is not replayed.
      feed({ lastSeq: 1, preview: { seq: 3, action: 'nod', expression: '' } }),
      feed({ lastSeq: 1, preview: { seq: 4, action: 'nod', expression: '' } }),
      feed({ lastSeq: 1, preview: { seq: 4, action: 'nod', expression: '' } }),
      feed({ lastSeq: 1, preview: { seq: 5, action: '', expression: 'smile' } }),
    ])
    const handle = startFeed(options)
    await settle()
    await nextPoll(handle)
    await nextPoll(handle)
    await nextPoll(handle)
    handle.stop()

    // Sequence 3 was already outstanding at the first poll and is not replayed; 4 plays once despite being polled
    // twice; 5 plays after it.
    expect(previewed).toEqual([
      { action: 'nod', expression: '' },
      { action: '', expression: 'smile' },
    ])
  })
})
