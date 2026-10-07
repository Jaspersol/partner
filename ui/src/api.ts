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
  /** True for the reply being written right now: it is not in the log yet, and has seq 0. */
  streaming?: boolean
  /**
   * The event's annotations, verbatim: tool id/name/args/ok, turn reason, and so on. A tool call and its
   * result are two events, and this is what pairs them into one card.
   */
  meta?: Record<string, string> | null
  /** What the model is thinking right now. Only ever set on a live row. */
  reasoning?: string | null
  /** When that thinking started, so the view can say how long it has been. Absent when it is over. */
  reasoningSince?: string | null
}

/** How much of the model's context window the history currently occupies. */
export interface ContextUsage {
  usedTokens: number
  budgetTokens: number
  contextTokens: number
  maxTokens: number
  keptItems: number
  foldedItems: number
  fraction: number
}

export interface SessionView {
  lastSeq: number
  items: SessionItem[]
  /** True while a turn is running: the view re-reads itself quickly so the turn is visible as it happens. */
  busy?: boolean
  context: ContextUsage
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
  /** Attachment ids the item carries; the bytes live in the media store, never in the queue log. */
  mediaIds?: string[]
  /** The gear this message chose, or null when it did not choose and the default applies. */
  reasoningEffort?: string | null
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
  /** When this record was last handed to a turn. Null means never recalled — the demotion rule counts that as unused. */
  lastRecalledAt?: string | null
  validTo: string | null
  supersededBy: string | null
  editedBy: string | null
  importance: number
}

/**
 * One MCP server, in the shape the settings file stores it.
 * <p>
 * Enabling starts a <b>child process</b> — which is why the capability is off by default and why a server
 * missing a name or command is refused at save rather than at start-up.
 */
export interface McpServerSettings {
  name: string
  command: string
  arguments: string[]
  workingDirectory: string | null
  callTimeoutSeconds: number
}

export interface McpSettings {
  enabled: boolean
  servers: McpServerSettings[]
  /** Tools that may run without asking. Empty means every one of them is asked for. */
  allowedTools: string[]
}

/** One workspace skill, as the read endpoint reports it. */
export interface SkillView {
  skillId: string
  name: string
  description: string
  enabled: boolean
  instructions: string
}

export interface SkillListView {
  skills: SkillView[]
  /** Why a directory was skipped. Shown rather than swallowed: a skill that did not load is otherwise invisible. */
  warnings: string[]
}

export interface SkillSettings {
  /** Skill ids to skip. A deny list, so a skill added later is on by default. */
  disabled: string[]
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

export interface InputSettings {
  enabled: boolean
  deviceId: string | null
  silenceThreshold: number
  utteranceGapMs: number
  sentenceGapMs: number
  windowSeconds: number
  asrStream: boolean
  maxSplitChars: number
}

export interface OutputSettings {
  enabled: boolean
  deviceId: string | null
  voice: string | null
  speed: number
  gapMs: number
  drainMs: number
  sampleRate: number
  muteDuringTts: boolean
}

export interface AsrSettings {
  baseUrl: string | null
  model: string
  language: string | null
  connectTimeoutMs: number
  readTimeoutMs: number
  sampleRate: number
}

export interface TtsSettings {
  baseUrl: string | null
  lang: string
  sampleRate: number
  speakerRefPath: string | null
  timeoutSeconds: number
  chunkMs: number
}

/** What a client sends: the key is optional and write-only. */
export interface ModelSettings {
  baseUrl: string | null
  model: string | null
  /** Absent keeps the stored key, empty clears it, a value replaces it. */
  apiKey?: string | null
  timeoutSeconds: number
  /** The model's context window in tokens. */
  contextTokens: number
  /** Output ceiling for one completion. */
  maxTokens: number
  /** Whether this model can look at pictures: the switch that makes screenshots the blind-app fallback. */
  supportsImages: boolean
  /** Default gear sent as reasoning_effort; `off` (or blank) sends nothing. Free text, not a closed list. */
  reasoningEffort: string

  /** The gears the UI offers as a menu, in the order the user arranged them. */
  reasoningGears: string[]
}

/** What the API returns: no key, only whether one is stored. */
export interface ModelSettingsView {
  baseUrl: string | null
  model: string | null
  apiKeySet: boolean
  timeoutSeconds: number
  contextTokens: number
  maxTokens: number
  /** Whether the model takes pictures; the screenshot fallback is registered from this. */
  supportsImages: boolean
  /** The default gear the consumer is actually sending; normalized to lower case. */
  reasoningEffort: string

  /** The gears offered as a menu, in the order the user arranged them. */
  reasoningGears: string[]
}

export interface RuntimeSettings {
  input: InputSettings
  output: OutputSettings
  asr: AsrSettings
  tts: TtsSettings
  model: ModelSettings
  desktop: DesktopSettings
  mcp: McpSettings
  skills: SkillSettings
}

export interface SettingsView {
  input: InputSettings
  output: OutputSettings
  asr: AsrSettings
  tts: TtsSettings
  model: ModelSettingsView
  desktop: DesktopSettings
  mcp: McpSettings
  skills: SkillSettings
}

/**
 * The desktop capability (M9). Off by default: it is the only irreversible capability here, and enabling it
 * lets the model *ask* to act — every action still waits for approval.
 */
export interface DesktopSettings {
  enabled: boolean
  leaseTtlMs: number
  /** 'none' | 'dangerous' | 'all' — see ApprovalGear. */
  approval: string
  allowedWindowTitles: string[]
}

/** The three approval gears, in the order the UI offers them. */
export const APPROVAL_GEARS = [
  { value: 'none', label: '不审批', hint: '模型直接动手，不留问' },
  { value: 'dangerous', label: '危险指令审批', hint: '执行命令 / 启动程序 / 写剪贴板要批准' },
  { value: 'all', label: '全部审批', hint: '每个桌面动作都要批准（看屏幕不算）' },
] as const

export interface DesktopState {
  enabled: boolean
  leaseTtlMs: number
  approval: string
  allowedWindowTitles: string[]
  lastObsId: string | null
  entityCount: number
  /** The windows from the last observation: an approval names an entityId, and this is how a human reads it. */
  entities: { entityId: string; title: string; process: string | null; foreground: boolean }[]
}

export interface DesktopEntity {
  entityId: string
  title: string
  process: string
  rect: { x: number; y: number; width: number; height: number }
}

export interface DesktopObservation {
  obsId: string
  count: number
  entities: DesktopEntity[]
}

/** A tool call waiting for a human decision. */
export interface PendingApproval {
  approvalId: string
  toolName: string
  arguments: string
  requestedAt: string
  expiresAt: string
}

export interface ApprovalQueue {
  timeoutSeconds: number
  pending: PendingApproval[]
}

/** Saving returns what was stored plus the consumer state it produced. */
/** What one compaction pass did; null when the context was not shrunk. */
export interface CompactionReport {
  foldedEvents: number
  summarySeq: number | null
  reason: string
  compacted: boolean
}

export interface SettingsSaved {
  settings: SettingsView
  consumer: ConsumerState
  voice: VoiceState
  compaction: CompactionReport | null
}

/** What a delivery lands as: a queue item, not an executed turn. */
export interface SubmitResult {
  itemId: string
  seq: number
  status: string
  deduplicated: boolean
}

/** Whether anything is executing the queue, and if not, why. */
/** One tool the model can call, with what governance will do about it. */
export interface ToolExposure {
  name: string
  decision: 'allow' | 'ask' | string
}

export interface ConsumerState {
  running: boolean
  reason: string
  model: string | null
  stats: {
    completed: number
    failed: number
    canceled: number
    skipped: number
    handled: number
  }
  /** What the model can actually call. A tool that is not here is not a capability. */
  tools: ToolExposure[]
}

/** One voice half: whether it is up, on which device, and why not. */
export interface VoiceHalfState {
  running: boolean
  reason: string
  device: string | null
  count: number
}

/** Device names the runtime can actually see. */
export interface DeviceList {
  inputs: string[]
  outputs: string[]
}

/** What a probe of the model endpoint found. An empty list with an error is a real answer. */
export interface ModelProbe {
  baseUrl: string
  models: string[]
  error?: string
}

export interface VoiceState {
  input: VoiceHalfState
  output: VoiceHalfState
}

export interface RebuildReport {
  sessionItems: number
  sessionLastSeq: number
  memoryIndexed: number
  queueLogEntries: number
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
/** One stored attachment, as the upload endpoint reports it. */
export interface UploadedMedia {
  mediaId: string
  contentType: string
  /** The name the person gave it, sanitised by the server. This is what the model is told to call the file. */
  fileName: string
  /** Where it lives on disk, so a message can hand the model a path it can actually open. */
  path: string
  bytes: number
}

/**
 * How a file name travels with its bytes.
 *
 * <para>
 * A header rather than the body, so a <code>File</code> can still be posted straight through without being read
 * into memory first — which is what makes a folder of attachments possible at all.
 * </para>
 */
const FILE_NAME_HEADER = 'X-File-Name'

export interface PartnerApiSurface {
  session(from?: number, take?: number): Promise<SessionView>
  editSession(seq: number, body: string, actor?: string): Promise<void>
  deleteEvent(seq: number): Promise<void>
  retractFrom(seq: number): Promise<void>
  sessionRevisions(seq: number): Promise<SessionRevision[]>
  queue(): Promise<QueueView>
  cancel(itemId: string): Promise<boolean>
  submitInput(
    payload: string,
    source: string,
    options?: { idempotencyKey?: string; media?: string[]; reasoningEffort?: string | null },
  ): Promise<SubmitResult>
  uploadMedia(file: Blob, contentType?: string, fileName?: string): Promise<UploadedMedia>
  mediaBlob(mediaId: string): Promise<Blob>
  /** Every record, including the ones a later statement retired — "fully visible" means seeing what replaced it. */
  memoryAll(): Promise<MemoryItem[]>
  /** Moves a record between tiers on the person's judgement. */
  setMemoryTier(recordId: string, tier: string): Promise<void>
  /** Pins a record against demotion and trimming. Blocks forgetting, not correction. */
  setMemoryScope(recordId: string, scope: string): Promise<void>
  /** Adds a memory the person typed, rather than one the distiller kept. Resolves to the new record id. */
  createMemory(input: { body: string; tier?: string; scope?: string }): Promise<string>
  /** What skills the workspace holds, plus the scan warnings for the ones that were skipped. */
  skills(): Promise<SkillListView>
  consumer(): Promise<ConsumerState>
  voice(): Promise<VoiceState>
  devices(): Promise<DeviceList>
  listModels(baseUrl?: string): Promise<ModelProbe>
  memory(tier?: string, scope?: string, take?: number): Promise<MemoryItem[]>
  recall(query: string, limit?: number): Promise<RecallHit[]>
  editMemory(recordId: string, body: string, actor?: string): Promise<void>
  deleteMemory(recordId: string): Promise<void>
  memoryRevisions(recordId: string): Promise<MemoryRevision[]>
  settings(): Promise<SettingsView>
  approvals(): Promise<ApprovalQueue>
  answerApproval(approvalId: string, approved: boolean): Promise<void>
  desktop(): Promise<DesktopState>
  observeDesktop(): Promise<DesktopObservation>
  setApprovalGear(approval: string): Promise<{ approval: string; changed: boolean }>
  saveSettings(settings: RuntimeSettings): Promise<SettingsSaved>
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

  /** Removes one event from the view and from the log; seqs are never reused. */
  async deleteEvent(seq: number): Promise<void> {
    await this.request('POST', `/api/session/${seq}/delete`)
  }

  /** Removes the selected event and everything after it, as of now. */
  async retractFrom(seq: number): Promise<void> {
    await this.request('POST', `/api/session/${seq}/retract`)
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

  /**
   * Delivers to the queue. Nothing executes it here, so the answer says "queued", not "done".
   * <para>
   * The per-message parts are an object rather than two more positional parameters: a caller reading
   * `submitInput(text, 'text', 'composer:1', ids, 'high')` cannot tell what the last two mean.
   * </para>
   */
  submitInput(
    payload: string,
    source: string,
    options: { idempotencyKey?: string; media?: string[]; reasoningEffort?: string | null } = {},
  ): Promise<SubmitResult> {
    const { idempotencyKey, media, reasoningEffort } = options

    return this.request<SubmitResult>('POST', '/api/input', {
      source,
      payload,
      idempotencyKey,
      media: media && media.length > 0 ? media : null,

      // Null when the composer did not choose: the server then applies the configured default, which is a
      // different outcome from sending "off" and must not collapse into it.
      reasoningEffort: reasoningEffort || null,
    })
  }

  /**
   * Uploads one attachment. The bytes go as the request body with the type and the name in headers, so a File
   * can be passed straight through without being read into memory first; what comes back is the id the delivery
   * refers to, plus the name and path the model is told.
   */
  async uploadMedia(file: Blob, contentType?: string, fileName?: string): Promise<UploadedMedia> {
    const type = contentType ?? file.type ?? ''
    const response = await this.fetchImpl(`${this.base}/api/media`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${this.token}`,
        'Content-Type': type,
        // Latin-1 only: a header has to be representable, and the server sanitises the name anyway.
        [FILE_NAME_HEADER]: encodeURIComponent(fileName ?? (file instanceof File ? file.name : 'attachment')),
      },
      body: file,
    })

    if (!response.ok) {
      const detail = await response.text().catch(() => '')
      throw new ApiError(response.status, detail || `上传失败（${response.status}）`)
    }

    return (await response.json()) as UploadedMedia
  }

  /**
   * The bytes of an attachment, fetched with the token rather than linked to: an <img src> cannot carry an
   * Authorization header, and putting the token in a URL would leak it into logs and history.
   */
  async mediaBlob(mediaId: string): Promise<Blob> {
    const response = await this.fetchImpl(`${this.base}/api/media/${encodeURIComponent(mediaId)}`, {
      headers: { Authorization: `Bearer ${this.token}` },
    })

    if (!response.ok) {
      throw new ApiError(response.status, `读取附图失败（${response.status}）`)
    }

    return response.blob()
  }

  /** Tool calls waiting for a human. Nobody answering means denied, so the UI is part of the safety path. */
  approvals(): Promise<ApprovalQueue> {
    return this.request<ApprovalQueue>('GET', '/api/approvals')
  }

  answerApproval(approvalId: string, approved: boolean): Promise<void> {
    return this.request<void>('POST', `/api/approvals/${approvalId}`, { approved })
  }

  desktop(): Promise<DesktopState> {
    return this.request<DesktopState>('GET', '/api/desktop')
  }

  /**
   * Sets the approval gear. A focused call rather than a settings PUT: the composer control has no business
   * round-tripping the whole form, and a partial PUT would reset the sections it does not mention.
   */
  setApprovalGear(approval: string): Promise<{ approval: string; changed: boolean }> {
    return this.request<{ approval: string; changed: boolean }>('POST', '/api/desktop/approval', { approval })
  }

  observeDesktop(): Promise<DesktopObservation> {
    return this.request<DesktopObservation>('POST', '/api/desktop/observe')
  }

  /** Whether the queue is being executed at all. Without this the UI cannot explain a stuck queue. */
  consumer(): Promise<ConsumerState> {
    return this.request<ConsumerState>('GET', '/api/consumer')
  }

  /** Whether the two voice halves are up. Without this a disabled microphone looks like a broken one. */
  voice(): Promise<VoiceState> {
    return this.request<VoiceState>('GET', '/api/voice')
  }

  /** Proxied through the API: the device list is a runtime fact, not something the browser can ask for. */
  devices(): Promise<DeviceList> {
    return this.request<DeviceList>('GET', '/api/voice/devices')
  }

  /**
   * Asks the API to probe the model endpoint's listing. Proxied because the endpoint will not allow the
   * webview's origin and the API key must not travel to the browser.
   */
  listModels(baseUrl?: string): Promise<ModelProbe> {
    return this.request<ModelProbe>('POST', '/api/models', { baseUrl: baseUrl ?? null })
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

  memoryAll(): Promise<MemoryItem[]> {
    return this.request<MemoryItem[]>('GET', '/api/memory/all')
  }

  skills(): Promise<SkillListView> {
    return this.request<SkillListView>('GET', '/api/skills')
  }

  async setMemoryTier(recordId: string, tier: string): Promise<void> {
    await this.request('POST', `/api/memory/${encodeURIComponent(recordId)}/tier`, { tier })
  }

  /** Pins a record against forgetting, or releases it. Deliberately separate from the tier: trust and time are not the same axis. */
  async setMemoryScope(recordId: string, scope: string): Promise<void> {
    await this.request('POST', `/api/memory/${encodeURIComponent(recordId)}/scope`, { scope })
  }

  /** Writes a memory by hand. The server decides the defaults: an established fact, subject to the ordinary forgetting rules. */
  async createMemory(input: { body: string; tier?: string; scope?: string }): Promise<string> {
    const created = await this.request<{ recordId: string }>('POST', '/api/memory', input)
    return created.recordId
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

  settings(): Promise<SettingsView> {
    return this.request<SettingsView>('GET', '/api/settings')
  }

  saveSettings(settings: RuntimeSettings): Promise<SettingsSaved> {
    return this.request<SettingsSaved>('PUT', '/api/settings', settings)
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
