/**
 * The pet window's application: load the Cubism Core, load the chosen model, and drive the scene engine from the
 * renderer's own ticker.
 *
 * The order matters: the Core is a global script that must exist before the sprite touches a model, the sprite must
 * be ready before its canvas size (and so the window size) is known, and the Pixi ticker must drive everything
 * because the model advances inside its own `onRender`. A failure never shows a dialog; the reason goes back to the
 * API, where the settings page shows it.
 */

import { Application, type Ticker } from 'pixi.js'
import type { ConfigType, Live2DSprite } from 'easy-live2d'

import {
  ActionLayer,
  isActionName,
  isExpressionName,
  type ActionMapping,
  type ActionName,
  type ActionRuntime,
} from './actions'
import {
  DEFAULT_PET_SETTINGS,
  petApiFromWindow,
  type PetApi,
  type PetModelInfo,
  type PetSettings,
} from './api'
import { Director, type DirectorSettings } from './director'
import { matchEmotion, type Dictionary } from './emotion'
import { startFeed, type FeedHandle } from './feed'
import { OPAQUE_ALPHA, PetInput, type ClickZone, type InputHost } from './input'
import { convertFileSrc, petShell, type PetShell } from './shellBridge'
import { BUBBLE_HEIGHT, BUBBLE_WIDTH, bubbleFrame, bubbleText, type BubbleFrame, type BubbleRect } from './speechBubble'

/** How long a missing Core counts as "still loading". */
const CORE_TIMEOUT_MS = 10_000
/** How long a model's own hit-area answer is trusted for the click that follows it. */
const HIT_AREA_MEMORY_MS = 600
/** The pet's height at 100%, in logical pixels. The model's own canvas is native resolution, not a window size. */
const PET_BASE_HEIGHT = 360
/** Wide enough for a landscape model at 200%, narrow enough to stay on a laptop screen. */
const PET_MAX_WIDTH = 720
/** Fraction of the resting figure's box left as clear margin on each side, so motions are not clipped. */
const CROP_MARGIN = 0.12

/** A promise that resolves after `ms`; used between boot attempts while the sidecar is still starting. */
function delay(ms: number): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>()
  setTimeout(resolve, ms)
  return promise
}

/** The global the Cubism Core script defines; its presence is the only readiness signal the SDK offers. */
function cubismCoreDefined(): boolean {
  return typeof Reflect.get(window, 'Live2DCubismCore') === 'object'
}

/** Loads the verified Core over the asset protocol and resolves once its global exists. */
async function loadCore(path: string): Promise<void> {
  if (cubismCoreDefined()) return

  const { promise, resolve, reject } = Promise.withResolvers<void>()
  const script = document.createElement('script')
  script.src = convertFileSrc(path)
  script.async = true

  const timer = window.setTimeout(() => reject(new Error('the Cubism Core did not initialise')), CORE_TIMEOUT_MS)

  script.addEventListener('load', () => {
    window.clearTimeout(timer)
    if (cubismCoreDefined()) resolve()
    else reject(new Error('the Cubism Core script ran but defined no Live2DCubismCore'))
  })
  script.addEventListener('error', () => {
    window.clearTimeout(timer)
    reject(new Error('the Cubism Core script could not be loaded'))
  })

  document.head.append(script)
  return promise
}

/**
 * Where the Core lives, given an imported model. The status endpoint reports the hash, not a path, and the storage
 * layout puts both under the same pet root (`<root>/models/<id>/…` and `<root>/core/live2dcubismcore.min.js`), so
 * splitting at `models` is exact.
 */
function corePathFor(model3: string): string | null {
  const normalized = model3.replace(/\\/g, '/')
  const marker = normalized.lastIndexOf('/models/')
  if (marker < 0) return null
  return `${normalized.slice(0, marker)}/core/live2dcubismcore.min.js`
}

function profileMapping(settings: PetSettings, modelId: string | null): ActionMapping {
  const profile = modelId === null ? undefined : settings.profiles[modelId]
  return {
    motions: profile?.motions ?? {},
    expressions: profile?.expressions ?? {},
    headBottom: profile?.headBottom ?? 0.35,
    faceBottom: profile?.faceBottom ?? 0.55,
    parameters: profile?.parameters ?? {},
    amplitude: profile?.amplitude ?? 1,
    procedural: profile?.procedural ?? {},
  }
}

function dictionaryOf(settings: PetSettings): Dictionary {
  const emotions = settings.emotions
  return {
    keywords: emotions?.keywords ?? {},
    negationPrefixes: emotions?.negationPrefixes ?? DEFAULT_PET_SETTINGS.emotions.negationPrefixes,
    minWeight: emotions?.minWeight ?? 1,
  }
}

/** The action runtime `ActionLayer` drives: the sprite, plus whether a motion group actually exists. */
class SpriteRuntime implements ActionRuntime {
  private readonly sprite: Live2DSprite
  private readonly motionGroups: Set<string>

  constructor(sprite: Live2DSprite, groups: readonly string[]) {
    this.sprite = sprite
    this.motionGroups = new Set(groups)
  }

  async playMotion(group: string, no: number, priority: 1 | 2 | 3): Promise<boolean> {
    // An unknown group is a silent no-op inside easy-live2d, so the answer has to come from here: the action layer
    // falls through to its procedural track only when this reports the motion was not playable.
    if (!this.motionGroups.has(group)) return false
    try {
      // A refusal is not an exception: a motion at the same or a higher priority still playing, or a sprite whose
      // model is still loading, answers -1 (`InvalidMotionQueueEntryHandleValue`), and the motion never plays. A
      // started motion's handle is its queue entry object, not a number.
      const handle: unknown = await this.sprite.startMotion({ group, no, priority })
      return handle !== -1 && handle !== null && handle !== undefined
    } catch {
      return false
    }
  }

  parameterRange(id: string): { min: number; max: number } | null {
    return this.sprite.getParameterValueRangeById(id)
  }

  /** An empty name means "the model's own look": the framework has no explicit reset, so its motions are dropped. */
  setExpression(name: string): void {
    if (name === '') {
      this.sprite.releaseExpressions()
      return
    }
    this.sprite.setExpression({ expressionId: name })
  }
}

/**
 * The whole pet window: one instance per webview, built and reported on by {@link bootPetApp}.
 */
class PetApp {
  private readonly api: PetApi
  private readonly element: HTMLElement
  private readonly bubble: HTMLDivElement
  private readonly bubbleContent: HTMLDivElement
  private shell: PetShell | null

  /** Set by {@link mount}, which is the only place the Core is known to exist. */
  private config: ConfigType | null = null

  private settings: PetSettings = DEFAULT_PET_SETTINGS
  private models: PetModelInfo[] = []
  private model: PetModelInfo | null = null

  private app: Application | null = null
  private readonly pixel = new Uint8Array(4)
  private sprite: Live2DSprite | null = null
  private actions: ActionLayer | null = null
  private director: Director | null = null
  private input: PetInput | null = null
  private feed: FeedHandle | null = null

  private sleeping = false
  private hidden = false
  /** The in-flight reload, so a second one waits for it instead of interleaving mount and unmount. */
  private reloading: Promise<void> | null = null
  private reloadPending = false
  private lastHit: { name: string; at: number } | null = null
  /** The resting figure's crop box as fractions of the full canvas, measured once at mount; see {@link resize}. */
  private crop: { minX: number; minY: number; width: number; height: number } | null = null
  /** Resting visible figure bounds as fractions of the uncropped model canvas. */
  private figureBounds: { top: number; height: number } | null = null
  private modelFrame: { width: number; height: number; spriteX: number } | null = null
  private windowFrame: BubbleFrame | null = null
  private layoutRequested = false
  private layoutTask: Promise<void> | null = null
  private speaking = false
  private latestReply: string | null = null
  private tickHandler: ((ticker: Ticker) => void) | null = null

  constructor(api: PetApi, element: HTMLElement) {
    this.api = api
    this.element = element
    this.shell = petShell()
    this.bubble = document.createElement('div')
    this.bubble.className = 'speech-bubble'
    this.bubble.hidden = true
    this.bubble.setAttribute('role', 'status')
    this.bubble.setAttribute('aria-live', 'polite')
    this.bubble.style.width = `${BUBBLE_WIDTH}px`
    this.bubble.style.height = `${BUBBLE_HEIGHT}px`
    this.bubbleContent = document.createElement('div')
    this.bubbleContent.className = 'speech-bubble-text'
    this.bubble.append(this.bubbleContent)
  }

  async start(): Promise<void> {
    // The shell creates this window at startup, in parallel with the sidecar: the API may not be listening yet, so a
    // failed boot is retried rather than treated as final. The reason is reported only when it changes — a missing
    // Core is a standing condition, not an event — and cleared once a boot succeeds.
    let reported: string | null = null
    for (;;) {
      try {
        await this.bootstrap()
        // Always, not only after a failure seen in this page load: the last error may have been reported by a
        // previous load, and it is the settings page's only view of what the window is doing.
        await this.report(null)
        return
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error)
        if (message !== reported) {
          reported = message
          await this.report(message)
        }
        await delay(2000)
      }
    }
  }

  private async bootstrap(): Promise<void> {
    this.settings = await this.readSettings()
    const core = await this.api.petCore()
    this.models = await this.api.petModels()

    if (!core.installed) {
      throw new Error('the Live2D Cubism Core is not installed')
    }

    const model = this.selectedModel()
    if (model === null) {
      throw new Error('no Live2D model is selected in the pet settings')
    }

    const corePath = corePathFor(model.model3)
    if (corePath === null) {
      throw new Error('could not locate the Cubism Core beside the imported models')
    }

    await loadCore(corePath)
    await this.mount(model)
    this.applyShellSettings()
    this.startFeed()

    this.shell?.onVisibility((visible) => this.setVisible(visible))
    document.addEventListener('visibilitychange', () => this.setVisible(!document.hidden))
    this.director?.scene('load')
  }

  /**
   * Re-reads the settings; rebuilds the sprite only when the model changed. Reloads are serialized through
   * {@link reloading} so `mount` and `unmount` never interleave; one that arrives mid-flight runs once afterwards,
   * which also collapses a burst of saves into one read.
   */
  async reload(): Promise<void> {
    if (this.reloading !== null) {
      this.reloadPending = true
      return this.reloading
    }

    const run = this.reloadOnce().finally(() => {
      this.reloading = null
      if (!this.reloadPending) return
      this.reloadPending = false
      void this.reload().catch((error: unknown) => this.report(error))
    })
    this.reloading = run
    return run
  }

  private async reloadOnce(): Promise<void> {
    this.settings = await this.readSettings()

    const model = this.selectedModel()
    if (model === null) {
      // The model was deleted, and the server already cleared the selection. Leaving the old sprite on the desktop
      // would keep showing a model the settings no longer name, so it is torn down before reporting the problem.
      await this.unmount()
      this.model = null
      throw new Error('no Live2D model is selected in the pet settings')
    }

    // A scale change only needs the renderer and window resized; a model change needs a full mount. `this.model` is
    // replaced only after `mount` succeeds, so a failed load is retried as a model change on the next save.
    const rebuilt = model.id !== this.model?.id

    if (rebuilt) {
      await this.mount(model)
    } else {
      this.actions?.configure(
        profileMapping(this.settings, model.id),
        model.motions,
        model.expressions,
        { holdSeconds: this.settings.expressionHoldSeconds, defaultExpression: this.defaultExpression() },
      )
      // Before the first mount there is no Config to update.
      if (this.config !== null) this.config.MotionSound = this.settings.motionSound
      this.resize()
    }

    this.configureDirector()
    this.applyShellSettings()
    await this.api.reportDiagnostics(model.id, this.actions?.diagnostics() ?? []).catch(() => undefined)
  }

  /**
   * Window properties the shell cannot read from the settings document itself: the window is created on top
   * unconditionally, so a saved "not always on top" has to be pushed once the page knows about it.
   */
  private applyShellSettings(): void {
    if (this.shell === null) return
    void this.shell.setAlwaysOnTop(this.settings.alwaysOnTop).catch(() => undefined)
  }

  // ------------------------------------------------------------------ setup

  private selectedModel(): PetModelInfo | null {
    const id = this.settings.activeModelId
    return id === null ? null : (this.models.find((entry) => entry.id === id) ?? null)
  }

  private async readSettings(): Promise<PetSettings> {
    try {
      const view = await this.api.petSettings()
      return view.pet ?? DEFAULT_PET_SETTINGS
    } catch {
      // A first read racing the API starting is not fatal: the feed's settings revision re-reads it later.
      return DEFAULT_PET_SETTINGS
    }
  }

  private defaultExpression(): string {
    return this.model === null ? '' : (this.settings.profiles[this.model.id]?.defaultExpression ?? '')
  }

  private directorSettings(): DirectorSettings {
    return {
      idleMinSeconds: this.settings.idleMinSeconds,
      idleMaxSeconds: this.settings.idleMaxSeconds,
      sleepAfterMinutes: this.settings.sleepAfterMinutes,
      greetingTimes: this.settings.greetingTimes,
      emotionCooldownSeconds: this.settings.emotionCooldownSeconds,
    }
  }

  private configureDirector(): void {
    this.director?.configure(this.directorSettings())
  }

  private async mount(model: PetModelInfo): Promise<void> {
    await this.unmount()

    // The module body reads `Live2DCubismCore` while it initialises, so it cannot be imported until the Core script
    // has loaded; a static import throws a ReferenceError and leaves the window blank.
    const { Config, Live2DSprite: Sprite } = await import('easy-live2d')
    this.config = Config

    Config.MouseFollow = false
    // The director owns idle behaviour; letting the framework pick Idle motions over it would double up. The group
    // name exists in no model, which is exactly why it is safe as a sentinel.
    Config.MotionGroupIdle = '__none__'
    Config.MotionSound = this.settings.motionSound
    // The model comes from the asset protocol, a different origin than the page: textures need a CORS request.
    Config.crossOrigin = 'anonymous'

    const canvas = document.createElement('canvas')
    this.element.replaceChildren(canvas, this.bubble)

    const app = new Application()
    await app.init({
      canvas,
      backgroundAlpha: 0,
      // The input probe reads one pixel per poll; without this the buffer is cleared after presenting.
      preserveDrawingBuffer: true,
      autoDensity: true,
      resolution: window.devicePixelRatio || 1,
    })
    app.ticker.maxFPS = 30

    const sprite = new Sprite({ modelPath: convertFileSrc(model.model3) })
    sprite.onLive2D('hit', ({ hitAreaName }) => {
      this.lastHit = { name: hitAreaName, at: performance.now() }
    })
    sprite.onLive2D('error', (error) => {
      void this.report(error instanceof Error ? error.message : String(error))
    })
    app.stage.addChild(sprite)

    this.app = app
    this.sprite = sprite

    await sprite.ready

    const canvasSize = sprite.getModelCanvasSize()
    if (canvasSize === null) {
      throw new Error('the model reported no canvas size')
    }
    this.model = model

    this.resize(canvasSize.width, canvasSize.height)

    const groups = model.motions.map((motion) => motion.group)
    const actions = new ActionLayer(
      new SpriteRuntime(sprite, groups),
      profileMapping(this.settings, model.id),
      model.motions,
      model.expressions,
      { holdSeconds: this.settings.expressionHoldSeconds, defaultExpression: this.defaultExpression() },
    )
    this.actions = actions

    this.director = new Director(
      {
        now: () => performance.now(),
        random: () => Math.random(),
        action: actions,
        sleeping: () => this.sleeping,
        setSleeping: (value: boolean) => {
          this.sleeping = value
        },
      },
      this.directorSettings(),
    )
    this.director.speaking(this.speaking)
    if (this.speaking) this.setBubble(this.latestReply)
    // The layer writes inside the model's own update, between its effects and its physics, so the procedural
    // offsets drive physics outputs (hair, smoothed `*2` chains) too. See `ui/patches/easy-live2d+1.1.0.patch`.
    sprite.onBeforePhysics((parameters) => this.actions?.apply(parameters))

    // One ticker callback drives the tracks and then the director, in that order: a scene the director starts this
    // frame may add a track, but nothing the tracks composed this frame is discarded. The ticker runs before the
    // render, which is where the model updates and the composed frame is applied.
    this.tickHandler = (ticker: Ticker) => {
      this.actions?.tick(ticker.deltaMS / 1000)
      this.director?.update(performance.now())
    }
    app.ticker.add(this.tickHandler)

    this.input = new PetInput(canvas, this.inputHost())
  }

  private async unmount(): Promise<void> {
    await this.api.reportDiagnostics(null, []).catch(() => undefined)
    this.input?.dispose()
    this.input = null

    if (this.app !== null && this.tickHandler !== null) {
      this.app.ticker.remove(this.tickHandler)
    }
    this.tickHandler = null
    this.director = null
    this.actions = null
    this.crop = null
    this.figureBounds = null
    this.modelFrame = null
    this.bubble.hidden = true
    this.bubbleContent.textContent = ''

    if (this.sprite !== null) {
      this.sprite.destroy()
      this.sprite = null
    }
    if (this.app !== null) {
      this.app.destroy(true)
      this.app = null
    }
    this.element.replaceChildren()
  }

  private setBubble(body: string | null): void {
    const text = body === null ? '' : bubbleText(body)
    if (text === this.bubbleContent.textContent) return
    if (text === '') this.bubble.hidden = true
    this.bubbleContent.textContent = text
    this.requestWindowLayout()
  }

  private requestWindowLayout(): void {
    this.layoutRequested = true
    this.layoutTask ??= this.layoutWindow().catch((error: unknown) => this.report(error)).finally(() => {
      this.layoutTask = null
      if (this.layoutRequested) this.requestWindowLayout()
    })
  }

  private async layoutWindow(): Promise<void> {
    while (this.layoutRequested) {
      this.layoutRequested = false
      const { app, sprite, modelFrame, windowFrame, shell } = this
      if (app === null || sprite === null || modelFrame === null) continue

      const offsetX = windowFrame?.modelOffsetX ?? 0
      const show = Boolean(this.bubbleContent.textContent)
      let workArea: BubbleRect | null = null
      if (show && shell !== null) {
        const bounds = await shell.bubbleBounds(offsetX + modelFrame.width / 2, modelFrame.height / 2)
        if (bounds.workArea !== null) {
          workArea = {
            x: (bounds.workArea.x - bounds.x) / bounds.scale,
            y: (bounds.workArea.y - bounds.y) / bounds.scale,
            width: bounds.workArea.width / bounds.scale,
            height: bounds.workArea.height / bounds.scale,
          }
        }
      }
      if (this.modelFrame !== modelFrame || show !== Boolean(this.bubbleContent.textContent)) continue

      const { width, height, modelOffsetX, bubble } = bubbleFrame({ ...modelFrame, x: offsetX, y: 0 }, workArea, show)
      if (bubble === null) this.bubble.hidden = true
      if (width !== windowFrame?.width || height !== windowFrame.height || modelOffsetX !== offsetX) {
        await shell?.setSize(width, height, offsetX - modelOffsetX)
      }
      this.windowFrame = { width, height, modelOffsetX, bubble }
      if (this.modelFrame !== modelFrame) continue

      sprite.x = modelFrame.spriteX + modelOffsetX
      if (app.screen.width !== width || app.screen.height !== height) app.renderer.resize(width, height)
      if (bubble !== null && this.bubbleContent.textContent) {
        this.bubble.style.left = `${bubble.x}px`
        this.bubble.style.top = `${bubble.y}px`
        this.bubble.hidden = false
      }
    }
  }

  /**
   * Sizes the window to the model's canvas times the user's scale, cropped to the figure. Authored canvases carry
   * wide empty margins, and the crop keeps them out of the window so `PET_BASE_HEIGHT` and the profile's head/face
   * fractions describe the character. Cropping moves the sprite; it never changes how large the character is drawn.
   */
  private resize(modelWidth?: number, modelHeight?: number): void {
    const app = this.app
    const sprite = this.sprite
    if (app === null || sprite === null) return

    const canvasSize =
      modelWidth !== undefined && modelHeight !== undefined
        ? { width: modelWidth, height: modelHeight }
        : sprite.getModelCanvasSize()
    if (canvasSize === null) return

    // The canvas is authored at native resolution, often taller than the screen, so only its aspect ratio is used;
    // the width clamp keeps a large scale on a laptop display without distorting it.
    const aspect = canvasSize.height > 0 ? canvasSize.width / canvasSize.height : 1
    const scale = this.settings.scale > 0 ? this.settings.scale : 1
    let height = PET_BASE_HEIGHT * scale
    let width = height * aspect
    if (width > PET_MAX_WIDTH) {
      width = PET_MAX_WIDTH
      height = width / aspect
    }

    const fullWidth = Math.max(1, Math.round(width))
    const fullHeight = Math.max(1, Math.round(height))

    // Measured once, in the resting pose at mount, and cached as fractions of the full canvas (the same at every
    // scale). Actions, the sleep pose and hair physics leave the resting box, so a later measurement would crop to
    // whatever pose was playing. The measurement reads the drawing buffer, so the sprite is first laid out uncropped.
    if (this.crop === null) {
      sprite.x = 0
      sprite.y = 0
      sprite.width = fullWidth
      sprite.height = fullHeight
      app.renderer.resize(fullWidth, fullHeight)

      const measured = this.opaqueBounds()
      if (measured === null) {
        // An unmeasurable model keeps the whole canvas rather than being clipped to a guess.
        this.modelFrame = { width: fullWidth, height: fullHeight, spriteX: 0 }
        this.requestWindowLayout()
        return
      }
      this.figureBounds = { top: measured.minY / fullHeight, height: measured.height / fullHeight }
      // Pad all four sides so motions are not clipped, clamped to the canvas so a figure that already fills it is
      // not shrunk by an imaginary margin.
      const marginX = measured.width * CROP_MARGIN
      const marginY = measured.height * CROP_MARGIN
      const minX = Math.max(0, measured.minX - marginX)
      const minY = Math.max(0, measured.minY - marginY)
      const maxX = Math.min(fullWidth, measured.minX + measured.width + marginX)
      const maxY = Math.min(fullHeight, measured.minY + measured.height + marginY)
      this.crop = {
        minX: minX / fullWidth,
        minY: minY / fullHeight,
        width: (maxX - minX) / fullWidth,
        height: (maxY - minY) / fullHeight,
      }
    }

    const crop = {
      minX: this.crop.minX * fullWidth,
      minY: this.crop.minY * fullHeight,
      width: this.crop.width * fullWidth,
      height: this.crop.height * fullHeight,
    }

    // `PET_BASE_HEIGHT` is the figure's height, not the canvas's, so the window takes the box's size and the sprite
    // is the window divided by the box's share of the canvas, slid so the box's corner lands on the window origin.
    const boxFractionY = crop.height / fullHeight
    const boxFractionX = crop.width / fullWidth
    let windowHeight = Math.max(1, Math.round(PET_BASE_HEIGHT * scale))
    let windowWidth = Math.max(1, Math.round(windowHeight * (crop.width / crop.height)))
    if (windowWidth > PET_MAX_WIDTH) {
      windowWidth = PET_MAX_WIDTH
      windowHeight = Math.max(1, Math.round(windowWidth * (crop.height / crop.width)))
    }

    const spriteWidth = Math.max(1, Math.round(windowWidth / (boxFractionX > 0 ? boxFractionX : 1)))
    const spriteHeight = Math.max(1, Math.round(windowHeight / (boxFractionY > 0 ? boxFractionY : 1)))
    const scaleX = spriteWidth / fullWidth
    const scaleY = spriteHeight / fullHeight

    sprite.y = -crop.minY * scaleY
    sprite.width = spriteWidth
    sprite.height = spriteHeight
    this.modelFrame = { width: windowWidth, height: windowHeight, spriteX: -crop.minX * scaleX }
    this.requestWindowLayout()
  }

  /**
   * The box the figure draws into, in logical window pixels, or null. Read from the alpha channel after a render:
   * a Live2D model exposes no geometry that matches what it draws. The stage must already be laid out at the size
   * being measured.
   */
  private opaqueBounds(): { minX: number; minY: number; width: number; height: number } | null {
    const app = this.app
    if (app === null) return null
    const renderer = app.renderer
    if (!('gl' in renderer)) return null
    const gl = renderer.gl
    const canvas = renderer.canvas
    const resolution = renderer.resolution > 0 ? renderer.resolution : 1
    const width = Math.round(Number(canvas.width))
    const height = Math.round(Number(canvas.height))
    if (width <= 0 || height <= 0) return null

    app.render()

    const pixels = new Uint8Array(width * height * 4)
    gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, pixels)
    // The threshold matches the click-through test's: an anti-aliased edge must not define the box.
    const opaque = Math.round(OPAQUE_ALPHA * 255)
    let minX = width
    let minY = height
    let maxX = -1
    let maxY = -1
    for (let y = 0; y < height; y += 1) {
      const row = y * width * 4
      for (let x = 0; x < width; x += 1) {
        if ((pixels[row + x * 4 + 3] as number) <= opaque) continue
        if (x < minX) minX = x
        if (y < minY) minY = y
        if (x > maxX) maxX = x
        if (y > maxY) maxY = y
      }
    }
    if (maxX < 0) return null

    // The buffer is in device pixels (`autoDensity` plus `resolution`) and `resize()` lays out in logical ones, so
    // the box is divided back down. readPixels counts rows from the bottom.
    return {
      minX: minX / resolution,
      minY: (height - maxY - 1) / resolution,
      width: (maxX - minX + 1) / resolution,
      height: (maxY - minY + 1) / resolution,
    }
  }

  // ------------------------------------------------------------------ probes

  /**
   * The alpha of the rendered pixel under a window-local physical pixel, which is the click-through test: the model
   * is the only thing on the transparent stage. `x` and `y` are in `pet_cursor`'s physical pixels, which is already
   * the drawing buffer's unit, so they are not multiplied by `resolution`. Null means the pixel cannot be read.
   */
  alphaAt(x: number, y: number): number | null {
    const app = this.app
    if (app === null) return null

    // Pixi's renderer type is a union of WebGL, WebGPU and canvas renderers; the pet window only ever initialises
    // WebGL, and `gl` is the property only that member declares, so this narrows rather than casts.
    const renderer = app.renderer
    if (!('gl' in renderer)) return null
    const gl = renderer.gl
    const canvas = renderer.canvas

    const width = Math.round(Number(canvas.width))
    const height = Math.round(Number(canvas.height))
    const pixelX = Math.round(x)
    // readPixels counts from the bottom-left; the shell reports cursor coordinates from the top-left.
    const pixelY = height - Math.round(y) - 1
    if (pixelX < 0 || pixelY < 0 || pixelX >= width || pixelY >= height) return null

    gl.readPixels(pixelX, pixelY, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, this.pixel)
    return (this.pixel[3] as number) / 255
  }

  /** A model's own hit area, when it has one, decides the click zone; the profile's fractions are the fallback. */
  private zoneFromHitArea(): ClickZone | null {
    const hit = this.lastHit
    if (hit === null || performance.now() - hit.at > HIT_AREA_MEMORY_MS) return null

    const name = hit.name.toLowerCase()
    if (name.includes('head')) return 'head'
    if (name.includes('face')) return 'face'
    if (name.includes('body')) return 'body'
    return null
  }

  private inputHost(): InputHost {
    const noShell = { cursorX: 0, cursorY: 0, inside: false, width: 0, height: 0, scale: 1 }
    return {
      alphaAt: (x, y) => this.alphaAt(x, y),
      cursor: async () => (this.shell === null ? noShell : this.shell.cursor()),
      setPassthrough: async (on) => {
        await this.shell?.setPassthrough(on)
      },
      moveBy: async (dx, dy) => {
        await this.shell?.moveBy(dx, dy)
        if (this.bubbleContent.textContent) this.requestWindowLayout()
      },
      visibleBounds: () => {
        const sprite = this.sprite
        const app = this.app
        const bounds = this.figureBounds
        if (sprite === null || app === null || bounds === null || app.screen.height <= 0) return null
        const cssScale = app.canvas.getBoundingClientRect().height / app.screen.height
        return { top: (sprite.y + bounds.top * sprite.height) * cssScale, height: bounds.height * sprite.height * cssScale }
      },
      isHidden: () => this.hidden,
      hitZone: () => this.zoneFromHitArea(),
      headBottom: () => profileMapping(this.settings, this.model?.id ?? null).headBottom,
      faceBottom: () => profileMapping(this.settings, this.model?.id ?? null).faceBottom,
      action: (action: ActionName, options) => this.actions?.play(action, options ?? {}),
      look: (x, y) => this.actions?.look(x, y),
      scene: (scene) => {
        this.director?.scene(scene)
      },
      notify: () => this.director?.notify(),
      flingThreshold: () => this.settings.fastDragPxPerSecond,
      clickThroughEnabled: () => this.settings.clickThroughTransparent,
    }
  }

  // ------------------------------------------------------------------ feed and visibility

  private startFeed(): void {
    this.feed?.stop()
    this.feed = startFeed({
      fetch: (after, previewAfter) => this.api.petFeed(after, previewAfter),
      onReply: (body) => {
        this.latestReply = body
        if (this.speaking && this.modelFrame !== null) this.setBubble(body)
        const director = this.director
        if (director === null) return
        // A sleeping pet wakes first; the emotion then queues behind the wake scene instead of being lost.
        director.reply()
        const match = matchEmotion(body, dictionaryOf(this.settings))
        director.emotion(match)
      },
      onSpeaking: (speaking) => {
        this.speaking = speaking
        this.director?.speaking(speaking)
        if (speaking) {
          if (this.modelFrame !== null) this.setBubble(this.latestReply)
        } else {
          this.latestReply = null
          this.setBubble(null)
        }
      },
      onSettings: () => {
        void this.reload().catch((error: unknown) => this.report(error))
      },
      onPreview: ({ action, expression }) => {
        // The names are plain strings from the settings page; an unknown expression would throw inside the action
        // layer and break the feed's whole `consume` call. A sleeping pet keeps its eyes shut, but a motion may play.
        if (expression !== '' && !this.sleeping && isExpressionName(expression)) {
          this.actions?.expression(expression)
        }
        if (action !== '' && isActionName(action)) {
          // A preview is a person asking to see the action right now, so it preempts whatever is playing.
          this.actions?.play(action, { priority: 3 })
        }
      },
      onError: () => {
        // A failed poll is transient — the API may be restarting — so the pet keeps asking rather than reporting.
      },
      isHidden: () => this.hidden,
    })
  }

  private setVisible(visible: boolean): void {
    this.hidden = !visible
    this.input?.visibilityChanged()
    const app = this.app
    if (app === null) return

    if (this.hidden) {
      app.ticker.stop()
      return
    }
    app.ticker.start()
    // The feed skips hidden windows, so one immediate poll is what makes the wake-up feel instant.
    this.feed?.pollNow()
  }

  private async report(reason: unknown | null): Promise<void> {
    const message = reason === null ? null : reason instanceof Error ? reason.message : String(reason)
    try {
      await this.api.reportPetStatus(message)
    } catch {
      // Nothing left to report it through, and no console anyone can read from here.
    }
  }
}

/** Boots the pet window. Any failure is reported to the API and leaves the window blank. */
export async function bootPetApp(): Promise<void> {
  const element = document.getElementById('pet')
  if (element === null) return

  // Only the shell's initialization script provides the token and port; without them there is nothing to report to.
  const api = petApiFromWindow()
  if (api === null) return

  const app = new PetApp(api, element)
  try {
    await app.start()
  } catch (error) {
    await api.reportPetStatus(error instanceof Error ? error.message : String(error)).catch(() => undefined)
  }
}
