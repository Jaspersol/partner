/**
 * The scene table: what the pet does for each situation. A scene is data (abstract actions with delays, an
 * expression, a priority and a duration), which keeps director.ts a small state machine.
 */

import type { ActionName, ExpressionName } from './actions'

export type SceneName =
  | 'load'
  | 'idle'
  | 'tapHead'
  | 'tapFace'
  | 'tapBody'
  | 'hover'
  | 'drag'
  | 'fling'
  | 'doubleTap'
  | 'greeting'
  | 'sleep'
  | 'wake'
  | 'emotion'

interface SceneStep {
  action: ActionName
  /** Milliseconds after the scene starts. */
  delay: number
}

export interface ScenePlan {
  name: SceneName
  /** Higher wins; a scene runs only when its priority is ≥ the running one's. */
  priority: number
  /** How long the director holds the slot, in milliseconds. */
  duration: number
  steps: SceneStep[]
  /** Expression to show, or null to leave whatever is showing. */
  expression: ExpressionName | null
}

/** `hover` is an overlay: it runs beside the main scene and never takes the slot, so its priority is never compared. */
export const SCENE_PRIORITY: Record<SceneName, number> = {
  load: 2,
  idle: 1,
  tapHead: 4,
  tapFace: 4,
  tapBody: 4,
  hover: 0,
  drag: 5,
  fling: 5,
  doubleTap: 4,
  greeting: 2,
  sleep: 1,
  wake: 4,
  emotion: 3,
}

function pick<T>(random: () => number, options: readonly T[]): T {
  const index = Math.min(options.length - 1, Math.max(0, Math.floor(random() * options.length)))
  return options[index] as T
}

/** Builds the plan for one scene. `random` is the director's injected source, so a fixed source gives a fixed scene. */
export function planScene(name: SceneName, random: () => number, emotion?: string): ScenePlan {
  switch (name) {
    case 'load':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 2200,
        steps: [
          { action: 'greet', delay: 0 },
          { action: 'blink', delay: 900 },
        ],
        expression: 'smile',
      }

    case 'idle': {
      // 45% greeting, 45% stretching, 10% an emphatic blink.
      const roll = random()
      if (roll < 0.45) {
        return {
          name,
          priority: SCENE_PRIORITY[name],
          duration: 2200,
          steps: [{ action: 'greet', delay: 0 }],
          expression: null,
        }
      }
      if (roll < 0.9) {
        return {
          name,
          priority: SCENE_PRIORITY[name],
          duration: 2800,
          steps: [{ action: 'stretch', delay: 0 }],
          expression: 'default',
        }
      }
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 600,
        steps: [{ action: 'blink', delay: 0 }],
        expression: null,
      }
    }

    case 'tapHead':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 1600,
        steps: [{ action: pick(random, ['pat', 'tilt', 'headShake'] as const), delay: 0 }],
        expression: random() < 0.5 ? 'shy' : 'angry',
      }

    case 'tapFace':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 1200,
        steps: [
          { action: 'blink', delay: 0 },
          { action: 'surprise', delay: 180 },
        ],
        expression: random() < 0.5 ? 'smile' : 'shy',
      }

    case 'tapBody':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 1600,
        steps: [{ action: pick(random, ['bodyShake', 'shrink'] as const), delay: 0 }],
        expression: random() < 0.5 ? 'shy' : 'angry',
      }

    case 'hover':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 1200,
        steps: [{ action: 'tilt', delay: 0 }],
        expression: null,
      }

    case 'drag':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 700,
        steps: [{ action: 'dragSpring', delay: 0 }],
        expression: null,
      }

    case 'fling':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 1800,
        steps: [
          { action: 'headShake', delay: 0 },
          { action: 'bodyShake', delay: 220 },
          { action: 'dragSpring', delay: 0 },
        ],
        // A fling is startling, not bashful.
        expression: 'surprise',
      }

    case 'doubleTap':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 2400,
        steps: [
          { action: 'greet', delay: 0 },
          { action: 'wave', delay: 0 },
          { action: 'blink', delay: 500 },
        ],
        expression: 'smile',
      }

    case 'greeting':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 2400,
        steps: [
          { action: 'greet', delay: 0 },
          { action: 'wave', delay: 0 },
        ],
        expression: 'smile',
      }

    case 'sleep':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 3200,
        steps: [
          { action: 'stretch', delay: 0 },
          { action: 'sleep', delay: 2200 },
        ],
        expression: 'default',
      }

    case 'wake':
      return {
        name,
        priority: SCENE_PRIORITY[name],
        duration: 1200,
        // The eyes open as the scene starts — that is the wake-up — and the blink follows as the "am I awake?".
        steps: [
          { action: 'wake', delay: 0 },
          { action: 'blink', delay: 700 },
        ],
        expression: 'smile',
      }

    case 'emotion':
      switch (emotion) {
        case 'angry':
          return {
            name,
            priority: SCENE_PRIORITY[name],
            duration: 1500,
            steps: [{ action: 'puff', delay: 0 }],
            expression: 'angry',
          }
        case 'cry':
          return {
            name,
            priority: SCENE_PRIORITY[name],
            duration: 2200,
            steps: [{ action: 'sob', delay: 0 }],
            expression: 'cry',
          }
        case 'shy':
          return {
            name,
            priority: SCENE_PRIORITY[name],
            duration: 1500,
            steps: [{ action: pick(random, ['tilt', 'shrink'] as const), delay: 0 }],
            expression: 'shy',
          }
        default:
          return {
            name,
            priority: SCENE_PRIORITY[name],
            duration: 1600,
            steps: [{ action: pick(random, ['wave', 'nod'] as const), delay: 0 }],
            expression: 'smile',
          }
      }
  }
}

/** How often the pet nods while speaking; the mouth flap itself is driven by the action layer. */
export const SPEAKING_NOD_INTERVAL_MS = 3500