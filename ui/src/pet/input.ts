/**
 * Pointer input for the pet window: click-through over transparent pixels, the three click zones, double clicks,
 * window dragging with a fling threshold, hover, and eye tracking.
 *
 * The window covers the sprite's rectangle, transparent corners included, so every 50 ms the shell reports the
 * global cursor and the page toggles click-through from that pixel's alpha. The same poll drives eye tracking while
 * the cursor is outside the window, where no pointer event fires.
 */

import type { ActionName } from './actions'

/** A click is a press and a release this close together, in physical pixels and milliseconds. */
const CLICK_MAX_MOVE_PX = 4
const CLICK_MAX_MS = 300
/** How long a second press promotes a click to a double click. */
const DOUBLE_CLICK_WINDOW_MS = 250
/** The cursor has to rest on opaque pixels this long before it counts as a hover. */
const HOVER_DELAY_MS = 600
const CURSOR_POLL_MS = 50
/** Velocity is measured over this much of the drag's most recent history. */
const VELOCITY_WINDOW_MS = 100
/** Alpha above this is opaque; anti-aliased edges and dust must not block the desktop behind them. */
export const OPAQUE_ALPHA = 0.1

interface PointerSample {
  screenX: number
  screenY: number
  at: number
}

export interface InputHost {
  /** 0..1 alpha of the rendered pixel under a window-local physical pixel; null when it cannot be read. */
  alphaAt(x: number, y: number): number | null
  cursor(): Promise<{ cursorX: number; cursorY: number; inside: boolean; width: number; height: number; scale: number }>
  setPassthrough(on: boolean): Promise<void>
  moveBy(dx: number, dy: number): Promise<void>
  /** The model's canvas in CSS pixels, for turning a window point into the sprite's own proportions. */
  modelSize(): { width: number; height: number }
  /** The model's own hit area answer (`Head`/`Face`/`Body`), or null when it has none. */
  hitZone(): ClickZone | null
  headBottom(): number
  faceBottom(): number
  action(
    action: ActionName,
    options?: { velocityX?: number; velocityY?: number; priority?: 1 | 2 | 3 },
  ): void
  look(x: number, y: number): void
  scene(scene: 'tapHead' | 'tapFace' | 'tapBody' | 'doubleTap' | 'drag' | 'fling' | 'hover'): void
  notify(): void
  flingThreshold(): number
  clickThroughEnabled(): boolean
}

export type ClickZone = 'head' | 'face' | 'body'

/**
 * Which part of the model a window point falls on. A model with HitAreas reports them from its own hit test; this
 * is the fallback, the model's bounding box split by the profile's two fractions measured from the top.
 */
function zoneFor(localY: number, height: number, headBottom: number, faceBottom: number): ClickZone {
  if (height <= 0) return 'body'
  const fraction = localY / height
  if (fraction < headBottom) return 'head'
  if (fraction < faceBottom) return 'face'
  return 'body'
}

interface DragState {
  lastX: number
  lastY: number
  history: PointerSample[]
  velocity: number
  /**
   * Device pixels per CSS pixel. `screenX`/`screenY` are CSS pixels while the window is moved in physical ones,
   * so at 150% a drag that moved the window by the raw delta would slide out from under the cursor.
   */
  scale: number
  /** Physical pixels accumulated but not yet handed to the window, since it only moves in whole pixels. */
  carryX: number
  carryY: number
}

interface PointerState {
  down: PointerSample
  drag: DragState | null
}

/**
 * Wires the window's pointer handling. Everything it needs from the rest of the app is a callback on `host`, so
 * the module stays testable and `PetApp` owns the wiring.
 */
export class PetInput {
  private readonly host: InputHost
  private readonly element: HTMLElement

  private pointer: PointerState | null = null
  /** Device pixels per CSS pixel, from the last cursor poll; 1 until the first report arrives. */
  private scale = 1
  private lastClickAt = 0
  private clickTimer: number | undefined
  private hoverSince = 0
  private hovering = false
  private passthroughOn = false
  private pollTimer: number | undefined
  private dragging = false
  private disposed = false

  private readonly onPointerDown = (event: PointerEvent): void => this.handleDown(event)
  private readonly onPointerMove = (event: PointerEvent): void => this.handleMove(event)
  private readonly onPointerUp = (event: PointerEvent): void => this.handleUp(event)
  private readonly onPointerCancel = (): void => this.cancelDrag()

  constructor(element: HTMLElement, host: InputHost) {
    this.element = element
    this.host = host

    element.addEventListener('pointerdown', this.onPointerDown)
    element.addEventListener('pointermove', this.onPointerMove)
    element.addEventListener('pointerup', this.onPointerUp)
    element.addEventListener('pointercancel', this.onPointerCancel)
    // Right-click and drag on a transparent window would otherwise open the webview's context menu.
    element.addEventListener('contextmenu', (event) => event.preventDefault())

    this.pollTimer = window.setInterval(() => void this.poll(), CURSOR_POLL_MS)
  }

  dispose(): void {
    this.disposed = true
    window.clearInterval(this.pollTimer)
    window.clearTimeout(this.clickTimer)
    this.element.removeEventListener('pointerdown', this.onPointerDown)
    this.element.removeEventListener('pointermove', this.onPointerMove)
    this.element.removeEventListener('pointerup', this.onPointerUp)
    this.element.removeEventListener('pointercancel', this.onPointerCancel)
  }

  // ------------------------------------------------------------------ cursor poll

  /**
   * The 50 ms poll: click-through follows the alpha under the cursor, and the eyes follow the cursor even when it
   * is on another window. Dragging forces click-through off, or the window would slip out from under the pointer.
   */
  private async poll(): Promise<void> {
    if (this.disposed) return

    let report: Awaited<ReturnType<InputHost['cursor']>>
    try {
      report = await this.host.cursor()
    } catch {
      // The pet window can be closed under us; nothing to do then.
      return
    }
    if (this.disposed) return

    const scale = report.scale || 1
    this.scale = scale
    const size = this.host.modelSize()
    const wantPassthrough = this.host.clickThroughEnabled() && !this.dragging

    if (report.inside && !this.dragging) {
      const alpha = this.host.alphaAt(report.cursorX, report.cursorY)
      // A null alpha means the buffer could not be read (a lost context); keeping the window clickable is the
      // safer failure, because an unclickable pet is one a user cannot even drag away.
      const opaque = alpha === null ? true : alpha > OPAQUE_ALPHA
      void this.setPassthrough(wantPassthrough && !opaque)
      this.trackHover(opaque)
    } else if (!this.dragging) {
      void this.setPassthrough(wantPassthrough)
      this.hovering = false
      this.hoverSince = 0
    }

    if (size.width > 0 && size.height > 0 && report.width > 0 && report.height > 0) {
      const target = lookTarget(report.cursorX, report.cursorY, report.width, report.height)
      this.host.look(target.x, target.y)
    }
  }

  private async setPassthrough(on: boolean): Promise<void> {
    if (on === this.passthroughOn) return
    this.passthroughOn = on
    try {
      await this.host.setPassthrough(on)
    } catch {
      // Racing the window's own teardown: the next poll tries again.
    }
  }

  /** Resting on opaque pixels for long enough plays the hover scene, once per rest. */
  private trackHover(opaque: boolean): void {
    if (!opaque) {
      this.hovering = false
      this.hoverSince = 0
      return
    }
    if (this.hovering || this.dragging) return
    if (this.hoverSince === 0) {
      this.hoverSince = performance.now()
      return
    }
    if (performance.now() - this.hoverSince < HOVER_DELAY_MS) return

    this.hovering = true
    this.host.scene('hover')
  }

  // ------------------------------------------------------------------ pointer

  private handleDown(event: PointerEvent): void {
    if (event.button !== 0) return
    const sample: PointerSample = { screenX: event.screenX, screenY: event.screenY, at: event.timeStamp }
    this.pointer = { down: sample, drag: null }
    this.host.notify()
    // A press has to answer immediately: the click's own scene waits up to 250 ms for a possible double click, and
    // without this a single tap on a still pet looks like nothing happened at all. A blink is small, lasts 0.14 s,
    // and cannot be mistaken for a scene — but it also must not eat a real motion, so it is the lowest priority.
    this.host.action('blink', { priority: 1 })
    this.element.setPointerCapture(event.pointerId)
  }

  private handleMove(event: PointerEvent): void {
    const pointer = this.pointer
    if (pointer === null) return

    const sample: PointerSample = { screenX: event.screenX, screenY: event.screenY, at: event.timeStamp }
    const dx = sample.screenX - pointer.down.screenX
    const dy = sample.screenY - pointer.down.screenY

    if (pointer.drag === null) {
      if (Math.hypot(dx, dy) < CLICK_MAX_MOVE_PX) return
      // Crossing the click threshold promotes the gesture to a drag; it never goes back to being a click.
      pointer.drag = {
        lastX: pointer.down.screenX,
        lastY: pointer.down.screenY,
        history: [pointer.down],
        velocity: 0,
        scale: this.scale,
        carryX: 0,
        carryY: 0,
      }
      this.dragging = true
      this.hovering = false
      this.hoverSince = 0
      void this.setPassthrough(false)
      this.host.scene('drag')
    }

    const drag = pointer.drag
    const stepX = sample.screenX - drag.lastX
    const stepY = sample.screenY - drag.lastY
    drag.lastX = sample.screenX
    drag.lastY = sample.screenY
    drag.history.push(sample)
    // Keep a little more than the velocity window so the first sample inside it is always available.
    const cutoff = sample.at - (VELOCITY_WINDOW_MS + CURSOR_POLL_MS)
    while (drag.history.length > 2 && (drag.history[0] as PointerSample).at < cutoff) {
      drag.history.shift()
    }

    // CSS to physical, keeping the sub-pixel remainder: rounding every step away would make the window lag the
    // cursor a little more with every event.
    drag.carryX += stepX * drag.scale
    drag.carryY += stepY * drag.scale
    const moveX = Math.trunc(drag.carryX)
    const moveY = Math.trunc(drag.carryY)
    if (moveX !== 0 || moveY !== 0) {
      drag.carryX -= moveX
      drag.carryY -= moveY
      void this.host.moveBy(moveX, moveY)
    }

    const velocity = velocityOf(drag.history, sample.at)
    drag.velocity = velocity.speed
    // The vector, not just the speed: a rightward drag has to lean the body left, a downward one forward.
    this.host.action('dragSpring', { velocityX: velocity.x, velocityY: velocity.y, priority: 3 })
  }

  private handleUp(event: PointerEvent): void {
    const pointer = this.pointer
    if (pointer === null) return
    this.pointer = null

    if (this.element.hasPointerCapture(event.pointerId)) {
      this.element.releasePointerCapture(event.pointerId)
    }

    const drag = pointer.drag
    if (drag !== null) {
      this.dragging = false
      this.host.notify()

      const release = velocityOf(drag.history, event.timeStamp)
      const speed = Math.max(drag.velocity, release.speed)
      if (speed > this.host.flingThreshold()) {
        this.host.scene('fling')
      } else {
        // The spring has to come back whether or not the release counted as a fling. The last-known direction is
        // what it springs back from.
        this.host.action('dragSpring', { velocityX: release.x, velocityY: release.y, priority: 3 })
      }
      void this.setPassthrough(this.host.clickThroughEnabled())
      return
    }

    if (event.timeStamp - pointer.down.at > CLICK_MAX_MS) return
    if (Math.hypot(event.screenX - pointer.down.screenX, event.screenY - pointer.down.screenY) >= CLICK_MAX_MOVE_PX) {
      return
    }

    this.registerClick(event)
  }

  /**
   * The click clock: a first click waits 250 ms, and a second inside that window becomes a double click. The zone
   * is resolved inside the timer because easy-live2d hit-tests from a `document`-level `pointerup` listener, which
   * runs after this element's; at registration time it would still hold the previous click's answer.
   */
  private registerClick(event: PointerEvent): void {
    this.host.notify()
    if (event.timeStamp - this.lastClickAt <= DOUBLE_CLICK_WINDOW_MS) {
      window.clearTimeout(this.clickTimer)
      this.clickTimer = undefined
      this.lastClickAt = 0
      this.host.scene('doubleTap')
      return
    }

    this.lastClickAt = event.timeStamp
    const localY = event.clientY - this.element.getBoundingClientRect().top
    window.clearTimeout(this.clickTimer)
    this.clickTimer = window.setTimeout(() => {
      this.clickTimer = undefined
      // The model's hit areas answer from its own geometry, which is the only zone that stays right when the model
      // is drawn smaller than the window; the profile's fractions are the fallback for a model without HitAreas.
      const zone = this.host.hitZone() ?? this.zoneAt(localY)
      this.host.scene(zone === 'head' ? 'tapHead' : zone === 'face' ? 'tapFace' : 'tapBody')
    }, DOUBLE_CLICK_WINDOW_MS)
  }

  /** The fallback zone: the profile's fractions of the window's height, measured from the top. */
  private zoneAt(localY: number): ClickZone {
    const rect = this.element.getBoundingClientRect()
    const size = this.host.modelSize()
    const height = size.height > 0 ? size.height : rect.height
    return zoneFor(localY, height, this.host.headBottom(), this.host.faceBottom())
  }

  private cancelDrag(): void {
    if (this.pointer?.drag === null || this.pointer === null) {
      this.pointer = null
      return
    }
    this.pointer = null
    this.dragging = false
    void this.setPassthrough(this.host.clickThroughEnabled())
  }
}

/**
 * Where the eyes aim for a cursor at window-local physical pixels: -1..1 on each axis around the window's middle.
 * No display scale here: the cursor and the window size are both physical pixels, so the ratio is unit-free.
 *
 * Outside the window the direction stays pinned to the edge and the strength falls off with the cursor's distance
 * from the window, reaching zero one window width away. A cursor on another monitor is not something the pet is
 * looking at, and a full-strength look there would hold the head at its limit most of the day.
 */
export function lookTarget(cursorX: number, cursorY: number, width: number, height: number): { x: number; y: number } {
  const halfWidth = width / 2
  const halfHeight = height / 2
  const x = (cursorX - halfWidth) / halfWidth
  const y = (cursorY - halfHeight) / halfHeight
  const outsideX = Math.max(0, Math.abs(x) - 1) * halfWidth
  const outsideY = Math.max(0, Math.abs(y) - 1) * halfHeight
  const strength = Math.max(0, 1 - Math.hypot(outsideX, outsideY) / width)
  return { x: clampUnit(x) * strength, y: clampUnit(y) * strength }
}

function clampUnit(value: number): number {
  return Math.max(-1, Math.min(1, value))
}

/**
 * The drag's velocity over the last {@link VELOCITY_WINDOW_MS}: the signed vector in CSS pixels per second, plus
 * its magnitude. The vector is what the body leans away from; a magnitude alone cannot say which direction.
 */
function velocityOf(history: readonly PointerSample[], nowMs: number): { x: number; y: number; speed: number } {
  const recent = history.filter((sample) => nowMs - sample.at <= VELOCITY_WINDOW_MS)
  if (recent.length < 2) return { x: 0, y: 0, speed: 0 }

  const first = recent[0] as PointerSample
  const last = recent[recent.length - 1] as PointerSample
  const elapsed = (last.at - first.at) / 1000
  // A window shorter than the timer's resolution would divide by nearly zero and report an absurd fling.
  if (elapsed < 0.016) return { x: 0, y: 0, speed: 0 }

  const x = (last.screenX - first.screenX) / elapsed
  const y = (last.screenY - first.screenY) / elapsed
  return { x, y, speed: Math.hypot(x, y) }
}