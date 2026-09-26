/**
 * The wire contract with Partner.Api, mirroring UiClient.cs one-for-one. These types *are* the interface:
 * the API returns anonymous JSON objects, so the field names below are pinned by tests on both sides.
 */

export interface SessionItem {
  seq: number
  at: string
  kind: string
  source: string
  body: string
  originalBody: string | null
  revisionCount: number
}

export interface SessionView {
  lastSeq: number
  items: SessionItem[]
}

export interface SessionRevision {
  seq: number
  at: string
  body: string
}

export interface QueueItem {
  itemId: string
  seq: number
  source: string
  status: string
  enqueuedAt: string
  startedAt: string | null
  finishedAt: string | null
  attempt: number
  error: string | null
  payload: string
}

export interface QueueView {
  headSeq: number
  runningItemId: string | null
  depth: number
  items: QueueItem[]
}

export interface MemoryItem {
  recordId: string
  tier: string
  scope: string
  body: string
  createdAt: string
  validTo: string | null
  supersededBy: string | null
  editedBy: string | null
  importance: number
}

export interface RecallHit {
  recordId: string
  body: string
  total: number
  reasons: string[]
}

export interface MemoryRevision {
  at: string
  before: string
  after: string
  actor: string
}

export interface VoiceSettings {
  inputEnabled: boolean
  inputDeviceId: string | null
  silenceThreshold: number
  utteranceGapMs: number
  sentenceGapMs: number
  windowSeconds: number
  asrStream: boolean
  outputEnabled: boolean
  outputDeviceId: string | null
  speed: number
  gapMs: number
  drainMs: number
  muteDuringTts: boolean
}

export interface RebuildReport {
  sessionItems: number
  sessionLastSeq: number
  memoryIndexed: number
  queueLogEntries: number
}

/** What a delivery lands as: a queue item, not an executed turn. */
export interface SubmitResult {
  itemId: string
  seq: number
  status: string
  deduplicated: boolean
}

/** Whether anything is executing the queue, and if not, why. */
export interface ConsumerState {
  running: boolean
  reason: string
  model: string | null
  stats: { completed: number; failed: number; canceled: number; skipped: number; handled: number }
}

/** The queue's shape reduced to what the badge and the status bar show. */
export interface QueueSummary {
  depth: number
  pending: number
  failed: number
  runningItemId: string | null
}

/** One definition, used by the top bar badge, the status bar and the composer's strip — they must agree. */
export function summarizeQueue(view: QueueView): QueueSummary {
  return {
    depth: view.depth,
    pending: view.items.filter((item) => item.status === 'Queued' || item.status === 'Running').length,
    failed: view.items.filter((item) => item.status === 'Failed').length,
    runningItemId: view.runningItemId,
  }
}

/** A non-2xx answer, carrying the API's own error text so the UI can show the real reason. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    detail: string,
  ) {
    super(`${status}: ${detail}`)
    this.name = 'ApiError'
  }
}

export interface ApiOptions {
  port: number
  token: string
  /** Test seam: lets a test observe requests without a real socket. */
  fetchImpl?: typeof fetch
}

/**
 * Exactly the surface the views use. Views depend on this, so a test stub is checked by the compiler
 * against the same shape the real client exposes — a stub cannot silently drift from the contract.
 */
export interface PartnerApiSurface {
  session(from?: number, take?: number): Promise<SessionView>
  editSession(seq: number, body: string, actor?: string): Promise<void>
  sessionRevisions(seq: number): Promise<SessionRevision[]>
  queue(): Promise<QueueView>
  cancel(itemId: string): Promise<boolean>
  submitInput(payload: string, source: string, idempotencyKey?: string): Promise<SubmitResult>
  consumer(): Promise<ConsumerState>
  memory(tier?: string, scope?: string, take?: number): Promise<MemoryItem[]>
  recall(query: string, limit?: number): Promise<RecallHit[]>
  editMemory(recordId: string, body: string, actor?: string): Promise<void>
  deleteMemory(recordId: string): Promise<void>
  memoryRevisions(recordId: string): Promise<MemoryRevision[]>
  settings(): Promise<VoiceSettings>
  saveSettings(settings: Partial<VoiceSettings>): Promise<VoiceSettings>
  rebuild(): Promise<RebuildReport>
}

export class PartnerApi implements PartnerApiSurface {
  private readonly base: string
  private readonly token: string
  private readonly fetchImpl: typeof fetch

  constructor(options: ApiOptions) {
    this.base = `http://127.0.0.1:${options.port}`
    this.token = options.token
    this.fetchImpl = options.fetchImpl ?? globalThis.fetch.bind(globalThis)
  }

  // ---- session ---------------------------------------------------------------------------------

  session(from = 1, take = 200): Promise<SessionView> {
    return this.request<SessionView>('GET', `/api/session?from=${from}&take=${take}`)
  }

  async editSession(seq: number, body: string, actor = 'human'): Promise<void> {
    await this.request('POST', `/api/session/${seq}/edit`, { body, actor })
  }

  sessionRevisions(seq: number): Promise<SessionRevision[]> {
    return this.request<SessionRevision[]>('GET', `/api/session/${seq}/revisions`)
  }

  // ---- queue -----------------------------------------------------------------------------------

  queue(): Promise<QueueView> {
    return this.request<QueueView>('GET', '/api/queue')
  }

  /** The API answers 404 for an item that is unknown or already finished: nothing to cancel, not an error. */
  async cancel(itemId: string): Promise<boolean> {
    const response = await this.send('POST', `/api/queue/${encodeURIComponent(itemId)}/cancel`)

    if (response.status === 404) {
      return false
    }

    await this.assertOk(response)
    return true
  }

  // ---- input delivery --------------------------------------------------------------------------

  /** Delivers to the queue. Nothing executes it here, so the answer says "queued", not "done". */
  submitInput(payload: string, source: string, idempotencyKey?: string): Promise<SubmitResult> {
    return this.request<SubmitResult>('POST', '/api/input', { source, payload, idempotencyKey })
  }

  /** Whether the queue is being executed at all. Without this the UI cannot explain a stuck queue. */
  consumer(): Promise<ConsumerState> {
    return this.request<ConsumerState>('GET', '/api/consumer')
  }

  // ---- memory ----------------------------------------------------------------------------------

  memory(tier?: string, scope?: string, take = 200): Promise<MemoryItem[]> {
    const query = new URLSearchParams({ take: String(take) })
    if (tier) {
      query.set('tier', tier)
    }
    if (scope) {
      query.set('scope', scope)
    }

    return this.request<MemoryItem[]>('GET', `/api/memory?${query}`)
  }

  recall(query: string, limit = 20): Promise<RecallHit[]> {
    return this.request<RecallHit[]>('POST', '/api/memory/recall', { query, limit })
  }

  async editMemory(recordId: string, body: string, actor = 'human'): Promise<void> {
    await this.request('POST', `/api/memory/${encodeURIComponent(recordId)}/edit`, { body, actor })
  }

  async deleteMemory(recordId: string): Promise<void> {
    await this.request('DELETE', `/api/memory/${encodeURIComponent(recordId)}`)
  }

  memoryRevisions(recordId: string): Promise<MemoryRevision[]> {
    return this.request<MemoryRevision[]>('GET', `/api/memory/${encodeURIComponent(recordId)}/revisions`)
  }

  // ---- settings --------------------------------------------------------------------------------

  settings(): Promise<VoiceSettings> {
    return this.request<VoiceSettings>('GET', '/api/settings')
  }

  saveSettings(settings: Partial<VoiceSettings>): Promise<VoiceSettings> {
    return this.request<VoiceSettings>('PUT', '/api/settings', settings)
  }

  // ---- projections -----------------------------------------------------------------------------

  rebuild(): Promise<RebuildReport> {
    return this.request<RebuildReport>('POST', '/api/projections/rebuild')
  }

  // ---- plumbing --------------------------------------------------------------------------------

  private send(method: string, path: string, payload?: unknown): Promise<Response> {
    const init: RequestInit = {
      method,
      headers: { Authorization: `Bearer ${this.token}` },
    }

    if (payload !== undefined) {
      init.headers = { ...init.headers, 'Content-Type': 'application/json' }
      init.body = JSON.stringify(payload)
    }

    return this.fetchImpl(`${this.base}${path}`, init)
  }

  private async request<T>(method: string, path: string, payload?: unknown): Promise<T> {
    const response = await this.send(method, path, payload)
    await this.assertOk(response)

    return (await response.json()) as T
  }

  private async assertOk(response: Response): Promise<void> {
    if (response.ok) {
      return
    }

    // 401 is worth naming: it means the token is wrong or stale, not that the request was malformed.
    const detail = await response.text()
    throw new ApiError(response.status, detail === '' ? response.statusText : detail)
  }
}
