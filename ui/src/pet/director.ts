/**
 * The director: one main scene slot, a hover overlay, a speaking overlay, and the rules for who may interrupt whom.
 * Time and randomness are injected so the state machine is testable without a clock or a model.
 *
 * A scene only preempts one of equal or lower priority; anything else is dropped, not queued, so a tap never replays
 * itself seconds later. Sleeping is only left by a tap, a reply or a greeting: dragging a sleeping pet around is the
 * point of a desktop pet.
 */

import type { ActionLayer, ExpressionName } from './actions'
import type { EmotionMatch } from './emotion'
import { SCENE_PRIORITY, SPEAKING_NOD_INTERVAL_MS, planScene, type SceneName, type ScenePlan } from './scenes'

interface DirectorHost {
  now(): number
  random(): number
  action: ActionLayer
  sleeping(): boolean
  setSleeping(value: boolean): void
}

export interface DirectorSettings {
  idleMinSeconds: number
  idleMaxSeconds: number
  sleepAfterMinutes: number
  greetingTimes: string[]
  emotionCooldownSeconds: number
}

interface RunningScene {
  plan: ScenePlan
  startedAt: number
  nextStep: number
}

const WAKE_SCENE: SceneName = 'wake'

/**
 * The priority an easy-live2d motion gets for a scene: idle scenes must not outrank a tap, which is what stops the
 * framework's own motion queue from burying what the director asked for.
 */
function motionPriority(priority: number): 1 | 2 | 3 {
  if (priority <= 1) return 1
  if (priority <= 3) return 2
  return 3
}

/** A tap scene carries its zone in its name; only these three scenes may borrow the model's own `Tap*` motion. */
function tapZoneFor(name: SceneName): 'head' | 'face' | 'body' | null {
  if (name === 'tapHead') return 'head'
  if (name === 'tapFace') return 'face'
  if (name === 'tapBody') return 'body'
  return null
}

export class Director {
  private readonly host: DirectorHost
  private settings: DirectorSettings

  private running: RunningScene | null = null
  private queued: EmotionMatch | null = null
  /** A tap that woke the pet, held until the wake scene has played: the tap is why the pet woke up. */
  private queuedScene: SceneName | null = null
  /** A greeting that came due while the pet was asleep, held until the wake scene has played. */
  private queuedGreeting = false
  private readonly cooldowns = new Map<string, number>()

  private lastActivityAt: number
  private nextIdleAt: number

  private speakingNow = false
  private nextNodAt = 0

  /** `HH:mm` greetings already fired, keyed `yyyy-mm-dd HH:mm`, so each fires once a day. */
  private readonly greeted = new Set<string>()
  private greetedDay = ''

  constructor(host: DirectorHost, settings: DirectorSettings) {
    this.host = host
    this.settings = settings
    const now = host.now()
    this.lastActivityAt = now
    this.nextIdleAt = now + this.sampleIdleGap()
  }

  configure(settings: DirectorSettings): void {
    this.settings = settings
    this.nextIdleAt = Math.max(this.nextIdleAt, this.host.now() + this.sampleIdleGap())
  }

  /** The scene holding the main slot, or null when the pet is between scenes. */
  get runningScene(): SceneName | null {
    return this.running?.plan.name ?? null
  }

  get isSpeaking(): boolean {
    return this.speakingNow
  }

  // ------------------------------------------------------------------ triggers

  /** Any pointer interaction: it resets the idle gap and postpones sleep. */
  notify(): void {
    const now = this.host.now()
    this.lastActivityAt = now
    this.nextIdleAt = now + this.sampleIdleGap()
  }

  /** A new reply arrived, so the pet is not idle; if it had gone to sleep, it wakes. */
  reply(): void {
    this.notify()
    if (this.host.sleeping()) this.wake(this.host.now())
  }

  /**
   * Leaves the sleeping state and releases the held `sleep` track: that track is written every frame, so flipping
   * the flag alone would leave the eyes shut and the head down.
   */
  private wake(now: number): void {
    this.host.setSleeping(false)
    this.host.action.stopAction('sleep')
    this.run(planScene(WAKE_SCENE, this.host.random), now)
  }

  /**
   * Asks for a scene. While asleep, a drag or fling plays its reaction without waking the pet, a hover is dropped,
   * and anything else wakes it.
   */
  scene(scene: SceneName, now = this.host.now()): 'ran' | 'dropped' {
    if (scene === 'hover') {
      // An overlay: it plays its one step and never takes the main slot, so a tap in the same tick still wins.
      if (this.host.sleeping()) return 'dropped'
      const plan = planScene('hover', this.host.random)
      this.playSteps(plan, { plan, startedAt: now, nextStep: 0 })
      return 'ran'
    }

    if (this.host.sleeping()) {
      if (scene === 'drag' || scene === 'fling') {
        return this.run(planScene(scene, this.host.random), now)
      }
      if (scene === WAKE_SCENE) return this.run(planScene(WAKE_SCENE, this.host.random), now)

      // The tap is why the pet woke: the wake scene plays, then the tap's own reaction. The wake alone barely moves
      // the model, so dropping the tap would make the first click look like a non-event.
      this.queuedScene = scene
      this.wake(now)
      return 'ran'
    }

    if (scene === 'sleep') {
      const result = this.run(planScene('sleep', this.host.random), now)
      if (result === 'ran') this.host.setSleeping(true)
      return result
    }

    return this.run(planScene(scene, this.host.random), now)
  }

  /** Plays an emotion scene, or queues it behind one that outranks it. Cooldown and repeat handling live here. */
  emotion(match: EmotionMatch, now = this.host.now()): 'ran' | 'queued' | 'dropped' {
    const emotion = match.emotion
    if (emotion === null) return 'dropped'

    if (now < (this.cooldowns.get(emotion) ?? 0)) {
      // The same emotion again only refreshes how long that face stays: replaying the motion would twitch. A
      // sleeping pet keeps its face, though — a mood change while asleep is not a thing anyone can see.
      if (!this.host.sleeping()) this.host.action.expression(emotion as ExpressionName)
      return 'dropped'
    }

    this.cooldowns.set(emotion, now + Math.max(0, this.settings.emotionCooldownSeconds) * 1000)

    if (this.host.sleeping()) {
      // Queue depth one: the emotion waits behind the wake scene and runs when it finishes.
      this.queued = match
      this.wake(now)
      return 'queued'
    }

    const emotionPriority = SCENE_PRIORITY.emotion
    if (this.running !== null && this.running.plan.priority > emotionPriority) {
      // A tap or a drag outranks a mood from a reply; the newest emotion replaces an unplayed older one, because
      // the newest reply is the topic.
      this.queued = match
      return 'queued'
    }

    return this.runEmotion(match, now)
  }

  /**
   * Voice or TTS started or stopped. The mouth flap rides on top of whatever the main slot is doing; the periodic
   * nod is the only thing the director adds while speaking.
   */
  speaking(moving: boolean): void {
    if (moving === this.speakingNow) return
    this.speakingNow = moving
    this.host.action.mouth(moving)
    if (!moving) return

    this.nextNodAt = this.host.now() + SPEAKING_NOD_INTERVAL_MS
    if (this.running === null && this.queued === null) {
      this.host.action.expression('smile')
    }
  }

  // ------------------------------------------------------------------ ticking

  /** Advances the main slot and its due steps, then handles speaking, greetings, idle sampling and sleep. */
  update(nowMs: number): void {
    if (this.running !== null) {
      const plan = this.running.plan
      for (;;) {
        const step = plan.steps[this.running.nextStep]
        if (step === undefined || nowMs - this.running.startedAt < step.delay) break
        const options = this.stepOptions(plan, this.running.nextStep)
        this.running.nextStep += 1
        this.host.action.play(step.action, options)
      }

      if (nowMs - this.running.startedAt >= plan.duration) {
        this.running = null
        this.flushQueue(nowMs)
      }
    }

    if (this.queued !== null && this.running === null) {
      this.flushQueue(nowMs)
    }

    // No nod while asleep: it would cover the held sleep pose's lowered head. The mouth flap still runs.
    if (this.speakingNow && !this.host.sleeping() && nowMs >= this.nextNodAt) {
      this.nextNodAt = nowMs + SPEAKING_NOD_INTERVAL_MS
      this.host.action.play('nod', { priority: 2 })
    }

    this.tickGreetings(nowMs)
    this.tickIdle(nowMs)
    this.tickSleep(nowMs)
  }

  /**
   * A local `HH:mm` that has come round fires once. The day is tracked so the same minute recurs the next day
   * instead of being suppressed forever.
   */
  private tickGreetings(nowMs: number): void {
    if (this.settings.greetingTimes.length === 0) return
    const date = new Date(nowMs)
    const day = `${date.getFullYear()}-${date.getMonth() + 1}-${date.getDate()}`
    if (day !== this.greetedDay) {
      this.greetedDay = day
      this.greeted.clear()
    }

    const slot = `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
    if (!this.settings.greetingTimes.includes(slot)) return

    const key = `${day} ${slot}`
    if (this.greeted.has(key)) return
    this.greeted.add(key)

    if (this.host.sleeping()) {
      // The wake scene outranks the greeting, so the greeting waits for it rather than being dropped by it.
      this.queuedGreeting = true
      this.wake(nowMs)
      return
    }
    this.run(planScene('greeting', this.host.random), nowMs)
  }

  private tickIdle(nowMs: number): void {
    if (nowMs < this.nextIdleAt) return
    if (this.running !== null) {
      // Waiting for the slot to clear; retry shortly rather than losing this gap's idle.
      this.nextIdleAt = nowMs + 250
      return
    }
    if (this.host.sleeping()) return

    this.nextIdleAt = nowMs + this.sampleIdleGap()
    this.run(planScene('idle', this.host.random), nowMs)
  }

  private tickSleep(nowMs: number): void {
    if (this.host.sleeping()) return
    const minutes = this.settings.sleepAfterMinutes
    if (minutes <= 0) return
    if (nowMs - this.lastActivityAt < minutes * 60_000) return
    // A tap or a greeting in progress outranks the sleep scene, which is retried on the next tick.
    if (this.running !== null && this.running.plan.name !== 'idle') return

    this.scene('sleep', nowMs)
  }

  // ------------------------------------------------------------------ internals

  private sampleIdleGap(): number {
    const min = Math.max(1, this.settings.idleMinSeconds)
    const max = Math.max(min, this.settings.idleMaxSeconds)
    return (min + this.host.random() * (max - min)) * 1000
  }

  private run(plan: ScenePlan, now: number): 'ran' | 'dropped' {
    const current = this.running
    if (current !== null && plan.priority < current.plan.priority) return 'dropped'

    const scene: RunningScene = { plan, startedAt: now, nextStep: 0 }
    this.running = scene
    this.playSteps(plan, scene)

    if (plan.name !== 'idle' && plan.name !== 'sleep') {
      this.lastActivityAt = now
      this.nextIdleAt = now + this.sampleIdleGap()
    }
    return 'ran'
  }

  /** Steps with no delay fire immediately; the rest wait for {@link update}, keeping them on the scene clock. */
  private playSteps(plan: ScenePlan, scene: RunningScene): void {
    for (;;) {
      const step = plan.steps[scene.nextStep]
      if (step === undefined || step.delay > 0) break
      this.host.action.play(step.action, this.stepOptions(plan, scene.nextStep))
      scene.nextStep += 1
    }
    if (plan.expression !== null) this.host.action.expression(plan.expression)
  }

  /**
   * Only the first step of a tap scene carries the zone; every other step resolves against the model's generic
   * keywords and procedural tracks, so `blink`, `wake` or `greet` never play as a `Tap` motion.
   */
  private stepOptions(plan: ScenePlan, index: number): { priority: 1 | 2 | 3; tapZone?: 'head' | 'face' | 'body' } {
    const priority = motionPriority(plan.priority)
    const zone = index === 0 ? tapZoneFor(plan.name) : null
    return zone === null ? { priority } : { priority, tapZone: zone }
  }

  private runEmotion(match: EmotionMatch, now: number): 'ran' | 'dropped' {
    const emotion = match.emotion
    if (emotion === null) return 'dropped'
    return this.run(planScene('emotion', this.host.random, emotion), now)
  }

  /**
   * Starts at most one queued scene per call; `update` calls this again when that scene ends. Starting them all at
   * once would let `run` drop the lower-priority ones after their queue slots were already cleared.
   */
  private flushQueue(now: number): void {
    if (this.queuedScene !== null) {
      const scene = this.queuedScene
      this.queuedScene = null
      this.run(planScene(scene, this.host.random), now)
      return
    }

    if (this.queued !== null) {
      const queued = this.queued
      this.queued = null
      this.runEmotion(queued, now)
      return
    }

    if (this.queuedGreeting) {
      this.queuedGreeting = false
      this.run(planScene('greeting', this.host.random), now)
    }
  }
}