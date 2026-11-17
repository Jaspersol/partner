// @vitest-environment node
// The action layer talks to a model only through the small `ActionRuntime` interface and the per-update
// `FrameParameters`, so fakes of both are the whole fixture: no Pixi, no WebGL, no Live2D.
import { describe, expect, it, vi } from 'vitest'

import { ActionLayer, type ActionMapping, type ActionName, type ActionRuntime } from '../src/pet/actions'
import { lookTarget } from '../src/pet/input'

interface MotionCall {
  group: string
  no: number
  priority: number
}

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

/**
 * A stand-in for the sprite. `present` is the set of parameters the model has; anything else answers null from
 * `parameterRange`, which is exactly what a real model does for a parameter it does not carry. `refuse` lists groups
 * that exist but whose motion does not start, as easy-live2d answers when a higher-priority motion is playing.
 */
function fakeRuntime(options: { present?: string[]; groups?: string[]; refuse?: string[] } = {}) {
  const present = new Set(options.present ?? [])
  const groups = new Set(options.groups ?? [])
  const refuse = new Set(options.refuse ?? [])
  const motions: MotionCall[] = []
  const expressions: string[] = []

  const runtime: ActionRuntime = {
    playMotion: async (group, no, priority) => {
      if (!groups.has(group) || refuse.has(group)) return false
      motions.push({ group, no, priority })
      return true
    },
    parameterRange: (id) => (present.has(id) ? rangeOf(id) : null),
    setExpression: (name) => {
      expressions.push(name)
    },
  }

  /**
   * One model update: every present parameter starts from what the model's motion, blink and breath left
   * (`base`, else open eyes and zero elsewhere), the layer applies its frame, and the model clamps each write.
   */
  const update = (
    layer: ActionLayer,
    base: Record<string, number> = {},
    motionPlaying = false,
  ): Map<string, number> => {
    const values = new Map<string, number>()
    for (const id of present) values.set(id, base[id] ?? (id === 'ParamEyeLOpen' || id === 'ParamEyeROpen' ? 1 : 0))
    const clamp = (id: string, value: number) => Math.max(rangeOf(id).min, Math.min(rangeOf(id).max, value))
    layer.apply({
      setParameterValueById: (id, value) => {
        if (values.has(id)) values.set(id, clamp(id, value))
      },
      addParameterValueById: (id, value) => {
        const current = values.get(id)
        if (current !== undefined) values.set(id, clamp(id, current + value))
      },
      isMotionFinished: () => !motionPlaying,
    })
    return values
  }

  return { runtime, motions, expressions, update }
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

function mapping(overrides: Partial<ActionMapping> = {}): ActionMapping {
  return { motions: {}, expressions: {}, headBottom: 0.35, faceBottom: 0.55, ...overrides }
}

describe('auto mapping by name', () => {
  it('matches an action to a group named after it', async () => {
    const fake = fakeRuntime({ groups: ['Greet'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'Greet', no: 2, name: 'Greet_2' }], [])

    layer.play('greet')
    await Promise.resolve()

    expect(fake.motions).toEqual([{ group: 'Greet', no: 2, priority: 2 }])
  })

  it('does not play an Idle group for a stretch', async () => {
    // An idle motion is the model breathing in place; playing it for a stretch looks like nothing happened.
    const fake = fakeRuntime({ groups: ['Idle'], present: ['ParamBodyAngleY', 'ParamAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'Idle', no: 0, name: 'Idle_0' }], [])

    layer.play('stretch')
    await Promise.resolve()
    for (let index = 0; index < 20; index += 1) layer.tick(0.05)

    expect(fake.motions).toEqual([])
    expect(fake.update(layer).get('ParamBodyAngleY')).toBeGreaterThan(0)
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
        { group: 'TapBody', no: 0, name: 'TapBody_0' },
        { group: 'CAT', no: 1, name: 'CAT_1' },
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

describe('procedural parameter configuration', () => {
  it('maps tracks, look, expression and mouth to explicit model channels', () => {
    const fake = fakeRuntime({ present: ['ParamAngleCustom', 'ParamAngleLook', 'CustomCheek', 'CustomMouth'] })
    const layer = new ActionLayer(fake.runtime, mapping({
      parameters: { ParamAngleZ: 'ParamAngleCustom', ParamAngleX: 'ParamAngleLook', ParamCheek: 'CustomCheek', ParamMouthOpenY: 'CustomMouth' },
    }), [], [], { random: () => 1 })
    layer.play('greet')
    layer.tick(0.2)
    expect(fake.update(layer).get('ParamAngleCustom')).toBeGreaterThan(0)
    layer.expression('shy')
    layer.tick(0.1)
    expect(fake.update(layer).get('CustomCheek')).toBe(1)
    layer.look(1, 0)
    const random = vi.spyOn(Math, 'random').mockReturnValue(1)
    layer.mouth(true)
    layer.tick(0.1)
    random.mockRestore()
    expect(fake.update(layer).get('ParamAngleLook')).toBeGreaterThan(0)
    expect(fake.update(layer).get('CustomMouth')).toBeGreaterThan(0)
  })

  it('combines model and action gains and drives an explicit extra track', () => {
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'CustomArm'] })
    const layer = new ActionLayer(fake.runtime, mapping({
      amplitude: 0.5,
      procedural: { greet: { amplitude: 0.5, extraTracks: [
        { id: 'CustomArm', keys: [{ at: 0, value: 0 }, { at: 0.6, value: -2 }, { at: 2, value: 0 }] },
      ] } },
    }), [], [])
    layer.play('greet')
    for (let index = 0; index < 3; index += 1) layer.tick(0.2)
    const values = fake.update(layer)
    expect(values.get('ParamAngleZ')).toBeCloseTo(2.25)
    expect(values.get('CustomArm')).toBeCloseTo(-0.5)
    for (let index = 0; index < 8; index += 1) layer.tick(0.2)
    expect(fake.update(layer).get('CustomArm')).toBe(0)
  })

  it('does not run configured procedural offsets when a model motion succeeds', async () => {
    const fake = fakeRuntime({ present: ['ParamAngleZ'], groups: ['Greet'] })
    const layer = new ActionLayer(fake.runtime, mapping({ amplitude: 3 }), [{ group: 'Greet', no: 0, name: 'Greet' }], [])
    layer.play('greet')
    await Promise.resolve()
    layer.tick(0.2)
    expect(fake.motions).toHaveLength(1)
    expect(fake.update(layer).get('ParamAngleZ')).toBe(0)
  })

  it('uses configured channels and gain when a model motion is refused', async () => {
    const fake = fakeRuntime({ present: ['ParamAngleCustom'], groups: ['Greet'], refuse: ['Greet'] })
    const layer = new ActionLayer(fake.runtime, mapping({
      parameters: { ParamAngleZ: 'ParamAngleCustom' }, amplitude: 0.5,
    }), [{ group: 'Greet', no: 0, name: 'Greet' }], [])
    layer.play('greet')
    await Promise.resolve()
    for (let index = 0; index < 3; index += 1) layer.tick(0.2)
    expect(fake.update(layer).get('ParamAngleCustom')).toBeCloseTo(4.5)
  })

  it('ignores extra tracks that do not return to rest', () => {
    const fake = fakeRuntime({ present: ['CustomArm'] })
    const layer = new ActionLayer(fake.runtime, mapping({ procedural: { greet: { extraTracks: [
      { id: 'CustomArm', keys: [{ at: 0, value: 0 }, { at: 2, value: 1 }] },
    ] } } }), [], [])
    layer.play('greet')
    layer.tick(0.2)
    expect(fake.update(layer).get('CustomArm')).toBe(0)
  })

  it('treats extra track IDs as actual IDs even when a standard channel is remapped', () => {
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'ParamAngleCustom'] })
    const layer = new ActionLayer(fake.runtime, mapping({
      parameters: { ParamAngleZ: 'ParamAngleCustom' },
      procedural: { greet: { extraTracks: [{ id: 'ParamAngleZ', keys: [
        { at: 0, value: 0 }, { at: 0.6, value: -2 }, { at: 2, value: 0 },
      ] }] } },
    }), [], [])
    layer.play('greet')
    for (let index = 0; index < 3; index += 1) layer.tick(0.2)
    expect(fake.update(layer).get('ParamAngleZ')).toBeCloseTo(-2)
    expect(fake.update(layer).get('ParamAngleCustom')).toBeCloseTo(9)
  })

  it('reports model motion, available mapped channels and missing channels without playing', () => {
    const fake = fakeRuntime({ present: ['ParamAngleCustom'], groups: ['Greet'] })
    const layer = new ActionLayer(fake.runtime, mapping({ parameters: { ParamAngleZ: 'ParamAngleCustom' } }),
      [{ group: 'Greet', no: 0, name: 'Greet' }], [])
    const diagnostics = layer.diagnostics()
    const greet = diagnostics.find((item) => item.action === 'greet')
    expect(greet?.kind).toBe('motion')
    expect(greet?.parameters).toContainEqual({ channel: 'ParamAngleZ', id: 'ParamAngleCustom', min: -30, max: 30 })
    expect(greet?.missing).toContain('ParamBodyAngleZ')
    expect(diagnostics.find((item) => item.action === 'tilt')?.kind).toBe('procedural')
    expect(diagnostics.find((item) => item.action === 'nod')?.kind).toBe('skipped')
    expect(fake.motions).toHaveLength(0)
  })
})


describe('refused motions', () => {
  it('runs the procedural track when the model refuses the motion', async () => {
    // A tap motion at a higher priority is still playing: easy-live2d answers -1 and the motion never starts.
    const fake = fakeRuntime({ groups: ['Greet'], refuse: ['Greet'], present: ['ParamAngleZ', 'ParamBodyAngleZ'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'Greet', no: 0, name: 'Greet_0' }], [])

    layer.play('greet')
    await Promise.resolve()
    layer.tick(0.3)

    expect(fake.update(layer).get('ParamAngleZ')).toBeGreaterThan(1)
  })
})

describe('none and procedural', () => {
  it('skips an action mapped to none', async () => {
    const fake = fakeRuntime({ present: allParams, groups: ['Greet'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'none' } } }),
      [{ group: 'Greet', no: 0, name: 'Greet_0' }],
      [],
    )

    layer.play('greet')
    layer.tick(0.3)
    await Promise.resolve()

    expect(fake.motions).toEqual([])
    expect(fake.update(layer).get('ParamAngleZ')).toBe(0)
  })

  it('skips the name match when the mapping says procedural', async () => {
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'ParamBodyAngleZ'], groups: ['Greet'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'procedural' } } }),
      [{ group: 'Greet', no: 0, name: 'Greet_0' }],
      [],
    )

    layer.play('greet')
    layer.tick(0.3)
    await Promise.resolve()

    expect(fake.motions).toEqual([])
    expect(fake.update(layer).get('ParamAngleZ')).toBeGreaterThan(1)
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
    expect(fake.update(layer).get('ParamCheek')).toBe(1)
  })

  it('stops writing a parameter preset when it expires', () => {
    const fake = fakeRuntime({ present: ['ParamBrowLY', 'ParamMouthForm', 'ParamCheek'] })
    const layer = new ActionLayer(fake.runtime, mapping({ expressions: { angry: { kind: 'procedural' } } }), [], [], {
      holdSeconds: 0.5,
    })

    layer.expression('angry')
    layer.tick(0.05)
    expect(fake.update(layer).get('ParamBrowLY')).toBe(-1)

    for (let index = 0; index < 20; index += 1) layer.tick(0.05)
    // The model's own values come back: nothing is pinned once the face is over.
    expect(Object.fromEntries(fake.update(layer, { ParamBrowLY: 0.3 }))).toEqual({
      ParamBrowLY: 0.3,
      ParamMouthForm: 0,
      ParamCheek: 0,
    })
  })

  it('treats an unknown mapping kind like auto', async () => {
    const fake = fakeRuntime({ present: allParams, groups: ['Greet'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping({ motions: { greet: { kind: 'something-new' } } }),
      [{ group: 'Greet', no: 0, name: 'Greet_0' }],
      [],
    )

    layer.play('greet')
    await Promise.resolve()
    expect(fake.motions).toEqual([{ group: 'Greet', no: 0, priority: 2 }])
  })
})

describe('procedural tracks', () => {
  it('writes only the parameters the model has', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('stretch')
    layer.tick(1.3 / 6)
    for (let index = 0; index < 5; index += 1) layer.tick(1.3 / 6)

    const values = fake.update(layer)
    expect([...values.keys()].sort()).toEqual(['ParamEyeLOpen', 'ParamEyeROpen'])
    expect(values.get('ParamEyeLOpen')).toBeLessThan(0.7)
  })

  it('adds to the model\'s own motion and breath instead of replacing them', () => {
    // F1: the offsets ride on top of what the model already set this update, so its motion and breathing survive.
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'ParamBodyAngleZ'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('greet')
    for (let index = 0; index < 3; index += 1) layer.tick(0.2)
    const alone = fake.update(layer).get('ParamAngleZ') ?? 0
    const onBreath = fake.update(layer, { ParamAngleZ: 4, ParamBodyAngleZ: -2 })

    expect(alone).toBeGreaterThan(5)
    expect(onBreath.get('ParamAngleZ')).toBeCloseTo(alone + 4)
    expect(onBreath.get('ParamBodyAngleZ')).toBeCloseTo((fake.update(layer).get('ParamBodyAngleZ') ?? 0) - 2)
  })

  it('leaves the model untouched once a track ends', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('blink')
    layer.tick(0.07)
    expect(fake.update(layer).get('ParamEyeLOpen')).toBeLessThan(0.2)

    layer.tick(0.3)
    // The framework's own blink, half way closed this update, is what shows.
    expect(fake.update(layer, { ParamEyeLOpen: 0.4 }).get('ParamEyeLOpen')).toBe(0.4)
  })

  it('eases every track back to rest, so nothing snaps when it ends', () => {
    // A track whose last key is off rest drops to 0 in the one update after it ends; with the writes feeding
    // physics, that snap whips the hair. `sleep` holds on purpose and `wake` starts from its pose, so neither is here.
    const actions: ActionName[] = [
      'blink', 'stretch', 'greet', 'wave', 'headTurn', 'headShake', 'bodyTurn', 'bodyShake',
      'nod', 'shake', 'tilt', 'pat', 'shrink', 'surprise', 'puff', 'sob',
    ]
    const present = [...allParams, 'ParamBrowLY', 'ParamBrowRY']
    for (const action of actions) {
      const fake = fakeRuntime({ present })
      const layer = new ActionLayer(fake.runtime, mapping(), [], [])
      const rest = fake.update(layer)
      layer.play(action)

      const frames: Map<string, number>[] = []
      for (let index = 0; index < 100; index += 1) {
        layer.tick(1 / 30)
        frames.push(fake.update(layer))
      }

      for (const id of present) {
        const resting = rest.get(id) ?? 0
        const last = frames.findLastIndex((frame) => frame.get(id) !== resting)
        if (last < 0) continue
        const { min, max } = rangeOf(id)
        // The last off-rest update is the jump the model sees when the track ends: under 5% of the parameter's span.
        expect(Math.abs((frames[last]?.get(id) ?? 0) - resting), `${action} ${id}`).toBeLessThan((max - min) * 0.05)
      }
    }
  })

  it('holds the sleep pose instead of ending it', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen', 'ParamAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('sleep')
    for (let index = 0; index < 30; index += 1) layer.tick(0.2)

    const values = fake.update(layer)
    expect(values.get('ParamEyeLOpen')).toBe(0)
    expect(values.get('ParamAngleY')).toBeCloseTo(-10)
  })

  it('closes the mouth when speaking stops', () => {
    const fake = fakeRuntime({ present: ['ParamMouthOpenY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.mouth(true)
    let opened = false
    for (let index = 0; index < 10 && !opened; index += 1) {
      layer.tick(0.1)
      opened = (fake.update(layer).get('ParamMouthOpenY') ?? 0) > 0
    }
    expect(opened).toBe(true)

    layer.mouth(false)
    layer.tick(0.05)
    expect(fake.update(layer).get('ParamMouthOpenY')).toBe(0)
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

  it('expires an expression the model supplies as an .exp3 file', () => {
    const fake = fakeRuntime({ present: [] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], ['angry.exp3.json'], { holdSeconds: 1 })

    layer.expression('angry')
    expect(fake.expressions.at(-1)).toBe('angry.exp3.json')

    // The preset parameter list is empty for an .exp3, so the hold timer must not hang on it.
    for (let index = 0; index < 25; index += 1) layer.tick(0.05)
    expect(fake.expressions.at(-1)).toBe('')
  })

  it('keeps a blink visible on top of a crying face', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.expression('cry')
    layer.tick(0.1)
    expect(fake.update(layer).get('ParamEyeLOpen')).toBe(0.5)

    layer.play('blink')
    layer.tick(0.07)
    expect(fake.update(layer).get('ParamEyeLOpen')).toBeLessThan(0.1)
  })
})

describe('cursor look', () => {
  it('keeps the head well inside its range at full deflection', () => {
    const fake = fakeRuntime({ present: ['ParamAngleX', 'ParamAngleY', 'ParamEyeBallX', 'ParamEyeBallY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.look(1, -1)
    for (let index = 0; index < 40; index += 1) layer.tick(0.05)
    const values = fake.update(layer)

    // 40% of a ±30 half range: ±12, leaving room for a nod or a stretch on top.
    expect(values.get('ParamAngleX')).toBeCloseTo(12, 0)
    expect(values.get('ParamAngleY')).toBeCloseTo(12, 0)
    expect(values.get('ParamEyeBallX')).toBeCloseTo(1, 1)
  })

  it('lets a stretch show while the cursor sits off the top-right corner', () => {
    // R2: with the head pinned at +30 by the look, the stretch's head pitch was clipped to nothing.
    const fake = fakeRuntime({ present: ['ParamAngleX', 'ParamAngleY', 'ParamBodyAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.look(1, -1)
    for (let index = 0; index < 40; index += 1) layer.tick(0.05)

    layer.play('stretch')
    const pitch: number[] = []
    for (let index = 0; index < 52; index += 1) {
      layer.tick(0.05)
      pitch.push(fake.update(layer).get('ParamAngleY') ?? 0)
    }

    expect(Math.max(...pitch) - Math.min(...pitch)).toBeGreaterThanOrEqual(8)
    expect(Math.max(...pitch)).toBeLessThan(30)
  })

  it('gives way while a model motion plays and comes back after', () => {
    const fake = fakeRuntime({ present: ['ParamAngleX'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.look(1, 0)
    for (let index = 0; index < 40; index += 1) layer.tick(0.05)

    for (let index = 0; index < 10; index += 1) {
      fake.update(layer, {}, true)
      layer.tick(0.05)
    }
    expect(fake.update(layer, {}, true).get('ParamAngleX')).toBeLessThan(3)

    for (let index = 0; index < 10; index += 1) {
      fake.update(layer)
      layer.tick(0.05)
    }
    expect(fake.update(layer).get('ParamAngleX')).toBeCloseTo(12, 0)
  })

  it('fades out as the cursor moves away from the window', () => {
    // A 200×400 window: the edge looks at full strength, one window width beyond it not at all.
    expect(lookTarget(200, 0, 200, 400)).toEqual({ x: 1, y: -1 })
    const near = lookTarget(300, 200, 200, 400)
    expect(near.x).toBeCloseTo(0.5)
    expect(near.y).toBe(0)
    expect(lookTarget(500, -1000, 200, 400)).toEqual({ x: 0, y: -0 })
  })
})

describe('sleeping and waking', () => {
  it('opens the eyes again after waking and does not close them later', () => {
    const fake = fakeRuntime({ present: ['ParamEyeLOpen', 'ParamEyeROpen', 'ParamAngleY'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('sleep')
    for (let index = 0; index < 12; index += 1) layer.tick(0.2)
    expect(fake.update(layer).get('ParamEyeLOpen')).toBe(0)

    layer.play('wake')
    for (let index = 0; index < 2; index += 1) layer.tick(0.2)
    expect(fake.update(layer).get('ParamEyeLOpen')).toBeGreaterThan(0.5)

    for (let index = 0; index < 20; index += 1) layer.tick(0.2)
    expect(fake.update(layer).get('ParamEyeLOpen')).toBe(1)
  })
})

describe('keyword boundaries', () => {
  it('does not match a short ASCII keyword inside another word', async () => {
    // `hi` must not claim `ThisIsFine`: model group names are full of English words, and a greeting that fires on a
    // body tap is worse than one that does not fire at all.
    const fake = fakeRuntime({ present: ['ParamAngleZ', 'ParamBodyAngleZ'], groups: ['ThisIsFine'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'ThisIsFine', no: 0, name: 'ThisIsFine_0' }], [])

    layer.play('greet')
    await Promise.resolve()

    // No motion matched, so the procedural greeting track ran instead.
    expect(fake.motions).toEqual([])
    layer.tick(0.3)
    expect(fake.update(layer).get('ParamAngleZ')).toBeGreaterThan(1)
  })

  it('still matches a keyword that is a whole word or a prefix of one', async () => {
    const fake = fakeRuntime({ groups: ['Hi_01'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'Hi_01', no: 1, name: 'Hi_01_1' }], [])

    layer.play('greet')
    await Promise.resolve()
    expect(fake.motions).toEqual([{ group: 'Hi_01', no: 1, priority: 2 }])
  })
})

describe('drag reaction', () => {
  it('leans the body away from the drag, in the drag direction', () => {
    const fake = fakeRuntime({ present: ['ParamBodyAngleX', 'ParamBodyAngleZ', 'ParamAngleX'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    // Dragging right: the sprite hangs back, so the body tips left (a negative angle).
    layer.play('dragSpring', { velocityX: 600, velocityY: 0, priority: 3 })
    layer.tick(0.05)
    expect(fake.update(layer).get('ParamBodyAngleX')).toBeLessThan(0)

    layer.stopAction('dragSpring')
    layer.play('dragSpring', { velocityX: -600, velocityY: 0, priority: 3 })
    layer.tick(0.05)
    expect(fake.update(layer).get('ParamBodyAngleX')).toBeGreaterThan(0)
  })

  it('does not pump the spring when pointer moves repeat the same velocity', () => {
    const fake = fakeRuntime({ present: ['ParamBodyAngleX'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    // A pointer event per frame for a second of steady dragging: the lean must stay bounded, not accumulate.
    let peak = 0
    for (let index = 0; index < 60; index += 1) {
      layer.play('dragSpring', { velocityX: 400, velocityY: 0, priority: 3 })
      layer.tick(1 / 60)
      peak = Math.max(peak, Math.abs(fake.update(layer).get('ParamBodyAngleX') ?? 0))
    }
    expect(peak).toBeLessThanOrEqual(18)
  })

  it('settles back to exactly the model\'s own pose', () => {
    const fake = fakeRuntime({ present: ['ParamBodyAngleX', 'ParamBodyAngleY', 'ParamBodyAngleZ'] })
    const layer = new ActionLayer(fake.runtime, mapping(), [], [])

    layer.play('dragSpring', { velocityX: 400, velocityY: 0, priority: 3 })
    for (let index = 0; index < 60; index += 1) layer.tick(0.05)

    // A body left at 0.001° would be pinned forever; the breath underneath has to come through unchanged.
    expect(Object.fromEntries(fake.update(layer, { ParamBodyAngleX: 2 }))).toEqual({
      ParamBodyAngleX: 2,
      ParamBodyAngleY: 0,
      ParamBodyAngleZ: 0,
    })
  })
})

describe('tap zone', () => {
  it('prefers the model\'s own tap motion for the zone that was hit', async () => {
    const fake = fakeRuntime({ groups: ['TapHead', 'TapBody'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping(),
      [
        { group: 'TapHead', no: 0, name: 'TapHead_0' },
        { group: 'TapBody', no: 1, name: 'TapBody_1' },
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
        { group: 'TapHead', no: 0, name: 'TapHead_0' },
        { group: 'TapBody', no: 1, name: 'TapBody_1' },
      ],
      [],
    )

    layer.play('shrink', { tapZone: 'body' })
    await Promise.resolve()

    expect(fake.motions).toEqual([{ group: 'TapBody', no: 1, priority: 2 }])
  })

  it('falls back to a generic Tap group, and then to the procedural track', async () => {
    const generic = fakeRuntime({ groups: ['Tap'] })
    const withTap = new ActionLayer(generic.runtime, mapping(), [{ group: 'Tap', no: 3, name: 'Tap_3' }], [])
    withTap.play('tilt', { tapZone: 'face' })
    await Promise.resolve()
    expect(generic.motions).toEqual([{ group: 'Tap', no: 3, priority: 2 }])

    const bare = fakeRuntime({ present: ['ParamAngleZ'] })
    const withoutTap = new ActionLayer(bare.runtime, mapping(), [], [])
    withoutTap.play('tilt', { tapZone: 'face' })
    await Promise.resolve()
    withoutTap.tick(0.3)
    expect(bare.update(withoutTap).get('ParamAngleZ')).toBeGreaterThan(10)
  })

  it('picks one of the group\'s motions at random instead of always the first', async () => {
    const fake = fakeRuntime({ groups: ['Tap'] })
    const layer = new ActionLayer(
      fake.runtime,
      mapping(),
      [
        { group: 'Tap', no: 0, name: 'Tap_0' },
        { group: 'Tap', no: 1, name: 'Tap_1' },
        { group: 'Tap', no: 2, name: 'Tap_2' },
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
    const layer = new ActionLayer(fake.runtime, mapping(), [{ group: 'TapHead', no: 0, name: 'TapHead_0' }], [])

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
