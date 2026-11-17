/**
 * The pet window's own slice of the API, kept apart from `ui/src/api.ts` so the pet does not depend on the settings
 * page's client. Same wire shapes and bearer token: the shell injects the token and port into both windows.
 */

import type { ActionDiagnostic, ActionMapping } from './actions'
import type { PetFeed } from './feed'

interface PetCoreStatus {
  installed: boolean
}

interface PetMotionInfo {
  group: string
  no: number
  name: string
}

export interface PetModelInfo {
  id: string
  model3: string
  motions: PetMotionInfo[]
  expressions: string[]
}

interface PetEmotionKeyword {
  text: string
  weight: number
}

interface PetEmotionDictionary {
  keywords: Record<string, PetEmotionKeyword[]>
  negationPrefixes: string[]
  minWeight: number
}

interface PetMotionRef {
  kind: string
  group?: string | null
  no?: number | null
}

interface PetExpressionRef {
  kind: string
  name?: string | null
}

interface PetModelProfile {
  motions: Record<string, PetMotionRef>
  expressions: Record<string, PetExpressionRef>
  headBottom: number
  faceBottom: number
  defaultExpression?: string | null
  parameters?: ActionMapping['parameters']
  amplitude?: ActionMapping['amplitude']
  procedural?: ActionMapping['procedural']
}

/** Only the section of the settings document the pet acts on; the rest is the settings page's business. */
export interface PetSettings {
  alwaysOnTop: boolean
  clickThroughTransparent: boolean
  motionSound: boolean
  scale: number
  activeModelId: string | null
  idleMinSeconds: number
  idleMaxSeconds: number
  sleepAfterMinutes: number
  greetingTimes: string[]
  emotionCooldownSeconds: number
  expressionHoldSeconds: number
  fastDragPxPerSecond: number
  emotions: PetEmotionDictionary
  profiles: Record<string, PetModelProfile>
}

interface PetSettingsView {
  pet?: PetSettings
}

declare global {
  interface Window {
    __PARTNER_PORT__?: number
    __PARTNER_TOKEN__?: string
  }
}

export interface PetApi {
  petSettings(): Promise<PetSettingsView>
  petCore(): Promise<PetCoreStatus>
  petModels(): Promise<PetModelInfo[]>
  petFeed(after: number, previewAfter: number): Promise<PetFeed>
  reportPetStatus(error: string | null): Promise<void>
  reportDiagnostics(modelId: string | null, actions: ActionDiagnostic[]): Promise<void>
}

function createPetApi(port: number, token: string): PetApi {
  const base = `http://127.0.0.1:${port}`

  async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = { Authorization: `Bearer ${token}` }
    if (body !== undefined) headers['Content-Type'] = 'application/json'

    const response = await fetch(`${base}${path}`, {
      method,
      headers,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })

    if (!response.ok) {
      const detail = await response.text().catch(() => '')
      throw new Error(`${method} ${path} -> ${response.status}${detail === '' ? '' : `: ${detail}`}`)
    }

    return (await response.json()) as T
  }

  return {
    petSettings: () => request<PetSettingsView>('GET', '/api/settings'),
    petCore: () => request<PetCoreStatus>('GET', '/api/pet/core'),
    petModels: () => request<PetModelInfo[]>('GET', '/api/pet/models'),
    petFeed: (after, previewAfter) =>
      request<PetFeed>(
        'GET',
        `/api/pet/feed?after=${Math.max(0, Math.trunc(after))}&previewAfter=${Math.max(0, Math.trunc(previewAfter))}`,
      ),
    reportPetStatus: async (error) => {
      await request<unknown>('POST', '/api/pet/status', { error })
    },
    reportDiagnostics: async (modelId, actions) => {
      await request<unknown>('POST', '/api/pet/diagnostics', { modelId, actions })
    },
  }
}

/** The defaults the API fills in for an absent section; used when the settings read has not answered yet. */
export const DEFAULT_PET_SETTINGS: PetSettings = {
  alwaysOnTop: true,
  clickThroughTransparent: true,
  motionSound: false,
  scale: 1,
  activeModelId: null,
  idleMinSeconds: 25,
  idleMaxSeconds: 60,
  sleepAfterMinutes: 10,
  greetingTimes: ['09:00', '14:00'],
  emotionCooldownSeconds: 8,
  expressionHoldSeconds: 6,
  fastDragPxPerSecond: 1500,
  emotions: {
    keywords: {},
    negationPrefixes: ['不', '没', '别', '不太', '并不', 'not', 'no', 'never'],
    minWeight: 1,
  },
  profiles: {},
}

/**
 * The connection the shell injected, or null without one: a missing token is not recoverable here, and the window
 * would only produce anonymous 401s.
 */
export function petApiFromWindow(): PetApi | null {
  const token = window.__PARTNER_TOKEN__
  const port = Number(window.__PARTNER_PORT__)
  if (typeof token !== 'string' || token.trim() === '') return null
  if (!Number.isFinite(port) || port <= 0) return null
  return createPetApi(port, token)
}