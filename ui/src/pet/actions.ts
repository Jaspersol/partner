/**
 * The action layer: everything the pet *does*, expressed as either a model motion, an expression preset, or a
 * procedural keyframe track over the model's parameters.
 *
 * A model may ship motions, expressions, neither, or ones named in a language nobody here reads, so each abstract
 * action resolves in a fixed order: the profile's explicit mapping, then a name match against what the model has,
 * then a procedural track; only when all three fail is the action skipped.
 *
 * Every parameter written here is released with weight 0 when its source ends. In easy-live2d 1.1.0 an override
 * persists across frames (it re-applies after motion, expression, breath and physics), so an unreleased one would
 * freeze that parameter and bury the framework's own breathing and blinking.
 */

export type ActionName =
  | 'blink'
  | 'stretch'
  | 'greet'
  | 'wave'
  | 'headTurn'
  | 'headShake'
  | 'bodyTurn'
  | 'bodyShake'
  | 'nod'
  | 'shake'
  | 'tilt'
  | 'pat'
  | 'shrink'
  | 'surprise'
  | 'puff'
  | 'sob'
  | 'sleep'
  | 'wake'
  | 'mouth'
  | 'dragSpring'
  | 'look'

export type ExpressionName = 'default' | 'smile' | 'angry' | 'cry' | 'shy' | 'surprise'

/**
 * Every action and expression the layer can play, for callers that receive a name from outside the page — the
 * settings page's 「预览」 is just a string over the wire, and an unknown one would throw deep inside the ticker.
 */
const ACTION_NAMES: readonly ActionName[] = [
  'blink',
  'stretch',
  'greet',
  'wave',
  'headTurn',
  'headShake',
  'bodyTurn',
  'bodyShake',
  'nod',
  'shake',
  'tilt',
  'pat',
  'shrink',
  'surprise',
  'puff',
  'sob',
  'sleep',
  'wake',
  'mouth',
  'dragSpring',
  'look',
]

const EXPRESSION_NAMES: readonly ExpressionName[] = ['default', 'smile', 'angry', 'cry', 'shy', 'surprise']

export function isActionName(value: string): value is ActionName {
  return (ACTION_NAMES as readonly string[]).includes(value)
}

export function isExpressionName(value: string): value is ExpressionName {
  return (EXPRESSION_NAMES as readonly string[]).includes(value)
}

export interface ActionRuntime {
  /** False when the group is unknown, so the caller can fall through to the procedural track. */
  playMotion(group: string, no: number, priority: 1 | 2 | 3): Promise<boolean>
  setParameter(id: string, value: number, weight?: number): void
  parameterRange(id: string): { min: number; max: number } | null
  /** Empty string means "no expression of my own": the model's own default look. */
  setExpression(name: string): void
}

interface MotionMapping {
  kind: string
  group?: string | null
  no?: number | null
}

interface ExpressionMapping {
  kind: string
  name?: string | null
}

/**
 * The mapping `PetModelProfile` carries, flattened. `kind` is `auto`, `motion`, `procedural`, `expression` or
 * `none`; anything unrecognised behaves like `auto` so a mapping written by a later build still plays.
 */
export interface ActionMapping {
  motions: Record<string, MotionMapping>
  expressions: Record<string, ExpressionMapping>
  headBottom: number
  faceBottom: number
}

interface ActionLayerOptions {
  /** How long an expression stays before it falls back. From `PetSettings.ExpressionHoldSeconds`. */
  holdSeconds?: number
  /** The model's own resting expression, from the profile; empty means the model's default. */
  defaultExpression?: string
  /** Source for the random pick within a model's own `Tap*` group. Defaults to `Math.random`. */
  random?: () => number
}

interface Keyframe {
  at: number
  value: number
}

interface ParameterTrack {
  id: string
  keys: Keyframe[]
}

interface TrackDefinition {
  duration: number
  params: ParameterTrack[]
  /** Keep the final value instead of releasing (sleeping); only `wake` undoes it. */
  hold?: boolean
}

interface RunningTrack {
  action: ActionName
  definition: TrackDefinition
  elapsed: number
  /** Parameters actually applied — a model without the parameter is not written at all. */
  live: ParameterTrack[]
}

interface FacePreset {
  /** Parameter → value. Values are clamped to each parameter's own range. */
  params: Record<string, number>
  expression?: string
}

const EYE_L = 'ParamEyeLOpen'
const EYE_R = 'ParamEyeROpen'

/** Two open/close keys for both eyes, so a blink and the half-lidded tracks share one shape. */
function eyeKeys(duration: number, closed: number): ParameterTrack[] {
  const mid = duration / 2
  return [
    { id: EYE_L, keys: [{ at: 0, value: 1 }, { at: mid, value: closed }, { at: duration, value: 1 }] },
    { id: EYE_R, keys: [{ at: 0, value: 1 }, { at: mid, value: closed }, { at: duration, value: 1 }] },
  ]
}

/**
 * The procedural fallback for each action. Values are model units (degrees for the angle parameters, a fraction for
 * the eye and cheek parameters), and each is clamped to the parameter's range before it is written.
 */
const TRACKS: Partial<Record<ActionName, TrackDefinition>> = {
  blink: {
    // 0.14s: a blink lasts 0.08–0.15s. Longer reads as a slow, sleepy blink.
    duration: 0.14,
    params: eyeKeys(0.14, 0),
  },
  stretch: {
    duration: 2.6,
    params: [
      { id: 'ParamBodyAngleY', keys: [{ at: 0, value: 0 }, { at: 1.3, value: 8 }, { at: 2.6, value: 0 }] },
      { id: 'ParamAngleY', keys: [{ at: 0, value: 0 }, { at: 1.3, value: 10 }, { at: 2.6, value: 0 }] },
      ...eyeKeys(2.6, 0.45),
    ],
  },
  greet: {
    duration: 2,
    // Amplitudes are deliberately larger than a motion capture of a real greeting: the pet is drawn at 360 logical
    // pixels tall, where 6 degrees of head roll is about two pixels and reads as nothing at all.
    params: [
      { id: 'ParamAngleZ', keys: [{ at: 0, value: 0 }, { at: 0.6, value: 9 }, { at: 1.3, value: -9 }, { at: 2, value: 0 }] },
      { id: 'ParamBodyAngleZ', keys: [{ at: 0, value: 0 }, { at: 0.6, value: 6 }, { at: 1.3, value: -6 }, { at: 2, value: 0 }] },
    ],
  },
  headTurn: {
    duration: 1.2,
    params: [{ id: 'ParamAngleX', keys: [{ at: 0, value: 0 }, { at: 0.6, value: 22 }, { at: 1.2, value: 0 }] }],
  },
  headShake: {
    duration: 1.4,
    params: [
      {
        id: 'ParamAngleZ',
        keys: [
          { at: 0, value: 0 },
          { at: 0.35, value: 5 },
          { at: 0.7, value: -5 },
          { at: 1.05, value: 5 },
          { at: 1.4, value: -5 },
        ],
      },
      {
        id: 'ParamAngleX',
        keys: [
          { at: 0, value: 0 },
          { at: 0.35, value: 4 },
          { at: 0.7, value: -4 },
          { at: 1.05, value: 4 },
          { at: 1.4, value: 0 },
        ],
      },
    ],
  },
  bodyTurn: {
    duration: 1.4,
    params: [{ id: 'ParamBodyAngleX', keys: [{ at: 0, value: 0 }, { at: 0.7, value: 8 }, { at: 1.4, value: -8 }] }],
  },
  bodyShake: {
    duration: 1.2,
    params: [
      {
        id: 'ParamBodyAngleZ',
        keys: [
          { at: 0, value: 0 },
          { at: 0.3, value: 6 },
          { at: 0.6, value: -6 },
          { at: 0.9, value: 6 },
          { at: 1.2, value: -6 },
        ],
      },
    ],
  },
  nod: {
    duration: 1.2,
    params: [
      {
        id: 'ParamAngleY',
        keys: [
          { at: 0, value: 0 },
          { at: 0.3, value: -10 },
          { at: 0.6, value: 0 },
          { at: 0.9, value: -10 },
          { at: 1.2, value: 0 },
        ],
      },
    ],
  },
  shake: {
    duration: 1.2,
    params: [
      {
        id: 'ParamAngleX',
        keys: [
          { at: 0, value: 0 },
          { at: 0.3, value: 12 },
          { at: 0.6, value: -12 },
          { at: 0.9, value: 12 },
          { at: 1.2, value: -12 },
        ],
      },
    ],
  },
  tilt: {
    duration: 1.2,
    params: [{ id: 'ParamAngleZ', keys: [{ at: 0, value: 0 }, { at: 0.2, value: 18 }, { at: 1, value: 18 }, { at: 1.2, value: 0 }] }],
  },
  pat: {
    duration: 1.4,
    params: [
      { id: 'ParamEyeLSmile', keys: [{ at: 0, value: 0 }, { at: 0.7, value: 1 }, { at: 1.4, value: 0 }] },
      { id: 'ParamEyeRSmile', keys: [{ at: 0, value: 0 }, { at: 0.7, value: 1 }, { at: 1.4, value: 0 }] },
      { id: 'ParamAngleY', keys: [{ at: 0, value: 0 }, { at: 0.7, value: -10 }, { at: 1.4, value: 0 }] },
    ],
  },
  shrink: {
    duration: 1.4,
    params: [
      { id: 'ParamBodyAngleY', keys: [{ at: 0, value: 0 }, { at: 0.7, value: -12 }, { at: 1.4, value: 0 }] },
      { id: 'ParamAngleY', keys: [{ at: 0, value: 0 }, { at: 0.7, value: -14 }, { at: 1.4, value: 0 }] },
    ],
  },
  surprise: {
    duration: 0.9,
    params: [
      { id: 'ParamAngleY', keys: [{ at: 0, value: 0 }, { at: 0.35, value: 12 }, { at: 0.9, value: 0 }] },
      { id: EYE_L, keys: [{ at: 0, value: 1 }, { at: 0.35, value: 1.2 }, { at: 0.9, value: 1 }] },
      { id: EYE_R, keys: [{ at: 0, value: 1 }, { at: 0.35, value: 1.2 }, { at: 0.9, value: 1 }] },
    ],
  },
  puff: {
    duration: 1.6,
    params: [{ id: 'ParamCheek', keys: [{ at: 0, value: 0 }, { at: 0.5, value: 1 }, { at: 1.1, value: 1 }, { at: 1.6, value: 0 }] }],
  },
  sob: {
    duration: 2.4,
    params: [
      {
        id: 'ParamAngleY',
        keys: [
          { at: 0, value: 0 },
          { at: 0.4, value: -4 },
          { at: 0.8, value: -2 },
          { at: 1.4, value: -5 },
          { at: 2.4, value: 0 },
        ],
      },
      { id: EYE_L, keys: [{ at: 0, value: 1 }, { at: 0.4, value: 0.5 }, { at: 2.4, value: 1 }] },
      { id: EYE_R, keys: [{ at: 0, value: 1 }, { at: 0.4, value: 0.5 }, { at: 2.4, value: 1 }] },
    ],
  },
  sleep: {
    duration: 2,
    hold: true,
    params: [
      { id: EYE_L, keys: [{ at: 0, value: 1 }, { at: 2, value: 0 }] },
      { id: EYE_R, keys: [{ at: 0, value: 1 }, { at: 2, value: 0 }] },
      { id: 'ParamAngleY', keys: [{ at: 0, value: 0 }, { at: 2, value: -10 }] },
    ],
  },
  wake: {
    duration: 0.6,
    params: [
      { id: EYE_L, keys: [{ at: 0, value: 0 }, { at: 0.6, value: 1 }] },
      { id: EYE_R, keys: [{ at: 0, value: 0 }, { at: 0.6, value: 1 }] },
      { id: 'ParamAngleY', keys: [{ at: 0, value: -10 }, { at: 0.6, value: 0 }] },
    ],
  },
}

/** Which action's keys a synonym uses: waving is a greeting with a different name, and nothing else. */
const TRACK_ALIASES: Partial<Record<ActionName, ActionName>> = {
  wave: 'greet',
}

/**
 * Expression presets, used when the model ships no `.exp3` for the expression or the profile forces the parameter
 * path.
 */
const EXPRESSION_PRESETS: Record<Exclude<ExpressionName, 'default'>, FacePreset> = {
  smile: {
    params: { ParamEyeLSmile: 1, ParamEyeRSmile: 1, 'ParamMouthForm': 1 },
  },
  angry: {
    params: { 'ParamBrowLY': -1, 'ParamBrowRY': -1, 'ParamMouthForm': -1, 'ParamCheek': 0.3 },
  },
  cry: {
    params: {
      'ParamBrowLY': 1,
      'ParamBrowRY': 1,
      'ParamEyeLOpen': 0.5,
      'ParamEyeROpen': 0.5,
      'ParamMouthForm': -0.6,
    },
  },
  shy: {
    params: { ParamCheek: 1, ParamEyeBallX: 0.6, 'ParamAngleY': -8 },
  },
  surprise: {
    // Wide eyes and raised brows: the same shape the `surprise` track bends, so a model without the parameter
    // pair still reads as startled.
    params: { 'ParamBrowLY': 1, 'ParamBrowRY': 1, 'ParamEyeLOpen': 1.15, 'ParamEyeROpen': 1.15, 'ParamMouthForm': 0.4 },
  },
}

/**
 * Name fragments that identify an action in a model's own vocabulary. Matched case-insensitively against the
 * motion's group and file name, so `CAT_motion#1` in group `TapBody` still counts as a tap.
 */
const MOTION_KEYWORDS: Partial<Record<ActionName, string[]>> = {
  greet: ['greet', 'hello', 'wave', 'hi'],
  wave: ['wave', 'greet', 'hello'],
  nod: ['nod'],
  shake: ['shake'],
  headShake: ['shake'],
  bodyShake: ['shake'],
  headTurn: ['turn'],
  bodyTurn: ['turn'],
  sleep: ['sleep'],
  blink: ['blink'],
  stretch: ['stretch', 'idle'],
}

/** The same idea for expressions: the name a model author would give each face. */
const EXPRESSION_KEYWORDS: Record<Exclude<ExpressionName, 'default'>, string[]> = {
  smile: ['smile', 'happy', '笑'],
  angry: ['angry', '怒', 'mad'],
  cry: ['sad', 'cry', '哭'],
  shy: ['shy', 'blush', '害羞'],
  surprise: ['surprise', 'shock', '惊讶', '吃惊'],
}

/** Keeps a drag-induced lean inside what a spine plausibly does: beyond ~18° the sprite folds. */
function clampSpring(value: number): number {
  return Math.max(-18, Math.min(18, value))
}

/**
 * Whether a model's own name contains a keyword. An ASCII keyword has to sit on a word boundary: `hi` is meant to
 * match `hi` and `hi_01`, not `this` or `shine`, and model motion names are full of English words like those. A
 * CJK keyword has no such boundaries and matches as a substring.
 */
function nameContains(haystack: string, keyword: string): boolean {
  if (!/^[\x20-\x7e]*$/.test(keyword)) return haystack.includes(keyword)

  const isWord = (character: string) => /[a-z0-9]/.test(character)
  for (let at = haystack.indexOf(keyword); at >= 0; at = haystack.indexOf(keyword, at + 1)) {
    const before = at === 0 ? '' : (haystack[at - 1] as string)
    const after = haystack[at + keyword.length] ?? ''
    if (!isWord(before) || !isWord(after)) return true
  }
  return false
}

function easeInOut(value: number): number {
  return value < 0.5 ? 2 * value * value : 1 - ((-2 * value + 2) ** 2) / 2
}

/** One frame's parameter writes, composed before anything reaches the model. See {@link ActionLayer.tick}. */
interface FrameWrite {
  value: number
  weight: number
}

type Frame = Map<string, FrameWrite>

/** Takes the parameter outright: whatever was composed for it this frame is replaced. */
function set(frame: Frame, id: string, value: number, weight: number): void {
  // A release (weight 0) still has to be sent, or the parameter stays pinned at its last value; but it must not
  // displace a real write, which is why it only lands where nothing has been written yet.
  if (weight === 0 && frame.has(id)) return
  frame.set(id, { value, weight })
}

/** Adds to whatever is already composed for the parameter, keeping the strongest weight. Used for offsets. */
function mix(frame: Frame, id: string, value: number, weight: number): void {
  const existing = frame.get(id)
  if (existing === undefined) {
    set(frame, id, value, weight)
    return
  }
  if (existing.weight === 0) {
    // A release followed by a real offset: the offset wins outright, and weight 0 no longer applies.
    set(frame, id, value, weight)
    return
  }
  frame.set(id, { value: existing.value + value, weight: Math.max(existing.weight, weight) })
}

/** The value a parameter track has at `elapsed`, easing between its neighbouring keys. */
function sample(keys: readonly Keyframe[], elapsed: number): number {
  if (elapsed <= (keys[0] as Keyframe).at) return (keys[0] as Keyframe).value
  const last = keys[keys.length - 1] as Keyframe
  if (elapsed >= last.at) return last.value

  for (let index = 1; index < keys.length; index += 1) {
    const right = keys[index] as Keyframe
    if (elapsed > right.at) continue
    const left = keys[index - 1] as Keyframe
    const span = right.at - left.at
    const fraction = span <= 0 ? 1 : easeInOut((elapsed - left.at) / span)
    return left.value + (right.value - left.value) * fraction
  }
  return last.value
}

/**
 * One model's action table: resolves abstract actions against what that model offers, runs the procedural tracks
 * it falls back to, and keeps the expression that is currently showing.
 */
export class ActionLayer {
  private readonly runtime: ActionRuntime
  private mapping: ActionMapping
  private motions: { group: string; no: number; name: string }[]
  private expressions: string[]
  private holdSeconds: number
  private defaultExpression: string
  private readonly random: () => number

  private readonly tracks = new Map<ActionName, RunningTrack>()
  private readonly presetParams = new Map<string, number>()
  private presetReleaseAt: number | null = null
  private presetElapsed = 0

  /** Low-passed look target, in -1..1, and the current smoothed value. */
  private lookTargetX = 0
  private lookTargetY = 0
  private lookX = 0
  private lookY = 0

  private mouthOpen = false
  private mouthElapsed = 0
  private mouthValue = 0

  /**
   * Damped spring for the drag reaction, in degrees: a target the drag sets, and an offset that springs toward it
   * and then back to rest. `springX/Z/Y` are the current offsets actually written to the model.
   */
  private springTargetX = 0
  private springTargetY = 0
  private springTargetZ = 0
  private springX = 0
  private springY = 0
  private springZ = 0
  private springVelocityX = 0
  private springVelocityY = 0
  private springVelocityZ = 0

  constructor(
    runtime: ActionRuntime,
    mapping: ActionMapping,
    motions: { group: string; no: number; name: string }[],
    expressions: string[],
    options: ActionLayerOptions = {},
  ) {
    this.runtime = runtime
    this.mapping = mapping
    this.motions = motions
    this.expressions = expressions
    this.holdSeconds = options.holdSeconds ?? 6
    this.defaultExpression = options.defaultExpression ?? ''
    this.random = options.random ?? Math.random
  }

  configure(
    mapping: ActionMapping,
    motions: { group: string; no: number; name: string }[],
    expressions: string[],
    options: ActionLayerOptions = {},
  ): void {
    this.releaseExpression()
    this.stopAll()
    this.mapping = mapping
    this.motions = motions
    this.expressions = expressions
    if (options.holdSeconds !== undefined) this.holdSeconds = options.holdSeconds
    if (options.defaultExpression !== undefined) this.defaultExpression = options.defaultExpression
  }

  // ------------------------------------------------------------------ playing

  /**
   * Runs one abstract action. `priority` is the scene's, mapped to easy-live2d's three levels: idle scenes must
   * not outrank a tap.
   */
  play(
    action: ActionName,
    options: { velocityX?: number; velocityY?: number; priority?: 1 | 2 | 3; tapZone?: 'head' | 'face' | 'body' } = {},
  ): void {
    const priority = options.priority ?? 2

    if (action === 'look') {
      // Always on; `look()` is what drives it, and playing it only makes sure the tick applies it.
      return
    }

    if (action === 'mouth') {
      this.mouthOpen = true
      return
    }

    if (action === 'dragSpring') {
      // The body leans away from the signed drag velocity, which is what a sprite hanging from the cursor does.
      // It sets the spring's *target* rather than adding to its velocity: pointer moves arrive dozens of times a
      // second, and an impulse per event would pump the spring into a blur, while a target is idempotent.
      const velocityX = options.velocityX ?? 0
      const velocityY = options.velocityY ?? 0
      const gain = 0.02
      this.springTargetX = clampSpring(-velocityX * gain)
      this.springTargetZ = clampSpring(velocityX * gain * 0.6)
      this.springTargetY = clampSpring(-velocityY * gain * 0.5)
      return
    }

    const mapping = this.mapping.motions[action]
    const kind = mapping?.kind ?? 'auto'

    if (kind === 'none') return

    if (kind === 'procedural') {
      this.startTrack(action)
      return
    }

    if (kind === 'motion') {
      const group = typeof mapping?.group === 'string' ? mapping.group : ''
      const no = typeof mapping?.no === 'number' ? mapping.no : 0
      if (group !== '') {
        void this.runtime.playMotion(group, no, priority).then((played) => {
          // A mapping to a group the model lost falls back rather than doing nothing at all.
          if (!played) this.startTrack(action)
        })
        return
      }
    }

    const motion = this.matchMotion(action)
    if (motion !== undefined) {
      void this.runtime.playMotion(motion.group, motion.no, priority).then((played) => {
        if (!played) this.startTrack(action)
      })
      return
    }

    // The first step of a tap scene may borrow the model's own `Tap*` motion: those groups say which part was hit
    // and are far more expressive than a 12° procedural tilt. The director passes the zone only for that first step,
    // so later actions never resolve to a tap motion.
    const tap = options.tapZone === undefined ? undefined : this.matchTapMotion(options.tapZone)
    if (tap !== undefined) {
      void this.runtime.playMotion(tap.group, tap.no, priority).then((played) => {
        if (!played) this.startTrack(action)
      })
      return
    }

    this.startTrack(action)
  }

  /**
   * Shows an expression for `holdSeconds`, then returns to default. The timeout is tick-driven rather than a
   * `setTimeout` so a hidden window does not come back to a face that already expired.
   */
  expression(name: ExpressionName): void {
    this.applyExpression(name)
    this.presetReleaseAt = name === 'default' ? null : this.holdSeconds
    this.presetElapsed = 0
  }

  releaseExpression(): void {
    for (const id of this.presetParams.keys()) {
      this.runtime.setParameter(id, 0, 0)
    }
    this.presetParams.clear()
    this.presetReleaseAt = null
    this.runtime.setExpression(this.defaultExpression)
  }

  /** Cursor position, each axis in -1..1 with 0 at the centre. Smoothing happens in {@link tick}. */
  look(x: number, y: number): void {
    this.lookTargetX = Math.max(-1, Math.min(1, x))
    this.lookTargetY = Math.max(-1, Math.min(1, y))
  }

  mouth(open: boolean): void {
    this.mouthOpen = open
    if (!open) {
      this.runtime.setParameter('ParamMouthOpenY', 0, 0)
      this.mouthValue = 0
    }
  }

  /** Stops one action. A held track (sleeping) never releases on its own, so the caller has to stop it. */
  stopAction(action: ActionName): void {
    if (action === 'mouth') {
      this.mouth(false)
      return
    }
    if (action === 'dragSpring') {
      this.springTargetX = 0
      this.springTargetY = 0
      this.springTargetZ = 0
      this.springX = 0
      this.springY = 0
      this.springZ = 0
      this.springVelocityX = 0
      this.springVelocityY = 0
      this.springVelocityZ = 0
      this.releaseSpring()
      return
    }
    if (action === 'look') {
      this.look(0, 0)
      return
    }

    const running = this.tracks.get(action)
    if (running === undefined) return
    this.tracks.delete(action)
    this.releaseTrack(running)
  }

  stopAll(): void {
    for (const running of this.tracks.values()) {
      this.releaseTrack(running)
    }
    this.tracks.clear()
    this.mouth(false)
    this.look(0, 0)
  }

  // ------------------------------------------------------------------ ticking

  /**
   * Advances every running track, the expression hold, the spring, the eyes and the mouth.
   *
   * Everything is composed into one map and written once, because an easy-live2d override is a map keyed by
   * parameter id: two writes to the same id in one frame leave only the last one standing. Precedence is
   * preset &lt; track &lt; spring &lt; look. A track is an authored motion, so it replaces a preset; the look and the
   * spring are continuous offsets, so they add to whatever a track already drives.
   */
  tick(dtSeconds: number): void {
    const dt = Math.max(0, Math.min(0.25, dtSeconds))
    const frame: Frame = new Map()
    const finished: RunningTrack[] = []

    // The expression timer runs first: a preset it releases this frame must not be written back at weight 1 from
    // `presetParams`. A model's own `.exp3` has no parameters, so the timer is not gated on the map being non-empty.
    this.tickExpression(dt)

    for (const [id, value] of this.presetParams) set(frame, id, value, 1)

    for (const running of [...this.tracks.values()]) {
      running.elapsed += dt

      const ended = running.elapsed >= running.definition.duration && running.definition.hold !== true
      if (!ended) {
        for (const parameter of running.live) {
          set(frame, parameter.id, sample(parameter.keys, running.elapsed), 1)
        }
        continue
      }
      this.tracks.delete(running.action)
      finished.push(running)
    }

    this.tickLook(dt, frame)
    this.tickMouth(dt, frame)
    this.tickSpring(dt, frame)

    for (const [id, write] of frame) this.write(id, write.value, write.weight)
    for (const running of finished) this.releaseTrack(running, frame)
  }

  private tickExpression(dt: number): void {
    if (this.presetReleaseAt !== null) {
      this.presetElapsed += dt
      if (this.presetElapsed >= this.presetReleaseAt) this.releaseExpression()
    }
  }

  private tickLook(dt: number, frame: Frame): void {
    // One-pole low pass: the eyes follow the cursor visibly but do not twitch with every pixel.
    const alpha = 1 - Math.exp(-dt / 0.12)
    this.lookX += (this.lookTargetX - this.lookX) * alpha
    this.lookY += (this.lookTargetY - this.lookY) * alpha

    // At rest the parameters have to be handed back, not merely held at zero: a 0.6 blend re-applied after breath
    // and physics every frame would damp the framework's own head sway and breathing for as long as the cursor is
    // away from the pet, which is most of the time. Weight 0 releases without pinning.
    const atRest =
      this.lookTargetX === 0 &&
      this.lookTargetY === 0 &&
      Math.abs(this.lookX) < 0.01 &&
      Math.abs(this.lookY) < 0.01

    // A held `sleep` track *is* the sleeping state: the head is down because that track keeps writing it, and a
    // cursor-driven head angle would fight it every frame. Only the eyes move while asleep, which is also what a
    // person does when they are pretending to sleep.
    const asleep = this.tracks.has('sleep')

    // The eye balls belong to nobody else, so they take the look outright; the head angles are additive, so a nod
    // or a turn still reads while the eyes follow.
    if (asleep) {
      set(frame, 'ParamAngleX', 0, 0)
      set(frame, 'ParamAngleY', 0, 0)
    } else {
      mix(frame, 'ParamAngleX', atRest ? 0 : this.lookX * 30, atRest ? 0 : 0.6)
      mix(frame, 'ParamAngleY', atRest ? 0 : -this.lookY * 30, atRest ? 0 : 0.6)
    }
    set(frame, 'ParamEyeBallX', atRest ? 0 : this.lookX, atRest ? 0 : 0.6)
    set(frame, 'ParamEyeBallY', atRest ? 0 : -this.lookY, atRest ? 0 : 0.6)
  }

  private tickMouth(dt: number, frame: Frame): void {
    if (!this.mouthOpen) return
    this.mouthElapsed += dt
    // 6–9 Hz: the flap rate of someone talking, not a metronome. A random amplitude per half-cycle keeps it from
    // looking like a sine wave.
    const period = 0.5 / 7.5
    if (this.mouthElapsed >= period) {
      this.mouthElapsed = 0
      this.mouthValue = Math.random() < 0.15 ? 0 : 0.25 + Math.random() * 0.75
    }
    set(frame, 'ParamMouthOpenY', this.mouthValue, 1)
  }

  private tickSpring(dt: number, frame: Frame): void {
    // The target is what the drag set; it decays back to rest, so releasing the gesture lets the body straighten
    // up without the input layer having to send a "drag ended" event.
    const decay = Math.exp(-dt / 0.25)
    this.springTargetX *= decay
    this.springTargetY *= decay
    this.springTargetZ *= decay

    // 0.25° is below what a 30 fps model can show, and the target is snapped on it because an exponential decay
    // would otherwise approach zero without ever arriving.
    const settled = 0.25
    if (Math.abs(this.springTargetX) < settled) this.springTargetX = 0
    if (Math.abs(this.springTargetY) < settled) this.springTargetY = 0
    if (Math.abs(this.springTargetZ) < settled) this.springTargetZ = 0

    const atRest =
      this.springX === 0 &&
      this.springY === 0 &&
      this.springZ === 0 &&
      this.springVelocityX === 0 &&
      this.springVelocityY === 0 &&
      this.springVelocityZ === 0 &&
      this.springTargetX === 0 &&
      this.springTargetY === 0 &&
      this.springTargetZ === 0
    if (atRest) return

    // k = 120, c = 20: enough overshoot to read as recoil, settled in well under a second. `c` is high because the
    // target decays too, and two slow decays would read as a wobble rather than a recoil.
    const step = (offset: number, velocity: number, target: number): [number, number] => {
      const next = velocity + (120 * (target - offset) - 20 * velocity) * dt
      return [offset + next * dt, next]
    }
    ;[this.springX, this.springVelocityX] = step(this.springX, this.springVelocityX, this.springTargetX)
    ;[this.springY, this.springVelocityY] = step(this.springY, this.springVelocityY, this.springTargetY)
    ;[this.springZ, this.springVelocityZ] = step(this.springZ, this.springVelocityZ, this.springTargetZ)

    // With the target at rest the spring is a pure decay toward zero: once it is close and slow, the motion is
    // over. Waiting for the velocity to separately drop leaves a long tail of sub-degree writes.
    const done = (offset: number, velocity: number, target: number) =>
      target === 0 && Math.abs(offset) < settled && Math.abs(velocity) < settled * 8

    if (done(this.springX, this.springVelocityX, this.springTargetX)) {
      this.springX = 0
      this.springVelocityX = 0
    }
    if (done(this.springY, this.springVelocityY, this.springTargetY)) {
      this.springY = 0
      this.springVelocityY = 0
    }
    if (done(this.springZ, this.springVelocityZ, this.springTargetZ)) {
      this.springZ = 0
      this.springVelocityZ = 0
    }

    // A zero written with weight 1 pins the body an imperceptible amount crooked forever; a zero with weight 0
    // hands the parameter back to the framework, which is what "the drag is over" means.
    const restWeight = this.springX === 0 && this.springY === 0 && this.springZ === 0 ? 0 : 1
    mix(frame, 'ParamBodyAngleX', this.springX, restWeight)
    mix(frame, 'ParamBodyAngleY', this.springY, restWeight)
    mix(frame, 'ParamBodyAngleZ', this.springZ, restWeight)
    // 40% of the body's recoil, so the head follows the body without doubling it.
    mix(frame, 'ParamAngleX', this.springX * 0.4, restWeight)
  }

  // ------------------------------------------------------------------ resolution

  private startTrack(action: ActionName): void {
    const name = TRACK_ALIASES[action] ?? action
    const definition = TRACKS[name]
    if (definition === undefined) return

    // `wake` starts from the held sleep pose, so it is the track that undoes it.
    if (name === 'wake') this.stopAction('sleep')

    // Starting a track replaces the running one, so two taps cannot stack into a doubled bend.
    const existing = this.tracks.get(name)
    if (existing !== undefined) {
      this.tracks.delete(name)
      this.releaseTrack(existing)
    }

    const live = definition.params.filter((parameter) => this.runtime.parameterRange(parameter.id) !== null)
    if (live.length === 0) return

    this.tracks.set(name, { action: name, definition, elapsed: 0, live })
  }

  /**
   * Hands a finished track's parameters back to the framework, except those `frame` already holds: another source
   * (a preset, the look, the spring) drives them this frame, and releasing them after `tick` sent its value would
   * drop that value for a frame, which reads as a pop.
   */
  private releaseTrack(running: RunningTrack, frame?: Frame): void {
    for (const parameter of running.live) {
      if (frame?.has(parameter.id) === true) continue
      this.runtime.setParameter(parameter.id, sample(parameter.keys, running.elapsed), 0)
    }
  }

  /** Writes a parameter, clamped to its own range, and skips parameters the model does not have. */
  private write(id: string, value: number, weight: number): void {
    const range = this.runtime.parameterRange(id)
    if (range === null) return
    this.runtime.setParameter(id, Math.max(range.min, Math.min(range.max, value)), weight)
  }

  private matchMotion(action: ActionName): { group: string; no: number; name: string } | undefined {
    const keywords = MOTION_KEYWORDS[action]
    if (keywords === undefined) return undefined

    return this.motions.find((motion) => {
      const group = motion.group.toLowerCase()
      const name = motion.name.toLowerCase()
      return keywords.some((keyword) => nameContains(group, keyword) || nameContains(name, keyword))
    })
  }

  /**
   * The model's own motion for a tap on `zone`: a group named for the zone (`TapHead`, `TapFace`, `TapBody`) wins
   * over a generic `Tap` group. One of the chosen group's motions is picked at random so a group with several taps
   * does not forever play `no: 0`.
   */
  private matchTapMotion(zone: 'head' | 'face' | 'body'): { group: string; no: number; name: string } | undefined {
    // `face` first tries the head group: most models that ship one tap group call it `TapHead` for the whole upper
    // body, and a face is inside it.
    const wanted =
      zone === 'face'
        ? ['tapface', 'taphead', 'tapbody']
        : zone === 'head'
          ? ['taphead', 'tapface', 'tapbody']
          : ['tapbody', 'taphead']

    let chosen: { group: string; no: number; name: string } | undefined
    for (const word of wanted) {
      chosen = this.motions.find((motion) => nameContains(motion.group.toLowerCase(), word))
      if (chosen !== undefined) break
    }
    if (chosen === undefined) {
      chosen = this.motions.find((motion) => nameContains(motion.group.toLowerCase(), 'tap'))
    }
    if (chosen === undefined) return undefined

    const group = chosen.group
    const inGroup = this.motions.filter((motion) => motion.group === group)
    const index = Math.min(inGroup.length - 1, Math.max(0, Math.floor(this.random() * inGroup.length)))
    return inGroup[index] ?? chosen
  }

  private applyExpression(name: ExpressionName): void {
    for (const id of this.presetParams.keys()) {
      this.runtime.setParameter(id, 0, 0)
    }
    this.presetParams.clear()

    const mapping = this.mapping.expressions[name]
    const kind = mapping?.kind ?? 'auto'

    if (kind === 'none' || name === 'default') {
      this.runtime.setExpression(this.defaultExpression)
      return
    }

    if (kind === 'expression') {
      const explicit = typeof mapping?.name === 'string' ? mapping.name : ''
      if (explicit !== '' && this.expressions.includes(explicit)) {
        this.runtime.setExpression(explicit)
        return
      }
    }

    if (kind !== 'procedural') {
      const auto = this.matchExpression(name)
      if (auto !== undefined) {
        this.runtime.setExpression(auto)
        return
      }
    }

    const preset = EXPRESSION_PRESETS[name]
    for (const [id, value] of Object.entries(preset.params)) {
      if (this.runtime.parameterRange(id) === null) continue
      this.presetParams.set(id, value)
    }
    // The framework has no "no expression" call, so a preset leaves the last `.exp3` playing and covers it with
    // parameter writes; clearing the expression is the model's own default look, which is what this asks for.
    this.runtime.setExpression(this.defaultExpression)
  }

  private matchExpression(name: Exclude<ExpressionName, 'default'>): string | undefined {
    const keywords = EXPRESSION_KEYWORDS[name]
    return this.expressions.find((expression) => {
      const lower = expression.toLowerCase()
      return keywords.some((keyword) => nameContains(lower, keyword.toLowerCase()))
    })
  }

  private releaseSpring(): void {
    this.write('ParamBodyAngleX', 0, 0)
    this.write('ParamBodyAngleY', 0, 0)
    this.write('ParamBodyAngleZ', 0, 0)
    this.write('ParamAngleX', 0, 0)
  }
}