// @vitest-environment node
// A state machine with an injected clock: no DOM, no timers, no model.
import { describe, expect, it } from 'vitest'

import type { ActionLayer } from '../src/pet/actions'
import { Director, type DirectorSettings } from '../src/pet/director'
import type { EmotionMatch } from '../src/pet/emotion'

interface Played {
  action: string
  priority?: number
  tapZone?: string
}

interface FakeActions {
  action: ActionLayer
  played: Played[]
  expressions: string[]
  /** Read through a getter: the fake mutates its own record, and a spread would snapshot it. */
  readonly mouthState: boolean | null
}

function fakeActions(): FakeActions {
  const played: Played[] = []
  const expressions: string[] = []
  const state = { mouth: null as boolean | null }
  const fake = {
    play: (action: string, options?: { priority?: number; tapZone?: string }) => {
      played.push({
        action,
        ...(options?.priority === undefined ? {} : { priority: options.priority }),
        ...(options?.tapZone === undefined ? {} : { tapZone: options.tapZone }),
      })
    },
    expression: (name: string) => {
      expressions.push(name)
    },
    releaseExpression: () => undefined,
    mouth: (open: boolean) => {
      state.mouth = open
    },
    tick: () => undefined,
    look: () => undefined,
    configure: () => undefined,
    stopAction: () => undefined,
    stopAll: () => undefined,
  }

  // A test seam: the director only ever calls play/expression/mouth, so the rest of the class is not modelled.
  return {
    action: fake as unknown as ActionLayer,
    played,
    expressions,
    get mouthState() {
      return state.mouth
    },
  }
}

function settings(overrides: Partial<DirectorSettings> = {}): DirectorSettings {
  return {
    idleMinSeconds: 60,
    idleMaxSeconds: 60,
    sleepAfterMinutes: 0,
    greetingTimes: [],
    emotionCooldownSeconds: 8,
    ...overrides,
  }
}

function emotion(name: 'smile' | 'angry' | 'cry' | 'shy' | null): EmotionMatch {
  const totals = { smile: 0, angry: 0, cry: 0, shy: 0 }
  if (name !== null) totals[name] = 3
  return { emotion: name, score: name === null ? 0 : 3, totals, hits: [] }
}

/** A clock a test moves by hand, and a random source fixed at whatever the test wants next. */
function harness(options: { settings?: DirectorSettings; now?: number; random?: number } = {}) {
  const state = {
    now: options.now ?? 0,
    sleeping: false,
    random: options.random ?? 0,
  }
  const actions = fakeActions()
  const director = new Director(
    {
      now: () => state.now,
      random: () => state.random,
      action: actions.action,
      sleeping: () => state.sleeping,
      setSleeping: (value: boolean) => {
        state.sleeping = value
      },
    },
    options.settings ?? settings(),
  )
  return { director, state, actions }
}

describe('priority', () => {
  it('preempts a scene of lower or equal priority', () => {
    const { director } = harness()
    expect(director.scene('idle', 0)).toBe('ran')
    expect(director.runningScene).toBe('idle')
    // A tap is priority 4 against the idle scene's 1.
    expect(director.scene('tapHead', 10)).toBe('ran')
    expect(director.runningScene).toBe('tapHead')
  })

  it('drops a scene of lower priority instead of queueing it', () => {
    const { director } = harness()
    expect(director.scene('fling', 0)).toBe('ran')
    expect(director.scene('idle', 10)).toBe('dropped')
    expect(director.runningScene).toBe('fling')
    // The dropped scene must not replay later either.
    director.update(5_000)
    expect(director.runningScene).toBeNull()
  })

  it('lets a scene end and the slot fall idle again', () => {
    const { director } = harness()
    director.scene('tapFace', 0)
    director.update(200)
    expect(director.runningScene).toBe('tapFace')
    director.update(2_000)
    expect(director.runningScene).toBeNull()
  })

  it('maps scene priority onto the framework motion priority', () => {
    const { director, actions } = harness()
    director.scene('idle', 0)
    expect(actions.played.at(-1)?.priority).toBe(1)
    director.scene('tapBody', 10)
    expect(actions.played.at(-1)?.priority).toBe(3)
  })
})

describe('emotions', () => {
  it('runs immediately when the slot is free', () => {
    const { director, actions } = harness()
    expect(director.emotion(emotion('angry'), 0)).toBe('ran')
    expect(director.runningScene).toBe('emotion')
    expect(actions.expressions).toContain('angry')
  })

  it('queues one deep behind a running scene and runs it when that ends', () => {
    const { director } = harness()
    director.scene('tapHead', 0)
    expect(director.emotion(emotion('cry'), 10)).toBe('queued')

    // A second emotion replaces the queued one: the newest reply is the topic.
    expect(director.emotion(emotion('shy'), 20)).toBe('queued')

    director.update(1_700)
    expect(director.runningScene).toBe('emotion')
  })

  it('honours the cooldown and only refreshes the face on a repeat', () => {
    const { director, actions } = harness({ settings: settings({ emotionCooldownSeconds: 8 }) })
    expect(director.emotion(emotion('smile'), 0)).toBe('ran')
    const faces = actions.expressions.length

    director.update(2_000)
    expect(director.emotion(emotion('smile'), 2_000)).toBe('dropped')
    // The held expression was refreshed rather than a scene replayed.
    expect(actions.expressions.length).toBeGreaterThan(faces)

    expect(director.emotion(emotion('smile'), 9_000)).toBe('ran')
  })

  it('ignores a match with no emotion', () => {
    const { director } = harness()
    expect(director.emotion(emotion(null), 0)).toBe('dropped')
  })
})

describe('idle sampling', () => {
  it('samples the 45/45/10 split from the injected random source', () => {
    for (const [roll, expected] of [[0, 'greet'], [0.44, 'greet'], [0.45, 'stretch'], [0.89, 'stretch'], [0.9, 'blink'], [0.99, 'blink']] as const) {
      const { director, actions } = harness({ random: roll })
      director.update(61_000)
      expect(director.runningScene, `roll ${roll}`).toBe('idle')
      expect(actions.played.at(-1)?.action, `roll ${roll}`).toBe(expected)
    }
  })

  it('does not idle while a scene owns the slot', () => {
    const { director } = harness({ settings: settings({ idleMinSeconds: 1, idleMaxSeconds: 1 }) })
    // The fling scene holds the slot until 1.8 s; the idle gap comes due at 1 s, inside it.
    director.scene('fling', 0)
    director.update(1_200)
    expect(director.runningScene).toBe('fling')

    director.update(2_000)
    expect(director.runningScene).toBe('idle')
  })
})

describe('sleeping', () => {
  it('sleeps after the configured minutes with no interaction', () => {
    const { director, state } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(9 * 60_000)
    expect(state.sleeping).toBe(false)
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)
  })

  it('never sleeps when the setting is zero', () => {
    const { director, state } = harness({ settings: settings({ sleepAfterMinutes: 0 }) })
    director.update(24 * 60 * 60_000)
    expect(state.sleeping).toBe(false)
  })

  it('wakes on a tap and then plays the tap scene after the wake', () => {
    const { director, state, actions } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)

    actions.played.length = 0
    const at = 700_000
    expect(director.scene('tapBody', at)).toBe('ran')
    expect(state.sleeping).toBe(false)
    expect(director.runningScene).toBe('wake')
    expect(actions.played.map((entry) => entry.action)).toContain('wake')

    // The tap that woke the pet runs once the wake scene ends.
    actions.played.length = 0
    director.update(at + 2000)
    const played = actions.played.map((entry) => entry.action)
    expect(played.some((action) => action === 'bodyShake' || action === 'shrink')).toBe(true)
  })

  it('does not wake from being dragged, but still reacts', () => {
    const { director, state, actions } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)

    const before = actions.played.length
    expect(director.scene('drag', 700_000)).toBe('ran')
    expect(actions.played.length).toBeGreaterThan(before)
    expect(state.sleeping).toBe(true)
  })

  it('does not tilt its head at the cursor while asleep', () => {
    const { director, state, actions } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)

    const before = actions.played.length
    expect(director.scene('hover', 700_000)).toBe('dropped')
    expect(actions.played.length).toBe(before)
    expect(state.sleeping).toBe(true)
  })

  it('wakes on a reply before the emotion runs', () => {
    const { director, state } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)

    director.reply()
    expect(state.sleeping).toBe(false)
    expect(director.runningScene).toBe('wake')

    // The emotion queues behind the wake and runs when it finishes.
    expect(director.emotion(emotion('smile'), 700_000)).toBe('queued')
    director.update(1_400_000)
    expect(director.runningScene).toBe('emotion')
  })

  it('wakes a sleeping pet for a greeting and then greets', () => {
    const greetingAt = new Date(2026, 4, 15, 9, 0, 0).getTime()
    const { director, state } = harness({
      now: greetingAt,
      settings: settings({ sleepAfterMinutes: 0, greetingTimes: ['09:00'] }),
    })
    state.sleeping = true
    director.update(greetingAt)
    expect(state.sleeping).toBe(false)

    expect(director.runningScene).toBe('wake')
    director.update(greetingAt + 2_000)
    expect(director.runningScene).toBe('greeting')
  })
})

describe('greetings', () => {
  it('fires once per HH:mm per day', () => {
    const at = new Date(2026, 4, 15, 9, 0, 0).getTime()
    const { director, actions } = harness({ now: at, settings: settings({ greetingTimes: ['09:00'] }) })

    director.update(at)
    expect(director.runningScene).toBe('greeting')
    const first = actions.played.length

    director.update(at + 30_000)
    expect(actions.played.length).toBe(first)

    // The same minute on the next day fires again.
    const tomorrow = new Date(2026, 4, 16, 9, 0, 0).getTime()
    director.update(tomorrow)
    expect(actions.played.length).toBeGreaterThan(first)
  })

  it('does nothing outside the configured minutes', () => {
    const at = new Date(2026, 4, 15, 9, 1, 0).getTime()
    const { director } = harness({ now: at, settings: settings({ greetingTimes: ['09:00'] }) })
    director.update(at)
    expect(director.runningScene).toBeNull()
  })
})

describe('speaking', () => {
  it('drives the mouth and nods periodically', () => {
    const { director, actions } = harness()
    director.speaking(true)
    expect(actions.mouthState).toBe(true)

    director.update(4_000)
    expect(actions.played.map((entry) => entry.action)).toContain('nod')

    director.speaking(false)
    expect(actions.mouthState).toBe(false)
  })
})

describe('tap zone routing', () => {
  it('passes the zone only to the first step of a tap scene', () => {
    const { director, actions } = harness()
    director.scene('tapFace', 0)
    // tapFace: [{ blink, 0 }, { surprise, 180 }]. Only blink gets the zone.
    expect(actions.played[0]).toMatchObject({ action: 'blink', tapZone: 'face' })
    actions.played.length = 0
    director.update(200)
    expect(actions.played.at(-1)).toMatchObject({ action: 'surprise' })
    expect(actions.played.at(-1)?.tapZone).toBeUndefined()
  })

  it('gives no zone to a scene that is not a tap', () => {
    const { director, actions } = harness()
    director.scene('greeting', 0)
    expect(actions.played.every((entry) => entry.tapZone === undefined)).toBe(true)
  })

  it('does not let the wake scene steal the zone from a tap that woke the pet', () => {
    const { director, state, actions } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)

    const at = 700_000
    actions.played.length = 0
    director.scene('tapHead', at)
    // The wake scene (and its delayed blink) must not carry the zone.
    expect(actions.played.every((entry) => entry.tapZone === undefined)).toBe(true)
    expect(actions.played.some((entry) => entry.action === 'wake')).toBe(true)

    // Wake ends; the queued tap plays its first step with the head zone.
    actions.played.length = 0
    director.update(at + 2_000)
    expect(actions.played.some((entry) => entry.tapZone === 'head')).toBe(true)
  })
})

describe('queued scenes', () => {
  it('keeps a queued emotion when a tap also woke the pet', () => {
    const { director, state } = harness({ settings: settings({ sleepAfterMinutes: 10 }) })
    director.update(10 * 60_000 + 1)
    expect(state.sleeping).toBe(true)

    const at = 700_000
    expect(director.scene('tapHead', at)).toBe('ran') // wakes the pet, queues the tap
    expect(director.emotion(emotion('shy'), at)).toBe('queued') // a reply during the wake: emotion queues too

    // Wake ends: only one scene is dequeued, so the tap takes the slot.
    director.update(at + 2_000)
    expect(director.runningScene).toBe('tapHead')

    // Tap ends: the emotion is still queued and now gets its turn.
    director.update(at + 4_000)
    expect(director.runningScene).toBe('emotion')
  })
})