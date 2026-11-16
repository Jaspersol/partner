// @vitest-environment node
// The action layer talks to a model only through the small `ActionRuntime` interface, so a fake one is the whole
// fixture: no Pixi, no WebGL, no Live2D.
import { describe, expect, it } from 'vitest'

import { ActionLayer, type ActionMapping, type ActionRuntime } from '../src/pet/actions'

interface Write {
  id: string
  value: number
  weight: number
}

interface MotionCall {
  group: string
  no: number
  priority: number
}

/**
 * A stand-in for the sprite. `present` is the set of parameters the model has; anything else answers null from
 * `parameterRange`, which is exactly what a real model does for a parameter it does not carry.
 */
function fakeRuntime(options: { present?: string[]; groups?: string[] } = {}) {
  const present = new Set(options.present ?? [])
  const groups = new Set(options.groups ?? [])
  const writes: Write[] = []
  const motions: MotionCall[] = []
  const expressions: string[] = []

  const runtime: ActionRuntime = {
    playMotion: async (group, no, priority) => {
      if (!groups.has(group)) return false
      motions.push({ group, no, priority })
      return true
    },
    setParameter: (id, value, weight) => {
      writes.push({ id, value, weight: weight ?? 1 })
    },
    parameterRange: (id) => (present.has(id) ? rangeOf(id) : null),
    setExpression: (name) => {
      expressions.push(name)
    },
  }
  return { runtime, writes, motions, expressions }
}

/**
 * The override map as easy-live2d keeps it: one slot per parameter id, last write wins, and a weight-0 write
 * releases the slot rather than pinning zero. A test that asserts on `writes` alone cannot see a later source
 * overwriting an earlier one.
 */
function overrides(writes: readonly Write[]): Map<string, number> {
  const map = new Map<string, number>()
  for (const write of writes) {
    if (write.weight === 0) map.delete(write.id)
    else map.set(write.id, write.value)
  }
  return map
}

const allParams = [
  'ParamEyeLOpen',
  'ParamEyeROpen',
  'ParamAngleX',
  'ParamAngleY',
  'ParamAngleZ',
  'ParamBodyAngleX',
  'ParamBodyAngleY',
  'ParamBodyAngleZ',
  'ParamEyeBallX',
  'ParamEyeBallY',
  'ParamMouthOpenY',
  'ParamEyeLSmile',
  'ParamEyeRSmile',
  'ParamCheek',
  'ParamMouthForm',
]

/**
 * The range a parameter actually has in a Cubism model. Degrees for the head and body angles, 0..1 for an open
 * eye, -1..1 for the brows and the mouth shape — a fixture that answered ±1 for everything would clamp a 30°
 * head turn to 1 and make a composition test pass for the wrong reason.
 */
function rangeOf(id: string): { min: number; max: number } {
  if (id.startsWith('ParamAngle') || id.startsWith('ParamBodyAngle')) return { min: -30, max: 30 }
  if (id === 'ParamEyeLOpen' || id === 'ParamEyeROpen') return { min: 0, max: 1 }
  if (id === 'ParamMouthOpenY' || id === 'ParamCheek') return { min: 0, max: 1 }
  return { min: -1, max: 1 }
}

function mapping(overrides: Partial<ActionMapping> = {}): ActionMapping {
  return { motions: {}, expressions: {}, headBottom: 0.35, faceBottom: 0.55, ...overrides }
}

describe('auto mapping by name', () => {
  it('matches an action to a group named after it', async () => {
    const fake = fakeRuntime({ groups: ['Greet'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'Greet', no: 2, name: 'hello' }], [])

    layer.play('greet')
    await Promise.resolve()

    expect(fake.motions).toEqual([{ group: 'Greet', no: 2, priority: 2 }])
  })

  it('matches an expression by name', () => {
    const fake = fakeRuntime({ present: allParams })
    const layer = new ActionLayer(fake.runtime, mapping(), [], ['smile.exp3.json', 'angry.exp3.json'])

    layer.expression('smile')
    expect(fake.expressions.at(-1)).toBe('smile.exp3.json')
  })

  it('matches a Chinese expression name', () => {
    const fake = fakeRuntime({ present: allParams })
    const layer = new ActionLayer(fake.runtime, mapping(), [], ['哭.exp3.json'])
    layer.expression('cry')
    expect(fake.expressions.at(-1)).toBe('哭.exp3.json')
  })
})

describe('explicit profile mapping', () => {
  it('uses the mapped group and index instead of the name match', async () => {
    const fake = fakeRuntime({ groups: ['TapBody', 'CAT'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'motion', group: 'CAT', no: 1 } } }),
      [
        { group: 'TapBody', no: 0, name: 'tap' },
        { group: 'CAT', no: 1, name: 'motion1' },
      ],
      [],
    )

    layer.play('greet')
    await Promise.resolve()
    expect(fake.motions).toEqual([{ group: 'CAT', no: 1, priority: 2 }])
  })

  it('uses an explicit expression', () => {
    const fake = fakeRuntime({ present: allParams })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ expressions: { angry: { kind: 'expression', name: 'live2d_expression1.exp3.json' } } }),
      [],
      ['live2d_expression1.exp3.json'],
    )

    layer.expression('angry')
    expect(fake.expressions.at(-1)).toBe('live2d_expression1.exp3.json')
  })
})

describe('none and procedural', () => {
  it('skips an action mapped to none', async () => {
    const fake = fakeRuntime({ present: allParams, groups: ['Greet'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'none' } } }),
      [{ group: 'Greet', no: 0, name: 'hello' }],
      [],
    )

    layer.play('greet')
    layer.tick(0.1)
    await Promise.resolve()

    expect(fake.motions).toEqual([])
    // A tick still hands the look parameters back at rest (weight 0); the action itself wrote nothing.
    expect(fake.writes.every((write) => write.weight === 0)).toBe(true)
    expect(fake.writes.some((write) => write.id === 'ParamAngleZ')).toBe(false)
  })

  it('skips the name match when the mapping says procedural', async () => {
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'ParamBodyAngleZ'], groups: ['Greet'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'procedural' } } }),
      [{ group: 'Greet', no: 0, name: 'hello' }],
      [],
    )

    layer.play('greet')
    layer.tick(0.1)
    await Promise.resolve()

    expect(fake.motions).toEqual([])
    expect(fake.writes.some((write) => write.id === 'ParamAngleZ')).toBe(true)
  })

  it('applies the parameter preset for a procedural expression, ignoring the .exp3', () => {
    const fake = fakeRuntime({ present: ['ParamCheek', 'ParamEyeBallX', 'ParamAngleY'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ expressions: { shy: { kind: 'procedural' } } }),
      [],
      ['blush.exp3.json'],
    )

    layer.expression('shy')
    expect(fake.expressions).not.toContain('blush.exp3.json')
    layer.tick(0.05)
    expect(fake.writes.some((write) => write.id === 'ParamCheek' && write.value === 1)).toBe(true)
  })

  it('releases a parameter preset when it expires, leaving nothing pinned', () => {
    // Asserted on the override map, not on `writes`: the release is in the write log either way, and what matters
    // is that nothing writes the preset back at weight 1 after it.
    const fake = fakeRuntime({ present: ['ParamBrowLY', 'ParamMouthForm', 'ParamCheek'] })
    const layer = new ActionLayer(fake.runtime, mapping({ expressions: { angry: { kind: 'procedural' } } }), [], [], {
      holdSeconds: 0.5,
    })

    layer.expression('angry')
    fake.writes.length = 0
    for (let index = 0; index < 20; index += 1) layer.tick(0.05)

    expect(Object.fromEntries(overrides(fake.writes))).toEqual({})
  })

  it('leaves a preset that is still running in place', () => {
    // The other half of the same rule: a preset is not released the moment it starts.
    const fake = fakeRuntime({ present: ['ParamBrowLY', 'ParamMouthForm', 'ParamCheek'] })
    const layer = new ActionLayer(fake.runtime, mapping({ expressions: { angry: { kind: 'procedural' } } }), [], [])

    layer.expression('angry')
    fake.writes.length = 0
    layer.tick(0.05)

    expect(Object.fromEntries(overrides(fake.writes))).toEqual({
      ParamBrowLY: -1,
      ParamMouthForm: -1,
      ParamCheek: 0.3,
    })
  })

  it('treats an unknown mapping kind like auto', async () => {
    const fake = fakeRuntime({ present: allParams, groups: ['Greet'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'something-new' } } }),
      [{ group: 'Greet', no: 0, name: 'hello' }],
      [],
    )

    layer.play('greet')
    await Promise.resolve()
    expect(fake.motions).toEqual([{ group: 'Greet', no: 0, priority: 2 }])
  })
})

describe('procedural tracks', () => {
  it('skips a parameter the model does not have', () => {
    // A model with only the eye parameter: the stretch track's body keys must not be written at all.
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('stretch')
    layer.tick(0.5)

    expect(fake.writes.length).toBeGreaterThan(0)
    expect(new Set(fake.writes.map((write) => write.id))).toEqual(new Set(['ParamEyeLOpen', 'ParamEyeROpen']))
  })

  it('releases a present parameter with weight 0 when the track ends', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('blink')
    layer.tick(0.1)
    const during = fake.writes.length
    expect(during).toBeGreaterThan(0)

    layer.tick(0.3)
    const last = fake.writes.filter((write) => write.id === 'ParamEyeLOpen').at(-1)
    expect(last?.weight).toBe(0)
  })

  it('holds the sleep pose instead of releasing it', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen', 'ParamAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('sleep')
    // Ticks are clamped to a quarter second, as a real frame is: advance past the track's two seconds.
    for (let index = 0; index < 12; index += 1) layer.tick(0.2)

    const eye = fake.writes.filter((write) => write.id === 'ParamEyeLOpen').at(-1)
    expect(eye?.weight).toBe(1)
    expect(eye?.value).toBeCloseTo(0)
  })

  it('releases the look parameters at rest instead of blending them', () => {
    const fake = fakeRuntime({ present: ['ParamAngleX', 'ParamAngleY', 'ParamEyeBallX', 'ParamEyeBallY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.look(1, 1)
    layer.tick(0.2)
    expect(fake.writes.filter((write) => write.weight === 0.6).length).toBeGreaterThan(0)

    // Back to centre and settled: every look parameter has to be handed back, or the framework's own breathing
    // would stay damped forever.
    layer.look(0, 0)
    for (let index = 0; index < 40; index += 1) layer.tick(0.05)
    const tail = fake.writes.slice(-4)
    expect(tail.map((write) => write.weight)).toEqual([0, 0, 0, 0])
  })

  it('closes the mouth when speaking stops', () => {
    const fake = fakeRuntime({ present: ['ParamMouthOpenY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.mouth(true)
    layer.tick(0.2)
    layer.mouth(false)

    const last = fake.writes.filter((write) => write.id === 'ParamMouthOpenY').at(-1)
    expect(last?.weight).toBe(0)
  })

  it('returns to the profile default expression', () => {
    const fake = fakeRuntime({ present: allParams })
    const layer = new ActionLayer(fake.runtime, mapping(), [], ['smile.exp3.json'], {
      defaultExpression: 'live2d_expression0.exp3.json',
    })

    layer.expression('smile')
    expect(fake.expressions.at(-1)).toBe('smile.exp3.json')

    layer.releaseExpression()
    expect(fake.expressions.at(-1)).toBe('live2d_expression0.exp3.json')
  })

  it('expires a held expression after the configured seconds', () => {
    const fake = fakeRuntime({ present: allParams })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [], { holdSeconds: 1 })

    layer.expression('cry')
    for (let index = 0; index < 25; index += 1) layer.tick(0.05)
    // Well past the hold: the preset parameters were released and the default expression is back.
    expect(fake.expressions.at(-1)).toBe('')
    expect(fake.writes.some((write) => write.weight === 0)).toBe(true)
  })
})
describe('one write per parameter per frame', () => {
  // The fake runtime above keeps every write, but the model keeps only the last one per id. Composing a frame
  // before writing is what the real layer has to do, so these read the override map rather than the write log.

  it('adds the cursor look to a nod instead of replacing it', () => {
    const fake = fakeRuntime({ present: ['ParamAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.look(0, -1)
    for (let index = 0; index < 10; index += 1) layer.tick(0.05)
    const lookAlone = overrides(fake.writes).get('ParamAngleY') ?? 0
    expect(lookAlone).toBeGreaterThan(0)

    layer.play('nod')
    // Early in the nod the track pulls the head down while the look pushes it up; the composed value has to sit
    // strictly between them, which it cannot if either source overwrote the other.
    layer.tick(0.05)
    const composed = overrides(fake.writes).get('ParamAngleY') ?? 0
    expect(composed).toBeLessThan(lookAlone)
    expect(composed).toBeGreaterThan(lookAlone - 15)
  })

  it('keeps a track visible when an expression preset drives the same parameter', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.expression('cry')
    layer.tick(0.1)
    layer.play('blink')
    layer.tick(0.05)

    // The blink closes the eyes; the cry preset's half-open lids must not be the value that survives the frame.
    const eye = overrides(fake.writes).get('ParamEyeLOpen')
    expect(eye).toBeDefined()
    expect(eye).toBeLessThan(1)
  })

  it('never writes the same parameter twice in one tick', () => {
    const fake = fakeRuntime({ present: allParams })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.expression('shy')
    layer.look(0.5, 0.5)
    layer.play('headShake')
    layer.play('dragSpring', { velocityX: 40 })
    fake.writes.length = 0

    layer.tick(0.05)
    const ids = fake.writes.map((write) => write.id)
    expect(new Set(ids).size).toBe(ids.length)
  })
})

describe('sleeping and waking', () => {
  it('opens the eyes again after the held sleep track is released', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen', 'ParamAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('sleep')
    for (let index = 0; index < 12; index += 1) layer.tick(0.2)
    expect(overrides(fake.writes).get('ParamEyeLOpen')).toBeCloseTo(0)

    // Starting the wake track drops the held sleep pose and eases the eyes open over its 0.6s.
    layer.play('wake')
    for (let index = 0; index < 2; index += 1) layer.tick(0.2)
    expect(overrides(fake.writes).get('ParamEyeLOpen')).toBeGreaterThan(0.5)

    // The third tick reaches the track's end, which releases the parameter so the framework's own open eyes win,
    // and the sleep track must not outlive the wake and write zero again on later frames.
    layer.tick(0.2)
    expect(overrides(fake.writes).has('ParamEyeLOpen')).toBe(false)

    const closed = fake.writes.filter((write) => write.id === 'ParamEyeLOpen' && write.weight > 0)
    for (let index = 0; index < 20; index += 1) layer.tick(0.2)
    expect(fake.writes.filter((write) => write.id === 'ParamEyeLOpen' && write.weight > 0).length).toBe(
      closed.length,
    )
  })
})

describe('expression resolution', () => {
  it('expires an expression the model supplies as an .exp3 file', () => {
    const fake = fakeRuntime({ present: [] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], ['angry.exp3.json'], { holdSeconds: 1 })

    layer.expression('angry')
    expect(fake.expressions.at(-1)).toBe('angry.exp3.json')

    // The preset parameter list is empty for an .exp3, so the hold timer must not hang on it.
    for (let index = 0; index < 25; index += 1) layer.tick(0.05)
    expect(fake.expressions.at(-1)).toBe('')
  })
})

describe('keyword boundaries', () => {
  it('does not match a short ASCII keyword inside another word', async () => {
    // `hi` must not claim `this_motion`: model motion names are full of English words, and a greeting that fires
    // on a body tap is worse than one that does not fire at all.
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'ParamBodyAngleZ'], groups: ['ThisIsFine'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'ThisIsFine', no: 0, name: 'shine' }], [])

    layer.play('greet')
    await Promise.resolve()

    // No motion matched, so the procedural greeting track ran instead.
    expect(fake.motions).toEqual([])
    layer.tick(0.1)
    expect(fake.writes.some((write) => write.id === 'ParamAngleZ' || write.id === 'ParamBodyAngleX')).toBe(true)
  })

  it('still matches a keyword that is a whole word or a prefix of one', async () => {
    const fake = fakeRuntime({ groups: ['Hi'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'Hi', no: 1, name: 'hi_01' }], [])

    layer.play('greet')
    await Promise.resolve()
    expect(fake.motions).toEqual([{ group: 'Hi', no: 1, priority: 2 }])
  })
})

describe('drag reaction', () => {
  it('leans the body away from the drag, in the drag direction', () => {
    const fake = fakeRuntime({ present: ['ParamBodyAngleX', 'ParamBodyAngleZ', 'ParamAngleX'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    // Dragging right: the sprite hangs back, so the body tips left (a negative angle).
    layer.play('dragSpring', { velocityX: 600, velocityY: 0, priority: 3 })
    layer.tick(0.05)
    const rightward = fake.writes.filter((write) => write.id === 'ParamBodyAngleX').at(-1)?.value ?? 0
    expect(rightward).toBeLessThan(0)

    layer.stopAction('dragSpring')
    fake.writes.length = 0

    layer.play('dragSpring', { velocityX: -600, velocityY: 0, priority: 3 })
    layer.tick(0.05)
    const leftward = fake.writes.filter((write) => write.id === 'ParamBodyAngleX').at(-1)?.value ?? 0
    expect(leftward).toBeGreaterThan(0)
  })

  it('does not pump the spring when pointer moves repeat the same velocity', () => {
    const fake = fakeRuntime({ present: ['ParamBodyAngleX'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    // A pointer event per frame for a second of steady dragging: the lean must stay bounded, not accumulate.
    let peak = 0
    for (let index = 0; index < 60; index += 1) {
      layer.play('dragSpring', { velocityX: 400, velocityY: 0, priority: 3 })
      layer.tick(1 / 60)
      const value = fake.writes.filter((write) => write.id === 'ParamBodyAngleX').at(-1)?.value ?? 0
      peak = Math.max(peak, Math.abs(value))
    }
    expect(peak).toBeLessThanOrEqual(18)
  })

  it('settles back to rest and stops writing the body parameters', () => {
    const fake = fakeRuntime({ present: ['ParamBodyAngleX', 'ParamBodyAngleY', 'ParamBodyAngleZ', 'ParamAngleX'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('dragSpring', { velocityX: 400, velocityY: 0, priority: 3 })
    for (let index = 0; index < 20; index += 1) layer.tick(0.05)

    // The spring's tail must reach zero: a body left at 0.001° would stay pinned forever. The look layer keeps
    // writing releases at rest, which is correct, so this counts only the spring's own parameters.
    const body = () => fake.writes.filter((write) => write.id.startsWith('ParamBodyAngle'))
    const quiet = body().length
    for (let index = 0; index < 40; index += 1) layer.tick(0.05)

    expect(body().length).toBe(quiet)
    // The last thing written is the weight-0 release, one tick after the spring snapped shut; without it the
    // parameters would stay pinned at their last subtle angle.
    const last = body().at(-1)
    expect(last?.weight).toBe(0)
    expect(last?.value).toBe(0)
  })
})

describe('tap zone', () => {
  it('prefers the model\'s own tap motion for the zone that was hit', async () => {
    const fake = fakeRuntime({
      groups: ['TapHead', 'TapBody'],
    })
    const layer = new ActionLayer(
      fake.runtime,
      mapping(),
      [
        { group: 'TapHead', no: 0, name: 'pat' },
        { group: 'TapBody', no: 1, name: 'poke' },
      ],
      [],
    )

    // The director passes the zone only to the first step of a tap scene.
    layer.play('pat', { tapZone: 'head' })
    await Promise.resolve()

    // `pat` has no keyword that matches a model name, so without the zone it would fall back to the procedural track.
    expect(fake.motions).toEqual([{ group: 'TapHead', no: 0, priority: 2 }])
  })

  it('uses the body group when the body was hit', async () => {
    const fake = fakeRuntime({ groups: ['TapHead', 'TapBody'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping(),
      [
        { group: 'TapHead', no: 0, name: 'pat' },
        { group: 'TapBody', no: 1, name: 'poke' },
      ],
      [],
    )

    layer.play('shrink', { tapZone: 'body' })
    await Promise.resolve()

    expect(fake.motions).toEqual([{ group: 'TapBody', no: 1, priority: 2 }])
  })

  it('falls back to a generic Tap group, and then to the procedural track', async () => {
    const generic = fakeRuntime({ groups: ['Tap'] })
    const withTap = new ActionLayer(generic.runtime, mapping(), [{ group: 'Tap', no: 3, name: 'm03' }], [])
    withTap.play('tilt', { tapZone: 'face' })
    await Promise.resolve()
    expect(generic.motions).toEqual([{ group: 'Tap', no: 3, priority: 2 }])

    const bare = fakeRuntime({ present: ['ParamAngleZ'] })
    const withoutTap = new ActionLayer(bare.runtime, mapping(), [], [])
    withoutTap.play('tilt', { tapZone: 'face' })
    await Promise.resolve()
    bare.writes.length = 0
    withoutTap.tick(0.05)
    expect(bare.writes.some((write) => write.id === 'ParamAngleZ')).toBe(true)
  })

  it('picks one of the group\'s motions at random instead of always the first', async () => {
    const fake = fakeRuntime({ groups: ['Tap'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping(),
      [
        { group: 'Tap', no: 0, name: 'a' },
        { group: 'Tap', no: 1, name: 'b' },
        { group: 'Tap', no: 2, name: 'c' },
      ],
      [],
      { random: () => 0.99 },
    )

    layer.play('tilt', { tapZone: 'face' })
    await Promise.resolve()

    expect(fake.motions).toEqual([{ group: 'Tap', no: 2, priority: 2 }])
  })

  it('never falls into the model\'s Tap group once a scene\'s first step has used the zone', async () => {
    // The zone is per play: actions the director plays without it stay off the model's Tap group.
    const fake = fakeRuntime({ groups: ['TapHead'], present: ['ParamEyeLOpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'TapHead', no: 0, name: 'pat' }], [])

    layer.play('pat', { tapZone: 'head' })
    await Promise.resolve()
    expect(fake.motions).toEqual([{ group: 'TapHead', no: 0, priority: 2 }])

    layer.play('blink')
    layer.play('wake')
    layer.play('greet')
    await Promise.resolve()
    expect(fake.motions).toHaveLength(1)
  })
})
