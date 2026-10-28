import { t, tr } from './i18n'
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

/**
 * What the view may offer at the history's tail.
 *
 * <para>
 * Deleting is only ever the latest conversation, as a whole; withdrawing from an earlier point is the
 * reversible alternative. `withdrawn` is non-null while a withdrawal is still pending — until the person
 * sends a new message, which makes it final on the server.
 * </para>
 */
export interface ConversationView {
  /** Seq of the first message of the latest conversation, or null when there is none. */
  latestFromSeq: number | null
  /** The range hidden by a pending withdrawal, or null when nothing is withdrawn. */
  withdrawn: WithdrawalState | null
}

export interface WithdrawalState {
  fromSeq: number
  toSeq: number
  conversations: number
  events: number
}

/**
 * Which slice of the one session to read. Display only: the log is never split by day.
 *
 * `day` is a local calendar day (yyyy-MM-dd) or "latest". `open: false` reads that day alone; `open: true` reads
 * from the start of that day to the end of the log, which is how a turn that crossed midnight is still shown
 * under the day the reader was on.
 */
export interface SessionWindow {
  day: string
  open: boolean
  /** The reader's UTC offset in minutes: "which day" is their calendar, not the server's. */
  offsetMinutes: number
}

/** A calendar day that has at least one event. Days without events do not exist. */
export interface SessionDay {
  day: string
  count: number
}

export interface SessionView {
  lastSeq: number
  items: SessionItem[]
  /** Every day that has events, oldest first. Present only when a window was asked for. */
  days?: SessionDay[]
  /** The day `items` is a window onto; null when the log is empty. */
  day?: string | null
  /** True while a turn is running: the view re-reads itself quickly so the turn is visible as it happens. */
  busy?: boolean
  conversation: ConversationView
  context: ContextUsage
}

/** What deleting the latest conversation removed: a range, not one row. */
export interface DeleteResult {
  removedFrom: number
  removedTo: number
  purgedEvents: number
  purgedRevisions: number
  purgedMemories: number
  eventSeq: number
}

/** What a withdrawal took, before it is made final by the next message. */
export interface WithdrawResult {
  withdrawnFrom: number
  withdrawnTo: number
  conversations: number
  events: number
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

/** What a person types to write a skill by hand. The server turns it into a SKILL.md. */
export interface NewSkill {
  skillId: string
  name: string
  description: string
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
  /** The record's own tier and retention: a hit is one of the rows the list already shows, not an anonymous match. */
  tier: string
  scope: string
  createdAt: string
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
  /** Ding when the wake word hits; only meaningful while a wake gate is active. */
  wakeTone: boolean
  /** Speaking over the assistant stops it. Off by default; meant for headphones (a speaker is heard by the mic). */
  bargeIn: boolean
  utteranceGapMs: number
  /** Grace period after a wake hit, before the user speaks. Not the sentence gap: thinking time is not "done talking". */
  wakeTimeoutMs: number
  sentenceGapMs: number
  windowSeconds: number
  asrStream: boolean
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
  /** Forced break length for a sentence that carries no punctuation. */
  maxSplitChars: number
  /** Shortest fragment read aloud on its own; shorter pieces are joined to what follows. */
  minSentenceChars: number
}

export interface AsrSettings {
  baseUrl: string | null
  /** 'chat-audio' (Qwen inline base64) | 'openai' (standard /audio/transcriptions multipart). */
  protocol: string
  model: string
  language: string | null
  connectTimeoutMs: number
  readTimeoutMs: number
  sampleRate: number
}

export interface TtsSettings {
  baseUrl: string | null
  /** 'native' (server's /tts raw float32 stream) | 'openai' (standard /audio/speech WAV). */
  protocol: string
  /** OpenAI protocol's model name (e.g. 'tts-1'); unused by native. */
  model: string | null
  /** OpenAI protocol's voice name (e.g. 'alloy'); native clones instead via speakerRefPath. */
  voice: string | null
  /** OpenAI protocol's speech rate; unused by native. */
  speed: number
  lang: string
  sampleRate: number
  speakerRefPath: string | null
  /** What is actually said in the reference clip; blank = zero-shot clone. */
  speakerRefText: string | null
  timeoutSeconds: number
  chunkMs: number
}

/** One local engine's process the runtime manages. Stopping it releases its memory for real. */
export interface LocalEngineSettings {
  enabled: boolean
  /** Executable path; null/empty means not managed even when enabled. */
  command: string | null
  /** Argument template: `__PORT__` is replaced by the port below. */
  arguments: string[]
  workingDirectory: string | null
  port: number
  /** How long the process may take to prove it stays up before being reported started. */
  readyTimeoutSeconds: number
  /**
   * Wake words (display names, Chinese only). Empty = leave whatever the engine has; non-empty is
   * pushed to the engine which transliterates to pinyin, persists and rebuilds its stream.
   */
  keywords: string[]
}

export interface LocalSettings {
  /** TTS server process (OpenAI protocol engine, e.g. qwentts.cpp server). */
  tts: LocalEngineSettings
  /** KWS+VAD+ASR server process (sherpa-onnx engine). */
  kws: LocalEngineSettings
}

export interface LocalEngineState {
  running: boolean
  reason: string
  processId: number | null
}

export interface LocalEnginesState {
  tts: LocalEngineState
  kws: LocalEngineState
}

/** One engine's on-disk readiness. `missing` lists what is absent, relative to the engines folder. */
export interface EngineReadiness {
  installed: boolean
  missing: string[]
}

/** What the page fills in for an engine it has found. Relative to the app dir unless the folder was chosen by hand. */
export interface EngineDefaults {
  command: string
  arguments: string[]
  workingDirectory: string
  port: number
}

export type EngineDownloadPhase = 'idle' | 'downloading' | 'extracting' | 'done' | 'failed' | 'cancelled'

export interface EngineDownloadState {
  phase: EngineDownloadPhase
  detail: string | null
  bytesDone: number
  bytesTotal: number | null
  error: string | null
}

/** The local-engine module (runtimes + models) as a unit that lives beside the app instead of in the installer. */
export interface EnginePackageStatus {
  root: string
  /** True when the user pointed at a folder of their own instead of <app>/engines. */
  customRoot: boolean
  tts: EngineReadiness
  kws: EngineReadiness
  ttsDefaults: EngineDefaults
  kwsDefaults: EngineDefaults
  /** False when no download address is published for what is missing. */
  downloadAvailable: boolean
  download: EngineDownloadState
}

/** What a client sends: the key is optional and write-only. */
export interface ModelSettings {
  /** Wire protocol: `openai` for chat/completions, `anthropic` for the Claude Messages API. */
  protocol: string
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

  /** Display text per gear word (word -> text). Display only: the word itself is what is sent. */
  reasoningLabels?: Record<string, string>
}

/** What the API returns: no key, only whether one is stored. */
export interface ModelSettingsView {
  /** Wire protocol the consumer speaks: `openai` or `anthropic`. */
  protocol: string
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

  reasoningLabels?: Record<string, string>
}

/** The main agent's editable part. Null means the built-in prompt. */
export interface MainAgentSettings {
  prompt: string | null
}

/** A sub-agent's own endpoint, in the shape the settings file stores it (the key is write-only). */
export interface SubAgentModelSettings {
  /** Wire protocol: `openai` or `anthropic`. */
  protocol: string
  baseUrl: string | null
  model: string | null
  /** Absent keeps the stored key, empty clears it, a value replaces it. Never read back. */
  apiKey?: string | null
  timeoutSeconds: number
  contextTokens: number
  maxTokens: number
  supportsImages: boolean
  reasoningEffort: string
}

/** One sub-agent as the settings page carries it. `id: ""` asks the server to name a new one. */
export interface SubAgentSettings {
  id: string
  name: string
  enabled: boolean
  scenario: string
  prompt: string | null
  maxRounds: number
  /** Null = follow the main model. */
  model: SubAgentModelSettings | null
}

export interface AgentsSettings {
  main: MainAgentSettings
  subAgents: SubAgentSettings[]
}

/** The file-reading capability: off by default, and a whitelist rather than a blacklist. */
export interface FilesSettings {
  enabled: boolean
  roots: string[]
}

/**
 * Web search. Enabled by default: it is the only network capability on by default and the only one that sends
 * the machine's text to third-party search services, so the settings page must say so beside it.
 */
export interface WebSettings {
  enabled: boolean
  /** Time budget for a whole search, in seconds, counted from the first engine's result. */
  timeBudgetSeconds: number
  /** Upper bound on results listed in the answer. */
  maxResults: number
  /** http(s) proxy; empty = direct. */
  proxy: string | null
  /** Ids of engines manually turned off. */
  disabledEngines: string[]
}

/** One engine with its current runtime state: circuit-broken or normal. */
export interface WebEngineView {
  id: string
  name: string
  /** 'ranked' (documents sorted by relevance) or 'record' (the publisher's own record, which is the answer). */
  nature: string
  /** Content types it indexes, lower-case names: general / news / academic / code / encyclopedia / weather / qa. */
  kinds: string[]
  /** The site's own catalogue (Wikipedia, arXiv): results do not restate the query. */
  singleSite: boolean
  disabled: boolean
  cooling: boolean
  until: string | null
  strikes: number
  lastReason: string | null
}

/** GET /api/web: settings plus each engine's state. */
export interface WebView {
  enabled: boolean
  proxy: string | null
  timeBudgetSeconds: number
  maxResults: number
  engines: WebEngineView[]
  /** Location of the circuit-breaker state file; this is what to inspect when something goes wrong. */
  healthPath: string
}

/** A sub-agent's endpoint as the read endpoints report it: whether a key is stored, never the key. */
export interface SubAgentModelView {
  protocol: string
  baseUrl: string | null
  model: string | null
  apiKeySet: boolean
  timeoutSeconds: number
  contextTokens: number
  maxTokens: number
  supportsImages: boolean
  reasoningEffort: string
}

/** A sub-agent as the settings response carries it. Same as the stored shape except for the masked key. */
export interface SubAgentSettingsView {
  id: string
  name: string
  enabled: boolean
  scenario: string
  prompt: string | null
  maxRounds: number
  model: SubAgentModelView | null
}

export interface AgentsSettingsView {
  main: MainAgentSettings
  subAgents: SubAgentSettingsView[]
}

export interface MainAgentView {
  prompt: string | null
  defaultPrompt: string
  /** What the model really sees: the person's prompt (or default) plus the generated blocks. */
  effectivePrompt: string
  promptTokens: number
}

/** One sub-agent as the prompts page reads it: stored values plus the defaults and the effective prompt. */
export interface SubAgentView {
  id: string
  name: string
  enabled: boolean
  scenario: string
  /** The built-in desktop agent: always present, no delete, scenario locked. */
  builtin: boolean
  prompt: string | null
  defaultPrompt: string
  effectivePrompt: string
  promptTokens: number
  maxRounds: number
  model: SubAgentModelView | null
}

/** One thing the main agent may hand to a sub-agent. The list is closed: desktop and file-read. */
export interface AgentScenarioView {
  key: string
  name: string
  /** Which requests belong here, written for the main agent. */
  when: string
  ownerId: string | null
  capabilityEnabled: boolean
  available: boolean
}

export interface AgentLimits {
  maxRounds: number
  defaultMaxRounds: number
  /** The most tokens one prompt may use. */
  promptTokens: number
}

/** Everything the prompts page needs in one read. */
export interface AgentsView {
  main: MainAgentView
  subAgents: SubAgentView[]
  scenarios: AgentScenarioView[]
  /** Human-readable warnings: configured but will not work. */
  problems: string[]
  limits: AgentLimits
  files: FilesSettings
  desktopEnabled: boolean
  webEnabled: boolean
}

export interface AgentsSaved {
  agents: AgentsView
  consumer: ConsumerState
}

/** One delegated job, as the status bar and the jobs list report it. */
export interface AgentJobView {
  id: string
  agentId: string
  agentName: string
  scenario: string
  goal: string
  /** 'running' | 'completed' | 'cannot_execute' — running is the only one that is not final. */
  status: string
  round: number
  maxRounds: number
  startedAt: string
  finishedAt: string | null
  category: string | null
  summary: string | null
  error: string | null
  rounds: string[]
}

export interface RuntimeSettings {
  input: InputSettings
  output: OutputSettings
  asr: AsrSettings
  tts: TtsSettings
  local: LocalSettings
  model: ModelSettings
  desktop: DesktopSettings
  mcp: McpSettings
  skills: SkillSettings
  /** Carried through by the settings page untouched: the prompts page owns it. */
  agents: AgentsSettings
  files: FilesSettings
  web: WebSettings
}

export interface SettingsView {
  input: InputSettings
  output: OutputSettings
  asr: AsrSettings
  tts: TtsSettings
  local: LocalSettings
  model: ModelSettingsView
  desktop: DesktopSettings
  mcp: McpSettings
  skills: SkillSettings
  /** The stored agents section, with sub-agent API keys replaced by `apiKeySet`. */
  agents: AgentsSettingsView
  files: FilesSettings
  web: WebSettings
}

/**
 * The newest published release. `error` is set when the check could not be made — an answer the page shows
 * beside the version instead of a rejected promise, because "GitHub is unreachable" is not a broken form.
 */
export interface UpdateCheck {
  current: string
  latest: string | null
  available: boolean
  installerName: string | null
  sizeBytes: number | null
  error: string | null
}

/** One installer download. `path` is set only when `phase` is `ready`; the shell then runs that file. */
export type UpdatePhase = 'idle' | 'downloading' | 'verifying' | 'ready' | 'failed' | 'cancelled'

export interface UpdateDownload {
  phase: UpdatePhase
  bytesDone: number
  bytesTotal: number | null
  path: string | null
  error: string | null
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
  /**
   * 'foreground' | 'auto' | 'background' — how far an action may go to reach its target. Optional because a
   * server older than the setting does not send it; the page then reads it as 'foreground', which is what
   * that server does.
   */
  inputMode?: string
  allowedWindowTitles: string[]
}

/** The input modes, in the order the UI offers them. */
export const INPUT_MODES = [
  { value: 'foreground', label: tr('可以占用屏幕'), hint: tr('需要时把窗口带到前台、移动鼠标（默认）') },
  { value: 'auto', label: tr('尽量后台'), hint: tr('能在后台完成就不打扰你，做不到再占用屏幕') },
  { value: 'background', label: tr('只在后台'), hint: tr('绝不占用屏幕；做不到的动作会被拒绝并说明原因') },
] as const

/** The three approval gears, in the order the UI offers them. */
export const APPROVAL_GEARS = [
  { value: 'none', label: tr('不审批'), hint: tr('模型直接动手，不留问') },
  { value: 'dangerous', label: tr('危险指令审批'), hint: tr('执行命令 / 启动程序 / 写剪贴板 / 删除定时任务要批准') },
  { value: 'all', label: tr('全部审批'), hint: tr('每个动作都要批准（看屏幕不算）：点击、创建定时任务、打开没见过的网址') },
] as const

export interface DesktopState {
  enabled: boolean
  leaseTtlMs: number
  approval: string
  allowedWindowTitles: string[]
  /** Continuing approval grants this many further steps on an approved window. Server-owned number. */
  stepsPerGrant?: number
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
  /** Who is asking, when it is not the main agent: e.g. 子 agent「桌面操控」：打开网易云放歌. */
  origin?: string | null
}

export interface ApprovalQueue {
  timeoutSeconds: number
  pending: PendingApproval[]
}

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

/** One settings-page connectivity probe: smallest real round-trip that proves the address speaks. */
export interface VoiceTestResult {
  ok: boolean
  detail: string
  latencyMs: number
  /** ASR test: the engine's transcript of a synthesized TTS sample. */
  transcribed?: string
  /** TTS test: synthesized 16-bit mono WAV, base64; the UI converts it to a Blob for playback. */
  audioBase64?: string
  seconds?: number
}

/** What a delivery lands as: a queue item, not an executed turn. */
export interface SubmitResult {
  itemId: string
  seq: number
  status: string
  deduplicated: boolean
}

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

/**
 * The input half also reports how loud the microphone is. Without it a dead device, a quiet room and speech
 * under the gate's threshold all render as the same "运行中".
 */
export interface VoiceInputState extends VoiceHalfState {
  rms: number
  ambientRms: number
  thresholdMisconfigured: boolean
  awake: boolean
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
  input: VoiceInputState
  output: VoiceHalfState
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
    /** Every rule a rejected save broke, when the endpoint reports them one by one. */
    readonly errors: readonly string[] = [],
  ) {
    super(`${status}: ${detail}`)
    this.name = 'ApiError'
  }
}

/** How a timed task decides when to fire. */
export type ScheduleKind = 'cron' | 'interval' | 'once'

/** once: deliver the one overdue run; skip: do not run late, count it; all: run every missed one (the newest few). */
export type ScheduleCatchUp = 'once' | 'skip' | 'all'

/** A timed task as the server keeps it. `inactive` is both "paused" and "a one-off that already ran". */
export interface ScheduleTaskView {
  id: string
  kind: ScheduleKind | 'unknown'
  /** A short machine-ish description of the rule, for a row that has no better words. */
  describe: string
  cron: string | null
  intervalMinutes: number | null
  at: string | null
  /** What is sent to the assistant when the task fires. */
  payload: string
  status: 'active' | 'inactive' | 'failed'
  /** Null when there is no upcoming run (paused, or a one-off that already ran). */
  nextAt: string | null
  lastAt: string | null
  attempt: number
  lastError: string | null
  /** What to do with occurrences missed while the app was not running. */
  catchUp: ScheduleCatchUp
  /** How many occurrences were skipped as missed under the `skip` policy. */
  missed: number
  lastMissedAt: string | null
  /** When the assistant finished the turn this task last started, and how it ended. Null before the first. */
  lastRunAt: string | null
  lastRunOk: boolean | null
  lastRunError: string | null
}

/** What creating a task takes: the rule's field depends on `kind`, the others are ignored. */
export interface ScheduleInput {
  kind: ScheduleKind
  cron?: string
  intervalMinutes?: number
  /** ISO 8601 with an offset; a one-off in the past is refused. */
  at?: string
  payload: string
  catchUp?: ScheduleCatchUp
}

export interface ApiOptions {
  port: number
  token: string
  /** Test seam: lets a test observe requests without a real socket. */
  fetchImpl?: typeof fetch
}

/**
 * Exactly the surface the views use. Views depend on this, so a test stub is checked by the compiler against
 * the same shape the real client exposes — a stub cannot silently drift from the contract.
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
 * A header rather than the body, so a <code>File</code> can be posted straight through without being read into
 * memory first.
 * </para>
 */
const FILE_NAME_HEADER = 'X-File-Name'

export interface PartnerApiSurface {
  session(from?: number, take?: number, window?: SessionWindow): Promise<SessionView>
  editSession(seq: number, body: string, actor?: string): Promise<void>
  /** Deletes the latest conversation as a whole; `seq` may be any row inside it. */
  deleteLatest(seq: number): Promise<DeleteResult>
  /** Hides `seq`'s conversation and everything after it. Reversible until the next message is sent. */
  withdrawFrom(seq: number): Promise<WithdrawResult>
  /** Brings back everything a pending withdrawal hid. */
  cancelWithdrawal(): Promise<void>
  sessionRevisions(seq: number): Promise<SessionRevision[]>
  queue(): Promise<QueueView>
  cancel(itemId: string): Promise<boolean>
  submitInput(
    payload: string,
    source: string,
    options?: { idempotencyKey?: string; media?: string[]; reasoningEffort?: string | null },
  ): Promise<SubmitResult>
  uploadMedia(file: Blob, contentType?: string, fileName?: string): Promise<UploadedMedia>
  /** Stores a voice-cloning reference clip in the workspace and returns the path the speech engine can open. */
  uploadVoiceReference(file: Blob, fileName: string): Promise<{ path: string; fileName: string; bytes: number }>
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
  /** Writes a new skill. Rejects with the reason (a bad id, a clash with an existing one). */
  createSkill(skill: NewSkill): Promise<{ skillId: string; name: string }>
  /** Adds a skill from a `.md` (one SKILL.md) or a `.zip` (a skill folder). Rejects with the reason. */
  uploadSkill(file: Blob, fileName: string): Promise<{ skillId: string; name: string }>
  /** Removes a skill folder. False when it was already gone. */
  deleteSkill(skillId: string): Promise<boolean>
  consumer(): Promise<ConsumerState>
  voice(): Promise<VoiceState>
  devices(): Promise<DeviceList>
  listModels(baseUrl?: string, protocol?: string): Promise<ModelProbe>
  memory(tier?: string, scope?: string, take?: number): Promise<MemoryItem[]>
  recall(query: string, limit?: number): Promise<RecallHit[]>
  editMemory(recordId: string, body: string, actor?: string): Promise<void>
  deleteMemory(recordId: string): Promise<void>
  memoryRevisions(recordId: string): Promise<MemoryRevision[]>
  settings(): Promise<SettingsView>
  /** The agents section as the prompts page reads it: stored values, defaults, effective prompts, problems. */
  agents(): Promise<AgentsView>
  /** Current state of every search engine (disabled ones and circuit-broken ones are marked). */
  web(): Promise<WebView>
  /** Writes the stored shape. A new sub-agent goes in with `id: ""`; the server names it. */
  saveAgents(agents: AgentsSettings): Promise<AgentsSaved>
  /** Every delegated job, newest first. */
  agentJobs(): Promise<AgentJobView[]>
  /** Stops a running job. False when it already finished — nothing to cancel, not an error. */
  cancelAgentJob(jobId: string): Promise<boolean>
  /** Every timed task, soonest first. */
  schedules(): Promise<ScheduleTaskView[]>
  /** Rejects with an `ApiError` carrying the server's reason when the rule or the message is not acceptable. */
  createSchedule(input: ScheduleInput): Promise<ScheduleTaskView>
  pauseSchedule(id: string): Promise<ScheduleTaskView>
  resumeSchedule(id: string): Promise<ScheduleTaskView>
  /** False when the task was already gone — nothing to delete, not an error. */
  deleteSchedule(id: string): Promise<boolean>
  testVoice(kind: 'asr' | 'tts' | 'kws' | 'model'): Promise<VoiceTestResult>
  /** Re-reads the disk each time, so this is also the "检测" button. */
  enginePackage(): Promise<EnginePackageStatus>
  /** Rejects with an `ApiError` carrying the reason when nothing can be downloaded or a download is running. */
  downloadEnginePackage(): Promise<EnginePackageStatus>
  cancelEnginePackage(): Promise<EnginePackageStatus>
  /** Points at a folder that already holds the engines; null returns to <app>/engines. Rejects with the reason. */
  setEngineRoot(root: string | null): Promise<EnginePackageStatus>
  /** Compares the installed version with the newest release. Failures arrive in `error`, not as a rejection. */
  checkUpdate(current: string): Promise<UpdateCheck>
  /** Starts downloading the installer found by the last check; poll `updateDownload` for progress. */
  downloadUpdate(): Promise<UpdateDownload>
  updateDownload(): Promise<UpdateDownload>
  cancelUpdate(): Promise<UpdateDownload>
  approvals(): Promise<ApprovalQueue>
  answerApproval(approvalId: string, approved: boolean): Promise<void>
  desktop(): Promise<DesktopState>
  observeDesktop(): Promise<DesktopObservation>
  setApprovalGear(approval: string): Promise<{ approval: string; changed: boolean }>
  saveSettings(settings: RuntimeSettings): Promise<SettingsSaved>
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


  session(from = 1, take?: number, window?: SessionWindow): Promise<SessionView> {
    const query = new URLSearchParams({ from: String(from) })
    if (window !== undefined) {
      query.set('day', window.day)
      query.set('open', String(window.open))
      query.set('offsetMinutes', String(window.offsetMinutes))
    }
    // Left to the server when not given: a day's window and a plain read have different sensible sizes.
    if (take !== undefined) query.set('take', String(take))
    return this.request<SessionView>('GET', `/api/session?${query.toString()}`)
  }

  async editSession(seq: number, body: string, actor = 'human'): Promise<void> {
    await this.request('POST', `/api/session/${seq}/edit`, { body, actor })
  }

  /**
   * Deletes the whole latest conversation, physically. `seq` may be any row inside it; the server refuses
   * (409) an earlier conversation, a pending withdrawal, or a running turn.
   */
  deleteLatest(seq: number): Promise<DeleteResult> {
    return this.request<DeleteResult>('POST', `/api/session/${seq}/delete`)
  }

  /**
   * Hides `seq`'s conversation and everything after it. Nothing is deleted: the range comes back with
   * `cancelWithdrawal`, and the next message sent makes it final.
   */
  withdrawFrom(seq: number): Promise<WithdrawResult> {
    return this.request<WithdrawResult>('POST', `/api/session/${seq}/withdraw`)
  }

  /** Takes back the pending withdrawal. 404 when there is none — the server's message says why. */
  async cancelWithdrawal(): Promise<void> {
    await this.request('POST', '/api/session/withdrawal/cancel')
  }

  sessionRevisions(seq: number): Promise<SessionRevision[]> {
    return this.request<SessionRevision[]>('GET', `/api/session/${seq}/revisions`)
  }


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


  /**
   * Delivers to the queue. Nothing executes it here, so the answer says "queued", not "done".
   * <para>
   * The per-message parts are an object rather than more positional parameters so call sites stay readable.
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

  async uploadVoiceReference(file: Blob, fileName: string): Promise<{ path: string; fileName: string; bytes: number }> {
    const response = await this.fetchImpl(`${this.base}/api/voice/reference`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${this.token}`,
        'Content-Type': 'application/octet-stream',
        [FILE_NAME_HEADER]: encodeURIComponent(fileName),
      },
      body: file,
    })

    if (!response.ok) {
      const detail = await response.text().catch(() => '')
      let message = detail
      try {
        message = (JSON.parse(detail) as { error?: string }).error ?? detail
      } catch {
        // Not JSON: the raw text is the best there is.
      }
      throw new ApiError(response.status, message || t('上传失败（{status}）', { status: response.status }))
    }

    return (await response.json()) as { path: string; fileName: string; bytes: number }
  }

  createSkill(skill: NewSkill): Promise<{ skillId: string; name: string }> {
    return this.request('POST', '/api/skills', skill)
  }

  async uploadSkill(file: Blob, fileName: string): Promise<{ skillId: string; name: string }> {
    const response = await this.fetchImpl(`${this.base}/api/skills/upload`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${this.token}`,
        'Content-Type': 'application/octet-stream',
        [FILE_NAME_HEADER]: encodeURIComponent(fileName),
      },
      body: file,
    })

    await this.assertOk(response)
    return (await response.json()) as { skillId: string; name: string }
  }

  async deleteSkill(skillId: string): Promise<boolean> {
    const response = await this.send('DELETE', `/api/skills/${encodeURIComponent(skillId)}`)

    if (response.status === 404) {
      return false
    }

    await this.assertOk(response)
    return true
  }

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
      throw new ApiError(response.status, detail || t('上传失败（{status}）', { status: response.status }))
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
      throw new ApiError(response.status, t('读取附图失败（{status}）', { status: response.status }))
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
  listModels(baseUrl?: string, protocol?: string): Promise<ModelProbe> {
    return this.request<ModelProbe>('POST', '/api/models', { baseUrl: baseUrl ?? null, protocol: protocol ?? null })
  }


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


  settings(): Promise<SettingsView> {
    return this.request<SettingsView>('GET', '/api/settings')
  }

  saveSettings(settings: RuntimeSettings): Promise<SettingsSaved> {
    return this.request<SettingsSaved>('PUT', '/api/settings', settings)
  }


  web(): Promise<WebView> {
    return this.request<WebView>('GET', '/api/web')
  }


  agents(): Promise<AgentsView> {
    return this.request<AgentsView>('GET', '/api/agents')
  }

  saveAgents(agents: AgentsSettings): Promise<AgentsSaved> {
    return this.request<AgentsSaved>('PUT', '/api/agents', agents)
  }

  async agentJobs(): Promise<AgentJobView[]> {
    const answer = await this.request<{ jobs?: AgentJobView[] }>('GET', '/api/agent-jobs')
    // An older build answers `{}`; treating that as "no jobs" keeps the status bar quiet rather than crashing it.
    return Array.isArray(answer.jobs) ? answer.jobs : []
  }

  /** 404 means the job is already over: the same rule the queue's cancel follows. */
  async cancelAgentJob(jobId: string): Promise<boolean> {
    const response = await this.send('POST', `/api/agent-jobs/${encodeURIComponent(jobId)}/cancel`)

    if (response.status === 404) {
      return false
    }

    await this.assertOk(response)
    return true
  }


  async schedules(): Promise<ScheduleTaskView[]> {
    const answer = await this.request<{ tasks?: ScheduleTaskView[] }>('GET', '/api/schedules')
    // An older build answers `{}`; "no tasks" is the honest reading of that.
    return Array.isArray(answer.tasks) ? answer.tasks : []
  }

  createSchedule(input: ScheduleInput): Promise<ScheduleTaskView> {
    return this.request<ScheduleTaskView>('POST', '/api/schedules', input)
  }

  pauseSchedule(id: string): Promise<ScheduleTaskView> {
    return this.request<ScheduleTaskView>('POST', `/api/schedules/${encodeURIComponent(id)}/pause`)
  }

  resumeSchedule(id: string): Promise<ScheduleTaskView> {
    return this.request<ScheduleTaskView>('POST', `/api/schedules/${encodeURIComponent(id)}/resume`)
  }

  async deleteSchedule(id: string): Promise<boolean> {
    const response = await this.send('DELETE', `/api/schedules/${encodeURIComponent(id)}`)

    if (response.status === 404) {
      return false
    }

    await this.assertOk(response)
    return true
  }

  /** Settings-page connectivity probe against /api/voice/test/{kind}. */
  testVoice(kind: 'asr' | 'tts' | 'kws' | 'model'): Promise<VoiceTestResult> {
    return this.request('POST', `/api/voice/test/${kind}`)
  }

  enginePackage(): Promise<EnginePackageStatus> {
    return this.request('GET', '/api/local-engines/package')
  }

  downloadEnginePackage(): Promise<EnginePackageStatus> {
    return this.request('POST', '/api/local-engines/package/download')
  }

  cancelEnginePackage(): Promise<EnginePackageStatus> {
    return this.request('POST', '/api/local-engines/package/cancel')
  }

  setEngineRoot(root: string | null): Promise<EnginePackageStatus> {
    return this.request('PUT', '/api/local-engines/package/root', { root })
  }

  checkUpdate(current: string): Promise<UpdateCheck> {
    return this.request('GET', `/api/update/check?current=${encodeURIComponent(current)}`)
  }

  downloadUpdate(): Promise<UpdateDownload> {
    return this.request('POST', '/api/update/download')
  }

  updateDownload(): Promise<UpdateDownload> {
    return this.request('GET', '/api/update/download')
  }

  cancelUpdate(): Promise<UpdateDownload> {
    return this.request('POST', '/api/update/download/cancel')
  }

  /**
   * One readiness probe. The three answers mean different things to a caller that is still starting up:
   * 'ok' — the API is listening and the token checks out; 'unauthorized' — listening but the token is
   * rejected (retrying cannot help); 'unavailable' — the port is not answering yet (retry).
   */
  async health(): Promise<'ok' | 'unauthorized' | 'unavailable'> {
    let response: Response
    try {
      response = await this.send('GET', '/api/health')
    } catch {
      return 'unavailable'
    }

    if (response.ok) {
      return 'ok'
    }

    return response.status === 401 ? 'unauthorized' : 'unavailable'
  }


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

    // A save can be refused for several rules at once, and the prompts page shows every one of them. The body
    // is read once and then interpreted: JSON when the endpoint answered JSON, the raw text otherwise.
    const body = await response.text()
    const reported = readErrors(body)
    const detail = reported.error ?? (body === '' ? response.statusText : body)

    // 401 is worth naming: it means the token is wrong or stale, not that the request was malformed.
    throw new ApiError(response.status, detail, reported.errors)
  }
}

/** The two error shapes the API sends: a plain message, or a message plus every broken rule. */
function readErrors(body: string): { error: string | null; errors: string[] } {
  try {
    const parsed: unknown = JSON.parse(body)
    if (typeof parsed !== 'object' || parsed === null) {
      return { error: null, errors: [] }
    }

    const record = parsed as { error?: unknown; errors?: unknown }
    return {
      error: typeof record.error === 'string' ? record.error : null,
      errors: Array.isArray(record.errors) ? record.errors.filter((entry): entry is string => typeof entry === 'string') : [],
    }
  } catch {
    return { error: null, errors: [] }
  }
}
