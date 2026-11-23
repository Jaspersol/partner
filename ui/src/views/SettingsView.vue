<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import Icon from '../components/Icon.vue'
import Dialog from '../components/Dialog.vue'
import McpView from './McpView.vue'
import PromptsView from './PromptsView.vue'
import SkillsView from './SkillsView.vue'
import PetSettingsPanel from './PetSettingsPanel.vue'
import EngineSettingsPanel from './EngineSettingsPanel.vue'
import VoiceInputSettingsPanel from './VoiceInputSettingsPanel.vue'
import VoiceOutputSettingsPanel from './VoiceOutputSettingsPanel.vue'
import HintTip from '../components/HintTip.vue'
import GearTable from '../components/GearTable.vue'
import SaveBar from '../components/SaveBar.vue'
import Segmented from '../components/Segmented.vue'
import SettingRow from '../components/SettingRow.vue'
import TestButton from '../components/TestButton.vue'
import { ApiError, INPUT_MODES } from '../api'
import { version as appVersion } from '../../package.json'
import { LOCALES, LOCALE_NAMES, locale, setLocale, t, type Locale, type MessageKey, tr } from '../i18n'
import { normalizeGear } from '../gears'
import { isPageHidden } from '../pageVisibility'
import { readReducedTransparency, applyReducedTransparency, themeLabel, type Theme } from '../theme'
import {
  getAutostart,
  getPetEnabled,
  getTrayEnabled,
  hasShellPrefs,
  installUpdate as launchInstaller,
  petSupported,
  resetPetPosition,
  setAutostart,
  setPetEnabled,
  setTrayEnabled,
  watchPetShowing,
} from '../shellPrefs'
import { DEFAULT_DICTIONARY, matchEmotion, type EmotionMatch } from '../pet/emotion'
import { showToast } from '../toast'
import type { IconName } from '../icons'
import type {
  ConsumerState,
  DeviceList,
  EnginePackageStatus,
  PartnerApiSurface,
  PetCoreStatus,
  PetKeyword,
  PetModelInfo,
  PetModelProfile,
  PetSettings,
  RuntimeSettings,
  UpdateCheck,
  UpdateDownload,
  VoiceState,
  VoiceTestResult,
  WebEngineSettings,
  WebEngineView,
  WebView,
} from '../api'


/**
 * Fallback menu, used only until the server answers. The stored list is what this page edits, so a
 * hardcoded copy used as the *saved* value would quietly re-add gears the user removed.
 */
const DEFAULT_REASONING_GEARS = ['off', 'low', 'medium', 'high', 'xhigh', 'max']
/**
 * `page` picks what the view is for. Unset, it is the settings page with its category nav. `pet` is the desktop
 * pet's own page: the same form and Save bar, but only the pet category, with no nav.
 */
const props = defineProps<{ api: PartnerApiSurface; theme?: Theme | undefined; page?: 'pet' | undefined }>()
const emit = defineEmits<{ saved: []; changed: []; navigate: [key: string]; 'set-theme': [theme: Theme] }>()

// A server-generated guard includes even write-only and service settings omitted from the view.
const rebaseGuard = ref<string | undefined>()
/** The form is a projection of the stored settings; nothing here is authoritative until it is saved. */
const form = reactive<RuntimeSettings>({
  revision: 0,
  input: {
    enabled: false,
    deviceId: null,
    silenceThreshold: 0.003,
    utteranceGapMs: 1200,
    wakeTimeoutMs: 5000,
    sentenceGapMs: 450,
    windowSeconds: 8,
    asrStream: false,
    wakeTone: true,
    bargeIn: false,
  },
  output: {
    enabled: false,
    deviceId: null,
    voice: null,
    speed: 1,
    gapMs: 0,
    drainMs: 400,
    sampleRate: 24000,
    muteDuringTts: true,
    maxSplitChars: 40,
    minSentenceChars: 10,
  },
  asr: { baseUrl: 'http://127.0.0.1:45103', protocol: 'sensevoice', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
  tts: { baseUrl: 'http://127.0.0.1:45102', protocol: 'local', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, speakerRefText: null, timeoutSeconds: 600, chunkMs: 100 },
  local: {
    tts: { enabled: false, command: 'engines/tts/python/python.exe', arguments: ['tts-server.py', '--port', '{port}'], workingDirectory: 'engines/tts', port: 45102, readyTimeoutSeconds: 30, keywords: [] as string[] },
    kws: { enabled: false, command: 'engines/kws-asr/python/python.exe', arguments: ['asr-server.py', '--port', '{port}'], workingDirectory: 'engines/kws-asr', port: 45103, readyTimeoutSeconds: 30, keywords: [] as string[] },
  },
  model: { protocol: 'openai', baseUrl: null, model: null, timeoutSeconds: 120, contextTokens: 128000, maxTokens: 32000, supportsImages: false, reasoningEffort: 'medium', reasoningGears: [...DEFAULT_REASONING_GEARS], reasoningLabels: {} },
  desktop: { enabled: false, execEnabled: false, leaseTtlMs: 15000, approval: 'all', inputMode: 'foreground', allowedWindowTitles: [] as string[] },

  // Carried through untouched so the two capability pages' state survives a save made here. Not editable on
  // this page: a server row and a skill row need more room than a card in a settings form gives them.
  mcp: { enabled: false, servers: [], allowedTools: [] },
  skills: { disabled: [] },

  // Carried, not edited here: the memory page owns it. Sent back like every other section, because a save that
  // left it out would put the server default over whatever was stored.
  memory: { embeddings: false },

  // Also carried, not edited: the prompts page owns the agents section. Dropping it here would reset every
  // prompt and sub-agent the next time anyone saved a voice setting.
  agents: { main: { prompt: null }, subAgents: [] },
  files: { enabled: false, roots: [], writeEnabled: false, writeRoots: [] },

  // The one section here that defaults to ON, and the one that sends what you typed off this machine. The
  // default is deliberate (not an oversight): the search is useless if nobody ever finds the switch, and the
  // section says in as many words where the queries go.
  web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] as string[], engines: [] as WebEngineSettings[] },

  // The dictionary starts empty and is replaced by the server's copy: the built-in table lives in C# and in the
  // pet frontend, and a third copy here would be one more to keep in step.
  pet: petFallback(),
})

/** The key is write-only: it is never read back, so the field starts empty and tracks whether it was edited. */
const apiKeySet = ref(false)
const apiKeyDraft = ref('')
const apiKeyEdited = ref(false)

/**
 * The settings as last stored, for telling "changed" from "unchanged".
 *
 * <para>
 * A `ref`, not a plain variable, and that is the whole trick. It started as one and the dirty marker never
 * appeared: `dirty` short-circuits on \`stored === null\`, so on the first evaluation it never called
 * \`fingerprint()\` — recorded no reactive dependency at all — and so it could never be invalidated by an edit
 * afterwards. A plain variable that a computed depends on only through a branch it did not take is invisible to
 * reactivity.
 * </para>
 *
 * <para>
 * Kept as a snapshot rather than a dirty flag so it survives the two ways this form changes underneath itself:
 * \`apply()\` rewrites \`form\` after every save, and the API key is write-only and never comes back to be
 * compared.
 * </para>
 */
const stored = ref<string | null>(null)
const settingsBusy = ref(false)

function fingerprint(): string {
  return JSON.stringify({
    input: form.input,
    output: form.output,
    asr: form.asr,
    tts: form.tts,
    local: form.local,
    model: form.model,
    desktop: form.desktop,
    files: form.files,
    web: form.web,
    pet: form.pet,
    // The key is write-only: it is either the draft or nothing, and it is the one field a fingerprint would
    // otherwise get wrong in both directions.
    apiKey: apiKeyEdited.value ? apiKeyDraft.value : null,
  })
}

const dirty = computed(() => stored.value !== null && fingerprint() !== stored.value)

/** Titles are edited as one comma-separated line; empty entries are dropped rather than stored. */
function onTitlesInput(event: Event) {
  const raw = (event.target as HTMLInputElement).value
  form.desktop.allowedWindowTitles = raw
    .split(/[,，]/)
    .map((title) => title.trim())
    .filter((title) => title !== '')
}

/** Engine arguments are edited as one shell line; blank runs are dropped rather than stored. */
function onEngineArgumentsInput(engine: 'tts' | 'kws', event: Event) {
  const raw = (event.target as HTMLInputElement).value
  form.local[engine].arguments = raw
    .split(/\s+/)
    .filter((part) => part !== '')
}

/**
 * Wake words are edited as one comma-separated line; mixed Chinese and English are stored in order, and the
 * engine only uses words it can convert to pinyin. The draft keeps the raw string while editing: splitting
 * and rejoining would drop a trailing comma and make the second word impossible to type.
 */
const keywordDrafts = ref<Record<'tts' | 'kws', string>>({ tts: '', kws: '' })

function onEngineKeywordsInput(engine: 'tts' | 'kws', event: Event) {
  const raw = (event.target as HTMLInputElement).value
  keywordDrafts.value = { ...keywordDrafts.value, [engine]: raw }
  form.local[engine].keywords = parseKeywords(raw)
}

/** Comma-separated string to word list. Both comma forms separate; surrounding whitespace and empty entries are dropped. */
function parseKeywords(raw: string): string[] {
  return raw
    .split(/[,，]/)
    .map((word) => word.trim())
    .filter((word) => word !== '')
}

const status = ref('')
const error = ref('')
const consumer = ref<ConsumerState | null>(null)
const voice = ref<VoiceState | null>(null)
const devices = ref<DeviceList>({ inputs: [], outputs: [] })
const models = ref<string[]>([])
const modelProbeError = ref('')
const probing = ref(false)

const voiceTest = ref<Record<string, VoiceTestResult | null>>({})
const voiceTesting = ref<Record<string, boolean>>({})

/** TTS test audio (base64 WAV to blob URL), reclaimed when the result is replaced or the component unmounts. */
const testTtsUrl = computed(() => {
  const result = voiceTest.value.tts
  if (!result?.ok || !('audioBase64' in result) || !result.audioBase64) {
    return ''
  }
  const binary = atob(result.audioBase64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  const blob = new Blob([bytes], { type: 'audio/wav' })
  const url = URL.createObjectURL(blob)
  return url
})

/** One /api/voice/test/{kind} round-trip; result text lands beside the corresponding card's button. */
async function testConnection(kind: 'asr' | 'tts' | 'kws' | 'model') {
  voiceTesting.value = { ...voiceTesting.value, [kind]: true }
  // What the screen said when the button was pressed: the result below is a statement about this, and editing
  // the block afterwards makes it a statement about something else.
  testedWith.value = { ...testedWith.value, [kind]: blockPrint(kind) }
  try {
    const result = await props.api.testVoice(kind)
    voiceTest.value = { ...voiceTest.value, [kind]: result }
  } catch (failure) {
    voiceTest.value = {
      ...voiceTest.value,
      [kind]: { ok: false, detail: String(failure), latencyMs: 0 },
    }
  } finally {
    voiceTesting.value = { ...voiceTesting.value, [kind]: false }
  }
}

const inputOptions = computed(() => withStored(devices.value.inputs, form.input.deviceId))
const outputOptions = computed(() => withStored(devices.value.outputs, form.output.deviceId))

/**
 * The input level only means something while it is live: a number captured when the page opened says
 * nothing about whether the microphone is delivering audio *now*. Polled at a rate the eye can follow,
 * and only while this view is on screen — the level is not worth a request after the user leaves.
 */
let levelTimer: ReturnType<typeof setInterval> | null = null

async function pollLevel() {
  if (isPageHidden()) {
    return
  }

  if (voice.value !== null && !voice.value.input.running) {
    return
  }

  try {
    voice.value = await props.api.voice()
  } catch {
    // A failed poll must not replace what is on screen: the save path is the one that reports errors.
  }
}

onMounted(() => {
  levelTimer = setInterval(pollLevel, 500)
})

onUnmounted(() => {
  if (levelTimer !== null) {
    clearInterval(levelTimer)
    levelTimer = null
  }
})

/** Level-bar ceiling: RMS 0.2 is already near full-scale speech, so higher values add no information. */
const LEVEL_CEILING = 0.2

function levelWidth(value: number): string {
  const ratio = Math.min(1, Math.max(0, value / LEVEL_CEILING))
  return `${(ratio * 100).toFixed(1)}%`
}

const levelPercent = computed(() => levelWidth(voice.value?.input.rms ?? 0))
const thresholdPercent = computed(() => levelWidth(form.input.silenceThreshold ?? 0))

/** The wake gate applies only when the local engine is managed and its port is valid, matching the backend. */
const wakeGateOn = computed(() => form.local.kws.enabled === true && (form.local.kws.port ?? 0) > 0)


const modelConfigured = computed(() => (form.model.baseUrl ?? '') !== '' && (form.model.model ?? '') !== '')
const keyHint = computed(() =>
  apiKeySet.value
    ? t('已设置。留空表示不改，输入新内容会替换原来的。保存后不会再显示出来。')
    : t('还没设置。本地模型一般不需要。保存后不会再显示出来。'),
)

/** A stored value that no longer enumerates must still be selectable, or the setting would silently change. */
function withStored(available: string[], stored: string | null): string[] {
  return stored !== null && stored !== '' && !available.includes(stored) ? [stored, ...available] : available
}

async function probeModels() {
  probing.value = true
  modelProbeError.value = ''

  try {
    const probe = await props.api.listModels(form.model.baseUrl ?? undefined, form.model.protocol)
    models.value = probe.models
    modelProbeError.value = probe.error ?? ''
  } catch (failure) {
    models.value = []
    modelProbeError.value = String(failure)
  } finally {
    probing.value = false
  }
}

/** Factory defaults, relative to the application directory and matching LocalSettings on the backend. */
const LOCAL_ENGINE_DEFAULTS = {
  tts: { command: 'engines/tts/python/python.exe', arguments: ['tts-server.py', '--port', '{port}'], workingDirectory: 'engines/tts', port: 45102 },
  kws: { command: 'engines/kws-asr/python/python.exe', arguments: ['asr-server.py', '--port', '{port}'], workingDirectory: 'engines/kws-asr', port: 45103 },
} as const

function apply(settings: RuntimeSettings & { rebaseGuard?: string; model: { apiKeySet?: boolean } }) {
  closeWebEngine()
  form.revision = settings.revision
  rebaseGuard.value = settings.rebaseGuard
  Object.assign(form.input, settings.input)
  Object.assign(form.output, settings.output)
  Object.assign(form.asr, settings.asr)
  Object.assign(form.tts, settings.tts)
  // These two carry lists; replaced, not merged, so a row removed on the server stays removed.
  Object.assign(form.local, {
    tts: { ...settings.local?.tts, arguments: [...(settings.local?.tts?.arguments ?? [])], keywords: [...(settings.local?.tts?.keywords ?? [])] },
    kws: { ...settings.local?.kws, arguments: [...(settings.local?.kws?.arguments ?? [])], keywords: [...(settings.local?.kws?.keywords ?? [])] },
  })
  // Stored settings may leave these empty (older client or server); fall back to the factory defaults.
  for (const engine of ['tts', 'kws'] as const) {
    const target = form.local[engine]
    const defaults = LOCAL_ENGINE_DEFAULTS[engine]
    if ((target.command ?? '').trim() === '') target.command = defaults.command
    if (target.arguments.length === 0) target.arguments = [...defaults.arguments]
    if ((target.workingDirectory ?? '').trim() === '') target.workingDirectory = defaults.workingDirectory
    if (!(target.port > 0)) target.port = defaults.port
  }
  // TTS and ASR clients default to the local engines; fill in values stored empty by older versions.
  if ((form.asr.baseUrl ?? '').trim() === '') {
    form.asr.baseUrl = `http://127.0.0.1:${form.local.kws.port}`
    form.asr.protocol = 'sensevoice'
  }
  const localTtsUrl = `http://127.0.0.1:${form.local.tts.port}`
  if ((form.tts.baseUrl ?? '').trim() === '') {
    form.tts.baseUrl = localTtsUrl
    form.tts.protocol = 'local'
  } else if ((form.tts.baseUrl ?? '').replace(/\/+$/, '') === localTtsUrl && form.tts.protocol === 'native') {
    // The local tts-server only serves /audio/speech; a stored native protocol would get a 404.
    form.tts.protocol = 'local'
  }
  keywordDrafts.value = { tts: form.local.tts.keywords.join(','), kws: form.local.kws.keywords.join(',') }
  Object.assign(form.desktop, {
    enabled: settings.desktop?.enabled ?? false,
    execEnabled: settings.desktop?.execEnabled ?? false,
    leaseTtlMs: settings.desktop?.leaseTtlMs ?? 15000,
    approval: settings.desktop?.approval ?? 'all',
    inputMode: settings.desktop?.inputMode ?? 'foreground',
    allowedWindowTitles: settings.desktop?.allowedWindowTitles ?? [],
  })
  Object.assign(form.model, {
    protocol: settings.model.protocol ?? 'openai',
    baseUrl: settings.model.baseUrl,
    model: settings.model.model,
    timeoutSeconds: settings.model.timeoutSeconds,
    contextTokens: settings.model.contextTokens,
    supportsImages: settings.model.supportsImages ?? false,
    maxTokens: settings.model.maxTokens,
    reasoningEffort: settings.model.reasoningEffort ?? 'medium',
    // A response from an older build carries no list; falling back keeps the editor usable rather than
    // leaving it with an empty menu that cannot be saved.
    reasoningGears: settings.model.reasoningGears ?? [...DEFAULT_REASONING_GEARS],
    reasoningLabels: { ...(settings.model.reasoningLabels ?? {}) },
  })

  applyCarried(settings)
  form.files = { enabled: settings.files?.enabled ?? false, roots: [...(settings.files?.roots ?? [])], writeEnabled: settings.files?.writeEnabled ?? false, writeRoots: [...(settings.files?.writeRoots ?? [])] }

  // A response from an older build carries no `web` at all; the fallback is the same default the section
  // starts from, so saving from a stale page writes back what the switch is showing rather than turning the
  // capability off behind the user's back.
  legacyWebEngines.value = settings.web?.engines == null
  form.web = {
    enabled: settings.web?.enabled ?? true,
    timeBudgetSeconds: settings.web?.timeBudgetSeconds ?? 12,
    maxResults: settings.web?.maxResults ?? 8,
    proxy: settings.web?.proxy ?? null,
    disabledEngines: [...(settings.web?.disabledEngines ?? [])],
    engines: (settings.web?.engines ?? []).map((e) => ({ ...e })),
  }
  if (form.web.engines && form.web.engines.length > 0) {
    form.web.disabledEngines = form.web.engines.filter((e) => !e.enabled).map((e) => e.id)
  }

  form.pet = normalizePet(settings.pet)
  // Inside the shell the window is the authority on whether the pet is showing: it decides at start-up from its
  // own preference, and the settings file follows it rather than the other way round.
  if (shellPrefs) {
    form.pet.enabled = petSpawned.value
  }

  apiKeySet.value = 'apiKeySet' in settings.model ? Boolean(settings.model.apiKeySet) : false
  apiKeyDraft.value = ''
  apiKeyEdited.value = false
  // Everything above is now what the server holds, so this is the point the form stops being dirty — not
  // `refresh()`, which can fail and leave the form showing edits that were never stored.
  stored.value = fingerprint()
}

async function refresh() {
  if (settingsBusy.value || cloneBusy.value) return
  settingsBusy.value = true
  error.value = ''
  try {
    apply(await props.api.settings())
    voice.value = await props.api.voice()
    devices.value = await props.api.devices()
    await refreshWeb()
    await refreshEnginePackage()
    await refreshPetCore()
    await refreshPetModels()
  } catch (failure) {
    error.value = String(failure)
  } finally {
    settingsBusy.value = false
  }
}

/**
 * The engine list, as the server last reported it.
 *
 * <para>
 * Read-only status, not part of the form: which engine is cooling off is the breaker's business, not
 * something a person edits here — the one thing editable about an engine is whether they have switched it
 * off, and that is a settings field (`form.web.disabledEngines`) rather than something derived from this.
 * </para>
 */
const webView = ref<WebView | null>(null)
const legacyWebEngines = ref(false)

async function refreshWeb() {
  try {
    webView.value = await props.api.web()
    if (legacyWebEngines.value) {
      form.web.engines = webView.value.engines.map(e => ({ id: e.id, name: e.name, enabled: !form.web.disabledEngines.includes(e.id), presetId: e.id }))
      legacyWebEngines.value = false
      stored.value = fingerprint()
    }
  } catch {
    // Its own try, not the caller's: a status list that cannot be read must not take down the form above it.
    webView.value = null
  }
}

// Engines (runtime plus models, about 3 GB) are not in the installer; they live under the application's
// engines/ directory and are downloaded here. Their state comes from disk and is not part of the form:
// detect, download and choose location take effect immediately, without Save.

const enginePackage = ref<EnginePackageStatus | null>(null)
const enginePackageNote = ref('')
const engineManualOpen = ref(false)
const engineManualRoot = ref('')
let enginePackageTimer: ReturnType<typeof setInterval> | null = null


const downloadRunning = computed(() => {
  const phase = enginePackage.value?.download.phase
  return phase === 'downloading' || phase === 'extracting'
})

const allEnginesInstalled = computed(() => enginePackage.value?.tts.installed === true && enginePackage.value?.kws.installed === true)

/**
 * Whether the command path was configured by the user. Empty or the engine package's standard location counts
 * as unconfigured, so detection can replace it with the interpreter it actually finds.
 */
function isCustomCommand(command: string | null | undefined): boolean {
  const value = (command ?? '').trim()
  return value !== '' && !/^engines\/(tts|kws-asr)\/.*python\.exe$/.test(value)
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(0)} MB`
  return `${Math.max(0, Math.round(bytes / 1024))} KB`
}

const downloadText = computed(() => {
  const download = enginePackage.value?.download
  if (!download) return ''
  if (download.phase === 'extracting') return t('{v} 解压中…', { v: download.detail ?? '' })
  if (download.phase !== 'downloading') return ''
  const total = download.bytesTotal
  const sizes = total ? t('{formatBytes} / {formatBytes2}（{v}%）', { formatBytes: formatBytes(download.bytesDone), formatBytes2: formatBytes(total), v: Math.floor((download.bytesDone / total) * 100) }) : formatBytes(download.bytesDone)
  return `${download.detail ?? t('下载中')} ${sizes}`
})

/**
 * Fills command, arguments, working directory and port for a detected engine. Only empty fields are filled;
 * `force` is for a location change that makes old defaults stale. The form then shows unsaved changes.
 */
function fillEngineDefaults(force = false) {
  const status = enginePackage.value
  if (!status) return

  for (const engine of ['tts', 'kws'] as const) {
    if (!status[engine].installed) continue
    const target = form.local[engine]
    if (!force && isCustomCommand(target.command)) continue

    const defaults = engine === 'tts' ? status.ttsDefaults : status.kwsDefaults
    target.command = defaults.command
    target.arguments = [...defaults.arguments]
    target.workingDirectory = defaults.workingDirectory
    target.port = defaults.port
  }
}

/** Polls progress once a second while downloading; stops when the download finishes, fails or is cancelled. */
function syncEnginePolling() {
  if (downloadRunning.value && enginePackageTimer === null) {
    enginePackageTimer = setInterval(() => void pollEnginePackage(), 1000)
  } else if (!downloadRunning.value && enginePackageTimer !== null) {
    clearInterval(enginePackageTimer)
    enginePackageTimer = null
  }
}

async function pollEnginePackage() {
  if (isPageHidden()) {
    return
  }

  try {
    const wasRunning = downloadRunning.value
    enginePackage.value = await props.api.enginePackage()
    syncEnginePolling()

    if (wasRunning && !downloadRunning.value) {
      const download = enginePackage.value.download
      enginePackageNote.value =
        download.phase === 'done' ? t('下载完成，已填入默认设置，保存后生效。')
          : download.phase === 'failed' ? t('下载失败：{error}', { error: download.error ?? t('未知原因') })
            : t('已取消，再点下载会从断点继续。')
      if (download.phase === 'done') fillEngineDefaults()
    }
  } catch {
    // A failed poll keeps the state already on screen; the next tick asks again.
  }
}

function describeFailure(failure: unknown): string {
  return failure instanceof Error ? failure.message : String(failure)
}

/** Detect: reread the disk, fill defaults when found, otherwise report what is missing. */
async function detectEngines() {
  enginePackageNote.value = ''
  try {
    enginePackage.value = await props.api.enginePackage()
    syncEnginePolling()
    fillEngineDefaults()

    const status = enginePackage.value
    if (status.tts.installed || status.kws.installed) {
      enginePackageNote.value = t('已检测到：{engines}。', { engines: [status.tts.installed ? t('朗读引擎') : '', status.kws.installed ? t('唤醒与识别引擎') : ''].filter(Boolean).join(t('、')) })
    } else {
      enginePackageNote.value = t('没有在 {root} 找到引擎。可以下载，或手动指定已有的位置。', { root: status.root })
    }
  } catch (failure) {
    enginePackageNote.value = t('检测失败：{describeFailure}', { describeFailure: describeFailure(failure) })
  }
}

async function startEngineDownload() {
  enginePackageNote.value = ''
  try {
    enginePackage.value = await props.api.downloadEnginePackage()
    syncEnginePolling()
  } catch (failure) {
    enginePackageNote.value = describeFailure(failure)
  }
}

async function cancelEngineDownload() {
  try {
    enginePackage.value = await props.api.cancelEnginePackage()
  } catch (failure) {
    enginePackageNote.value = describeFailure(failure)
  }
}

/** Manual location: the server records it only if engines exist there. Empty returns to the default location. */
async function applyEngineRoot() {
  enginePackageNote.value = ''
  try {
    enginePackage.value = await props.api.setEngineRoot(engineManualRoot.value.trim() === '' ? null : engineManualRoot.value.trim())
    fillEngineDefaults(true)
    engineManualOpen.value = false
    enginePackageNote.value = enginePackage.value.customRoot
      ? t('已指向 {root}，默认设置已填入，保存后生效。', { root: enginePackage.value.root })
      : t('已回到应用目录下的默认位置。')
  } catch (failure) {
    enginePackageNote.value = describeFailure(failure)
  }
}

async function refreshEnginePackage() {
  try {
    enginePackage.value = await props.api.enginePackage()
    syncEnginePolling()
  } catch {
    // When unreadable (for example an older server), treat the module as absent instead of blanking the page.
    enginePackage.value = null
  }
}

/** Shows further settings only for an installed engine or a previously configured command path. */
function showEngine(engine: 'tts' | 'kws'): boolean {
  const status = enginePackage.value
  if (!status) return true
  return status[engine].installed || isCustomCommand(form.local[engine].command)
}

onUnmounted(() => {
  if (enginePackageTimer !== null) {
    clearInterval(enginePackageTimer)
    enginePackageTimer = null
  }
  stopUpdatePoll()
})

/** Toggles an engine. Stores which ones are off, so a newly added engine defaults to on. */
function toggleEngine(id: string) {
  const row = form.web.engines?.find((e) => e.id === id)
  if (row) {
    row.enabled = !row.enabled
    if (engineDraft.value?.id === id) engineDraft.value.enabled = row.enabled
  }
  const off = form.web.disabledEngines
  form.web.disabledEngines = off.includes(id) ? off.filter((each) => each !== id) : [...off, id]
}

function addWebEngine() {
  const id = `custom-${Math.random().toString(36).slice(2, 10)}`
  engineDraft.value = {
    id,
    name: '',
    enabled: true,
  }
}

const engineDraft = ref<WebEngineSettings | null>(null)
const isEditingEngine = computed(() => form.web.engines?.some((engine) => engine.id === engineDraft.value?.id) ?? false)

function editWebEngine(id: string) {
  const row = form.web.engines?.find((engine) => engine.id === id)
  if (row) engineDraft.value = { ...row }
}

function closeWebEngine() {
  engineDraft.value = null
}

function confirmWebEngine() {
  const draft = engineDraft.value
  if (!draft || draft.name.trim() === '') return
  const rows = form.web.engines ?? []
  const index = rows.findIndex((engine) => engine.id === draft.id)
  if (index < 0) rows.push({ ...draft })
  else rows[index] = { ...draft }
  form.web.engines = rows
  closeWebEngine()
}
function removeWebEngine(id: string) {
  if (!window.confirm(t('删除后无法恢复。'))) return
  form.web.engines = (form.web.engines ?? []).filter((e) => e.id !== id)
  form.web.disabledEngines = form.web.disabledEngines.filter((e) => e !== id)
  if (engineDraft.value?.id === id) closeWebEngine()
}
function restoreWebDefaults() {
  if (!window.confirm(t('恢复默认搜索引擎？'))) return
  const defaults = webView.value?.defaults ?? []
  if (defaults.length) {
    form.web.engines = defaults.map((e) => ({ ...e, enabled: true }))
    form.web.disabledEngines = []
    closeWebEngine()
  }
}

/** Human labels for content types an engine indexes; unknown values are shown verbatim. */
const KIND_LABELS: Record<string, string> = {
  general: tr('网页'),
  news: tr('新闻'),
  academic: tr('论文'),
  code: tr('代码'),
  encyclopedia: tr('百科'),
  weather: tr('天气'),
  qa: tr('问答'),
  none: tr('不限'),
}

function kindLabel(kind: string): string {
  const label = KIND_LABELS[kind]
  return label === undefined ? kind : t(label)
}


function webEngineRows(): Array<WebEngineSettings & Pick<WebEngineView, 'kinds' | 'singleSite'>> {
  return (form.web.engines ?? []).map((engine) => {
    const status = webView.value?.engines.find((row) => row.id === engine.id)
    return {
      ...engine,
      kinds: status?.kinds ?? [],
      singleSite: status?.singleSite ?? false,
    }
  })
}

const cloneBusy = ref(false)
const cloneError = ref('')
const cloneName = computed(() => (form.tts.speakerRefPath ?? '').split(/[\/]/).pop() ?? '')

/** Uploads the chosen file and stores its on-disk path: the browser cannot supply a path and the engine needs one. */
async function onCloneFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || settingsBusy.value || cloneBusy.value) return
  cloneBusy.value = true
  cloneError.value = ''
  try {
    const stored = await props.api.uploadVoiceReference(file, file.name)
    form.tts.speakerRefPath = stored.path
  } catch (failure) {
    cloneError.value = failure instanceof Error ? failure.message : String(failure)
  } finally {
    cloneBusy.value = false
  }
}

function blankToNull(value: string | null | undefined): string | null {
  return value === null || value === undefined || value.trim() === '' ? null : value.trim()
}

async function persist() {
  if (settingsBusy.value || cloneBusy.value) return
  settingsBusy.value = true
  error.value = ''
  status.value = ''

  const payload: RuntimeSettings = {
    revision: form.revision,
    desktop: { ...form.desktop },
    input: { ...form.input },
    output: { ...form.output },
    asr: { ...form.asr, baseUrl: blankToNull(form.asr.baseUrl) },
    // A blank field means unset, not an empty string; clearing speed restores 1 so a numeric field does not fail.
    tts: {
      ...form.tts,
      baseUrl: blankToNull(form.tts.baseUrl),
      model: blankToNull(form.tts.model),
      voice: blankToNull(form.tts.voice),
      speakerRefPath: blankToNull(form.tts.speakerRefPath),
      speakerRefText: blankToNull(form.tts.speakerRefText),
      speed: typeof form.tts.speed === 'number' && Number.isFinite(form.tts.speed) ? form.tts.speed : 1,
    },
    local: {
      tts: { ...form.local.tts, arguments: [...form.local.tts.arguments], keywords: [...form.local.tts.keywords] },
      kws: { ...form.local.kws, arguments: [...form.local.kws.arguments], keywords: [...form.local.kws.keywords] },
    },
    // Carried, not edited: these two have pages of their own. Dropping them here would silently undo whatever
    // was decided there the next time anybody saved a voice setting.
    mcp: form.mcp,
    skills: form.skills,
    agents: form.agents,
    // A row left blank is not a directory. Sending it would have the server reject the whole save over an
    // empty string the person never meant to add.
    files: { enabled: form.files.enabled, roots: form.files.roots.map((root) => root.trim()).filter((root) => root !== ''), writeEnabled: form.files.writeEnabled, writeRoots: form.files.writeRoots.map((root) => root.trim()).filter((root) => root !== '') },
    // An empty string means unset, not a proxy named ""; otherwise this field blocks the whole save.
    web: {
      enabled: form.web.enabled,
      timeBudgetSeconds: form.web.timeBudgetSeconds,
      maxResults: form.web.maxResults,
      proxy: form.web.proxy === null || form.web.proxy.trim() === '' ? null : form.web.proxy.trim(),
      disabledEngines: [...form.web.disabledEngines],
      engines: (form.web.engines ?? []).map((e) => ({ ...e, name: e.name.trim(), urlTemplate: e.urlTemplate?.trim() || null, resultsPath: e.resultsPath?.trim() || null, titleField: e.titleField?.trim() || null, urlField: e.urlField?.trim() || null, snippetField: e.snippetField?.trim() || null })),
    },
    model: {
      protocol: form.model.protocol,
      baseUrl: form.model.baseUrl === '' ? null : form.model.baseUrl,
      model: form.model.model === '' ? null : form.model.model,
      timeoutSeconds: form.model.timeoutSeconds,
      contextTokens: form.model.contextTokens,
      supportsImages: form.model.supportsImages,
      maxTokens: form.model.maxTokens,
      reasoningEffort: form.model.reasoningEffort,
      // A row the user opened and left empty is not a gear. Sending it would put a blank entry in the
      // picker, which reads as a level that thinks very little — or as a silent no-op.
      reasoningGears: form.model.reasoningGears.filter((gear) => normalizeGear(gear) !== ''),
      reasoningLabels: form.model.reasoningLabels ?? {},
    },
    memory: { embeddings: form.memory.embeddings },
    pet: petPayload(),
  }

  // Only an edited key is sent: an untouched field must not clear what is stored.
  if (apiKeyEdited.value) {
    payload.model.apiKey = apiKeyDraft.value
  }

  // What the shell was told last, so the shell command runs only when the switch actually moved.
  const wasSpawned = petSpawned.value

  try {
    // The API is the source of truth: show what it stored, not what was typed.
    const saved = await props.api.saveSettings(payload)

    // Runs before `apply()`, which reads `petSpawned` to decide what the switch shows; syncing after it would
    // leave the form dirty again. Only after a successful save: a refused save must not leave the pet showing
    // while the settings say it is off.
    petShellError.value = ''
    petRetryTarget.value = null
    if (shellPrefs && payload.pet.enabled !== wasSpawned) {
      try {
        await setPetEnabled(payload.pet.enabled)
        petSpawned.value = payload.pet.enabled
      } catch (shellFailure) {
        petRetryTarget.value = payload.pet.enabled
        petShellError.value = t('settings.general.petFailed', { error: failureText(shellFailure) })
      }
    }

    apply(saved.settings as RuntimeSettings & { model: { apiKeySet: boolean } })
    consumer.value = saved.consumer
    voice.value = saved.voice
    // The disabled list is stored now, so the engine rows have to be re-read to show it as it was saved.
    await refreshWeb()

    const at = new Date().toLocaleTimeString(locale.value)
    status.value = petRetryTarget.value !== null ? '' : saved.compaction
      ? t('已保存 · {at} · {reason}', { at: at, reason: saved.compaction.reason })
      : t('已保存 · {at}', { at: at })

    // The composer's picker is fed from the shell, so it has to hear about the save — otherwise the gears
    // just edited here would not be the ones offered in the session view.
    emit('saved')
  } catch (failure) {
    error.value = failure instanceof ApiError && failure.status === 409
      ? t('设置已被其他页面修改。请先记录未保存的更改，再刷新并重新编辑。')
      : String(failure)
  } finally {
    settingsBusy.value = false
  }
}

function clearKey() {
  apiKeyDraft.value = ''
  apiKeyEdited.value = true
}

/**
 * The desktop switch and the built-in desktop sub-agent have to agree.
 *
 * <para>
 * The server refuses this pair and returns a 400, which the generic error line shows. But the settings page is
 * where the switch was flipped, so it is where the reason belongs: the prompts page holds the agent, and a link
 * there is the whole fix.
 * </para>
 */
const desktopAgentOff = computed(
  () => form.desktop.enabled && form.agents.subAgents.some((agent) => agent.id === 'desktop' && !agent.enabled),
)

/**
 * The categories, in the order a person needs them: the model first (nothing runs without one), then what it can
 * hear and say, then what it may touch, then the plumbing. They follow what a person wants to do rather than the
 * module that implements it.
 */
interface Section {
  key: string
  label: MessageKey
  icon: IconName
}

const SECTIONS: Section[] = [
  { key: 'general', label: 'settings.section.general', icon: 'settings' },
  { key: 'appearance', label: 'settings.section.appearance', icon: 'contrast' },
  { key: 'model', label: 'settings.section.model', icon: 'sparkle' },
  { key: 'input', label: 'settings.section.input', icon: 'mic' },
  { key: 'output', label: 'settings.section.output', icon: 'speaker' },
  { key: 'skills', label: 'settings.section.skills', icon: 'skill' },
  { key: 'mcp', label: 'settings.section.mcp', icon: 'mcp' },
  { key: 'agents', label: 'settings.section.agents', icon: 'prompt' },
  { key: 'desktop', label: 'settings.section.desktop', icon: 'crosshair' },
  { key: 'files', label: 'settings.section.files', icon: 'folder' },
  { key: 'web', label: 'settings.section.web', icon: 'search' },
  { key: 'engines', label: 'settings.section.engines', icon: 'terminal' },
]

const SECTION_KEY = 'partner.settings.section'
const ADVANCED_KEY = 'partner.settings.advanced'

function readStored<T>(key: string, fallback: T, accept: (value: unknown) => value is T): T {
  try {
    const raw = window.localStorage.getItem(key)
    if (raw === null) return fallback
    const parsed: unknown = JSON.parse(raw)
    return accept(parsed) ? parsed : fallback
  } catch {
    return fallback
  }
}

function writeStored(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // A sandbox without storage still works; it just forgets.
  }
}

const isPet = computed(() => props.page === 'pet')
const section = ref(props.page === 'pet' ? 'pet' : 'model')

/**
 * Skills, MCP and agent are whole pages with their own Save bar and their own settings round trip, so they are
 * mounted here one at a time instead of being fields of this form. While one is showing this page's own Save bar
 * is hidden: it would save a different thing.
 */
const embedded = computed(() => !isPet.value && (section.value === 'skills' || section.value === 'mcp' || section.value === 'agents'))
const child = ref<{ hasUnsavedChanges?: () => boolean } | null>(null)

/**
 * Which 「更多选项」 are open. Remembered, because the views are `v-if`: without it, opening the options you
 * actually use would have to be redone on every tab switch. Closed on a first visit — the point is that most
 * people do not need them. The model section has none: its three limits sit in the connection card.
 */
const advanced = ref<Record<string, boolean>>({})

/** A stored section that no longer exists falls back to the model page. */
function resolveSection(key: string): string {
  if (isPet.value) return 'pet'
  return SECTIONS.some((item) => item.key === key) ? key : 'model'
}

function choose(key: string): void {
  if (key === section.value) return
  // The embedded pages are `v-if`: leaving one destroys it and its edits.
  if (child.value?.hasUnsavedChanges?.() === true && !window.confirm(t('app.unsavedLeave'))) return
  closeWebEngine()
  section.value = key
  writeStored(SECTION_KEY, key)
}

/**
 * Mcp, skills and agents are written by the embedded pages, and this form carries them back on its own save.
 * Re-reading them after each of those saves keeps the carried copy from putting an old list over a new one. They
 * are not part of the fingerprint, so unsaved edits elsewhere on this page are left alone.
 */
function applyCarried(settings: RuntimeSettings) {
  // Replaced, not merged: the response is the whole truth, and holding on to what arrived earlier would
  // resurrect a server or a deny entry the person has since removed.
  form.mcp = { ...settings.mcp, servers: [...(settings.mcp?.servers ?? [])], allowedTools: [...(settings.mcp?.allowedTools ?? [])] }
  form.skills = { disabled: [...(settings.skills?.disabled ?? [])] }
  form.agents = {
    main: { prompt: settings.agents?.main?.prompt ?? null },
    subAgents: (settings.agents?.subAgents ?? []).map((agent) => ({ ...agent })),
  }
  form.memory = { embeddings: settings.memory?.embeddings ?? false }
}

async function onChildChanged(): Promise<void> {
  emit('changed')
  try {
    const latest = await props.api.settings()
    // Only embedded-page fields may have changed. Never accept a revision merely because the visible form matches:
    // the guard also covers secrets and services that the settings response deliberately does not expose.
    if (rebaseGuard.value && latest.rebaseGuard === rebaseGuard.value) {
      applyCarried(latest)
      form.revision = latest.revision
    }
  } catch {
    // The embedded page shows its own failure; the carried copy is re-read on the next refresh.
  }
}

/** The agent page's "go to settings" button points at the capability switches, which are the desktop category. */
function onChildNavigate(key: string): void {
  if (key === 'settings') choose('desktop')
  else emit('navigate', key)
}

function toggleAdvanced(key: string): void {
  advanced.value = { ...advanced.value, [key]: advanced.value[key] !== true }
  writeStored(ADVANCED_KEY, advanced.value)
}

/** A category is 「运行中」 when what it controls is actually on: the dot in the nav is read from the same state. */
const running = computed<Record<string, boolean>>(() => ({
  model: consumer.value?.running === true,
  input: voice.value?.input?.running === true,
  output: voice.value?.output?.running === true,
}))

// Launch-at-login and the tray belong to the desktop shell, so they apply at once and are not part of the saved form.
const shellPrefs = hasShellPrefs()
const autostart = ref(false)
const trayEnabled = ref(false)
const shellPrefError = ref('')
const petShellError = ref('')
const petResetError = ref('')
const petRetryTarget = ref<boolean | null>(null)

async function retryPetWindow(): Promise<void> {
  const target = petRetryTarget.value
  if (target === null || settingsBusy.value) return
  settingsBusy.value = true
  petShellError.value = ''
  try {
    await setPetEnabled(target)
    const previous = petSpawned.value
    petSpawned.value = target
    if (form.pet.enabled === previous) form.pet.enabled = target
    if (stored.value !== null) {
      const baseline = JSON.parse(stored.value)
      baseline.pet.enabled = target
      stored.value = JSON.stringify(baseline)
    }
    petRetryTarget.value = null
  } catch (failure) {
    petShellError.value = t('settings.general.petFailed', { error: failureText(failure) })
  } finally {
    settingsBusy.value = false
  }
}

async function onAutostart(value: boolean): Promise<void> {
  shellPrefError.value = ''
  try {
    await setAutostart(value)
    autostart.value = value
  } catch (error) {
    autostart.value = !value
    shellPrefError.value = t('settings.general.autostartFailed', { error: error instanceof Error ? error.message : String(error) })
  }
}

async function onTrayEnabled(value: boolean): Promise<void> {
  shellPrefError.value = ''
  try {
    await setTrayEnabled(value)
    trayEnabled.value = value
  } catch (error) {
    trayEnabled.value = !value
    shellPrefError.value = t('settings.general.trayFailed', { error: error instanceof Error ? error.message : String(error) })
  }
}

async function loadShellPrefs(): Promise<void> {
  if (!shellPrefs) return
  petAvailable.value = petSupported()

  try {
    autostart.value = await getAutostart()
    trayEnabled.value = await getTrayEnabled()
    if (petAvailable.value) petSpawned.value = await getPetEnabled()
  } catch {
    // Leave both off; toggling reports the real error.
  }
}

// ---- Desktop pet ---------------------------------------------------------------------------------------------
// The Core and the models are user-supplied and live under %LOCALAPPDATA%\Partner\pet; neither is in the
// repository or the installer. So this section has two halves: what is on disk (read and imported at once,
// not part of the form) and how the pet behaves (part of the form, saved with everything else).

/** Where the person gets the Core. The shell opens nothing outside itself, so the URL is also copyable. */
const PET_CORE_URL = 'https://www.live2d.com/en/sdk/download/web'
const PET_LICENSE_URLS = {
  open: 'https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html',
  proprietary: 'https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html',
} as const

/** The four emotions both the dictionary editor and the trial box work in. */
const PET_EMOTIONS = ['smile', 'angry', 'cry', 'shy'] as const
type PetEmotion = (typeof PET_EMOTIONS)[number]

/**
 * The abstract actions the runtime plays, in the order the mapping table offers them. A model that cannot
 * resolve one falls back to the procedural parameter tracks, so an unmapped row is not a broken row.
 */
const PET_ACTIONS = ['blink', 'stretch', 'greet', 'wave', 'headTurn', 'headShake', 'bodyTurn', 'bodyShake', 'nod', 'shake', 'tilt', 'pat', 'shrink', 'surprise', 'puff', 'sob', 'sleep', 'wake'] as const

/** The section's own default, used until the server answers and when it answers without a `pet` section. */
function petFallback(): PetSettings {
  return {
    enabled: false,
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
    emotions: { keywords: {}, negationPrefixes: ['不', '没', '别', '不太', '并不', 'not', 'no', 'never'], minWeight: 1 },
    profiles: {},
  }
}

/** Deep copy: the form is edited in place, and the response it was built from must not change underneath it. */
function copyProfile(profile: PetModelProfile): PetModelProfile {
  return {
    motions: Object.fromEntries(Object.entries(profile.motions ?? {}).map(([key, ref]) => [key, { ...ref }])),
    expressions: Object.fromEntries(Object.entries(profile.expressions ?? {}).map(([key, ref]) => [key, { ...ref }])),
    parameters: { ...(profile.parameters ?? {}) },
    amplitude: profile.amplitude ?? 1,
    procedural: Object.fromEntries(Object.entries(profile.procedural ?? {}).map(([action, config]) => [action, {
      ...config,
      extraTracks: (config.extraTracks ?? []).map((track) => ({ ...track, keys: track.keys.map((key) => ({ ...key })) })),
    }])),
    headBottom: profile.headBottom ?? 0.35,
    faceBottom: profile.faceBottom ?? 0.55,
    defaultExpression: profile.defaultExpression ?? null,
  }
}

/** An older server sends no `pet` at all; the fallback keeps the section saveable rather than blank. */
function normalizePet(pet: PetSettings | undefined): PetSettings {
  const fallback = petFallback()
  if (pet === undefined) return fallback

  return {
    enabled: pet.enabled ?? fallback.enabled,
    alwaysOnTop: pet.alwaysOnTop ?? fallback.alwaysOnTop,
    clickThroughTransparent: pet.clickThroughTransparent ?? fallback.clickThroughTransparent,
    motionSound: pet.motionSound ?? fallback.motionSound,
    scale: pet.scale ?? fallback.scale,
    activeModelId: pet.activeModelId ?? null,
    idleMinSeconds: pet.idleMinSeconds ?? fallback.idleMinSeconds,
    idleMaxSeconds: pet.idleMaxSeconds ?? fallback.idleMaxSeconds,
    sleepAfterMinutes: pet.sleepAfterMinutes ?? fallback.sleepAfterMinutes,
    greetingTimes: [...(pet.greetingTimes ?? fallback.greetingTimes)],
    emotionCooldownSeconds: pet.emotionCooldownSeconds ?? fallback.emotionCooldownSeconds,
    expressionHoldSeconds: pet.expressionHoldSeconds ?? fallback.expressionHoldSeconds,
    fastDragPxPerSecond: pet.fastDragPxPerSecond ?? fallback.fastDragPxPerSecond,
    // The dictionary is kept as keyed, including emotions a later build added: dropping them here would let a
    // save made on this page delete another build's table.
    emotions: {
      keywords: Object.fromEntries(
        Object.entries(pet.emotions?.keywords ?? fallback.emotions.keywords).map(([key, list]) => [key, list.map((entry) => ({ ...entry }))]),
      ),
      negationPrefixes: [...(pet.emotions?.negationPrefixes ?? fallback.emotions.negationPrefixes)],
      minWeight: pet.emotions?.minWeight ?? fallback.emotions.minWeight,
    },
    profiles: Object.fromEntries(Object.entries(pet.profiles ?? {}).map(([key, profile]) => [key, copyProfile(profile)])),
  }
}

/** The shell's answer to 「桌宠是不是开着」, which is what the window itself obeys. */
const petSpawned = ref(false)

/** Unsubscribes the `partner:pet` listener. */
let stopPetWatch: (() => void) | null = null

/** Whether to offer the pet page: false only in a shell built without the pet (see `petSupported`). */
const petAvailable = ref(true)

const petCore = ref<PetCoreStatus | null>(null)
const petCorePath = ref('')
const petCoreConsent = ref(false)
const petCoreError = ref('')
const petCoreBusy = ref(false)

const petModels = ref<PetModelInfo[]>([])
const petModelsError = ref('')
const petModelPath = ref('')
const petImportError = ref('')
const petImportBusy = ref(false)
/** The path a candidate list came from, so the second post names the file and not the archive. */
const petCandidatePath = ref('')
const petCandidates = ref<string[]>([])
const petCandidateChoice = ref('')
/** The model whose delete is armed: the first press arms, the second deletes — same as the skills list. */
const petArmedModel = ref<string | null>(null)

const petTrialText = ref('')

const EMOTION_LABELS: Record<PetEmotion, string> = {
  smile: tr('笑'),
  angry: tr('怒'),
  cry: tr('哭'),
  shy: tr('害羞'),
}

const ACTION_LABELS: Record<(typeof PET_ACTIONS)[number], string> = {
  blink: tr('眨眼'),
  stretch: tr('伸懒腰'),
  greet: tr('打招呼'),
  wave: tr('挥手'),
  headTurn: tr('转头'),
  headShake: tr('摇头'),
  bodyTurn: tr('扭身'),
  bodyShake: tr('晃身'),
  nod: tr('点头'),
  shake: tr('抖一下'),
  tilt: tr('歪头'),
  pat: tr('摸头'),
  shrink: tr('缩身'),
  surprise: tr('惊讶'),
  puff: tr('鼓脸'),
  sob: tr('抽泣'),
  sleep: tr('入睡'),
  wake: tr('醒来'),
}

function emotionLabel(emotion: string): string {
  return emotion in EMOTION_LABELS ? t(EMOTION_LABELS[emotion as PetEmotion]) : emotion
}

function actionLabel(action: string): string {
  return action in ACTION_LABELS ? t(ACTION_LABELS[action as keyof typeof ACTION_LABELS]) : action
}

/** An older server has no pet endpoints; the section then shows nothing installed rather than an error that
 * reads like a failure of the zip the person is holding. */
async function refreshPetCore(): Promise<void> {
  try {
    const status = await props.api.petCore()
    petCore.value = typeof status === 'object' && status !== null && typeof status.installed === 'boolean' ? status : null
  } catch {
    petCore.value = null
  }
}

async function refreshPetModels(): Promise<void> {
  petModelsError.value = ''
  try {
    const list = await props.api.petModels()
    // A server without this endpoint answers `{}`; iterating that as the model list breaks the whole panel.
    petModels.value = Array.isArray(list) ? list : []
  } catch (failure) {
    petModels.value = []
    petModelsError.value = describeFailure(failure)
  }
}

/** The Core's first eight hex characters are what the page shows: enough to tell two builds apart, and short. */
const petCoreHash = computed(() => (petCore.value?.sha256 ?? '').slice(0, 8))

async function importPetCore(): Promise<void> {
  if (!petCoreConsent.value || petCorePath.value.trim() === '') return

  petCoreBusy.value = true
  petCoreError.value = ''
  try {
    petCore.value = await props.api.importPetCore(petCorePath.value.trim())
    petCorePath.value = ''
    petCoreConsent.value = false
    showToast(t('已导入 Cubism Core'))
  } catch (failure) {
    petCoreError.value = describeFailure(failure)
  } finally {
    petCoreBusy.value = false
  }
}

async function copyText(text: string, message: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text)
    showToast(message)
  } catch {
    showToast(t('复制失败'), 'error')
  }
}

/**
 * Sends a 「预览」 to the pet window. It is not saved first: the mapping being previewed is the one in the form,
 * and the question the button answers is "does this motion look right", not "should this be persisted".
 */
async function previewAction(action: string): Promise<void> {
  await sendPreview(action, '')
}

async function previewExpression(emotion: string): Promise<void> {
  await sendPreview('', emotion)
}

async function sendPreview(action: string, expression: string): Promise<void> {
  try {
    await props.api.previewPetAction(action, expression)
    // The window polls once a second at most, so a preview is not instant; saying so avoids a second click.
    showToast(t('已发送预览，桌宠会在下一次轮询时播放'))
  } catch (failure) {
    showToast(t('预览失败：{error}', { error: describeFailure(failure) }), 'error')
  }
}

/** Imports a folder or a zip. Both posts go through here: the second call names the `model3.json` the person
 * picked, and answers with the model instead of a candidate list.
 */
async function importPetModel(model3?: string): Promise<void> {
  const path = model3 === undefined ? petModelPath.value.trim() : petCandidatePath.value
  if (path === '') return

  petImportBusy.value = true
  petImportError.value = ''
  try {
    const result = await props.api.importPetModel(path, model3)
    if (result.candidates.length > 0) {
      petCandidates.value = result.candidates
      petCandidatePath.value = path
      petCandidateChoice.value = result.candidates[0] ?? ''
      return
    }

    petCandidates.value = []
    await refreshPetModels()
    if (result.imported !== null) {
      petModelPath.value = ''
      // The first model imported becomes the current one: otherwise the pet would stay invisible with the
      // model sitting there, and "it imported but shows nothing" is the confusing state to avoid.
      if (form.pet.activeModelId === null || form.pet.activeModelId === '') {
        form.pet.activeModelId = result.imported.id
      }
      showToast(t('已导入模型「{name}」', { name: result.imported.name }))
    }
  } catch (failure) {
    petImportError.value = describeFailure(failure)
  } finally {
    petImportBusy.value = false
  }
}

async function deletePetModel(id: string): Promise<void> {
  if (petArmedModel.value !== id) {
    petArmedModel.value = id
    return
  }

  petArmedModel.value = null
  petModelsError.value = ''
  try {
    await props.api.deletePetModel(id)
    if (form.pet.activeModelId === id) {
      form.pet.activeModelId = null
    }
    // The model's mapping table is gone with it; keeping it would be a row for a model that no longer exists.
    const { [id]: _removed, ...rest } = form.pet.profiles
    form.pet.profiles = rest
    await refreshPetModels()
  } catch (failure) {
    petModelsError.value = describeFailure(failure)
  }
}

const activePetModel = computed(() => petModels.value.find((model) => model.id === form.pet.activeModelId) ?? null)

function blankProfile(): PetModelProfile {
  return { motions: {}, expressions: {}, headBottom: 0.35, faceBottom: 0.55, defaultExpression: null }
}

/**
 * The section as it is written. A blank keyword or greeting row is one a person abandoned, not an entry; sending
 * it would have the server refuse the whole save over an empty string.
 */
function petPayload(): PetSettings {
  const keywords: Record<string, PetKeyword[]> = {}
  for (const [emotion, list] of Object.entries(form.pet.emotions.keywords)) {
    keywords[emotion] = list
      .map((entry) => ({ text: entry.text.trim(), weight: Math.min(10, Math.max(1, Math.round(entry.weight))) }))
      .filter((entry) => entry.text !== '')
  }

  return {
    enabled: form.pet.enabled,
    alwaysOnTop: form.pet.alwaysOnTop,
    clickThroughTransparent: form.pet.clickThroughTransparent,
    motionSound: form.pet.motionSound,
    scale: form.pet.scale,
    activeModelId: form.pet.activeModelId,
    idleMinSeconds: form.pet.idleMinSeconds,
    idleMaxSeconds: form.pet.idleMaxSeconds,
    sleepAfterMinutes: form.pet.sleepAfterMinutes,
    greetingTimes: form.pet.greetingTimes.map((time) => time.trim()).filter((time) => time !== ''),
    emotionCooldownSeconds: form.pet.emotionCooldownSeconds,
    expressionHoldSeconds: form.pet.expressionHoldSeconds,
    fastDragPxPerSecond: form.pet.fastDragPxPerSecond,
    emotions: {
      keywords,
      negationPrefixes: form.pet.emotions.negationPrefixes.map((prefix) => prefix.trim()).filter((prefix) => prefix !== ''),
      minWeight: form.pet.emotions.minWeight,
    },
    profiles: Object.fromEntries(Object.entries(form.pet.profiles).map(([key, profile]) => [key, copyProfile(profile)])),
  }
}

/** The profile the mapping table edits: the stored one, or the defaults the runtime would use. */
const activeProfile = computed<PetModelProfile>(() => {
  const id = form.pet.activeModelId
  const stored = id === null ? undefined : form.pet.profiles[id]
  return stored === undefined ? blankProfile() : copyProfile(stored)
})

/** Every mapping edit writes one whole profile back, keyed by the active model; nothing partial is merged. */
function editProfile(change: (profile: PetModelProfile) => PetModelProfile): void {
  const id = form.pet.activeModelId
  if (id === null || id === '') return

  const current = form.pet.profiles[id] ?? blankProfile()
  form.pet.profiles = { ...form.pet.profiles, [id]: change(copyProfile(current)) }
}

/**
 * One motion row's `<select>` value. A `kind` this page does not know is carried as `kind:<value>` and written
 * back unchanged: a newer build's mapping must survive a save made here.
 */
function motionValue(ref: { kind: string; group?: string | null; no?: number | null } | undefined): string {
  if (ref === undefined) return 'auto'
  if (ref.kind === 'motion') {
    return ref.group === null || ref.group === undefined || ref.no === null || ref.no === undefined ? 'auto' : `motion:${ref.group}#${ref.no}`
  }
  return ref.kind === 'auto' || ref.kind === 'procedural' || ref.kind === 'none' ? ref.kind : `kind:${ref.kind}`
}

function motionRef(value: string): { kind: string; group: string | null; no: number | null } {
  if (value.startsWith('motion:')) {
    const [group, no] = value.slice('motion:'.length).split('#')
    const index = Number(no)
    return { kind: 'motion', group: group ?? '', no: Number.isInteger(index) ? index : 0 }
  }
  if (value.startsWith('kind:')) return { kind: value.slice('kind:'.length), group: null, no: null }
  return { kind: value, group: null, no: null }
}

function expressionValue(ref: { kind: string; name?: string | null } | undefined): string {
  if (ref === undefined) return 'auto'
  if (ref.kind === 'expression') return ref.name === null || ref.name === undefined || ref.name === '' ? 'auto' : `expression:${ref.name}`
  return ref.kind === 'auto' || ref.kind === 'procedural' || ref.kind === 'none' ? ref.kind : `kind:${ref.kind}`
}

function expressionRef(value: string): { kind: string; name: string | null } {
  if (value.startsWith('expression:')) return { kind: 'expression', name: value.slice('expression:'.length) }
  if (value.startsWith('kind:')) return { kind: value.slice('kind:'.length), name: null }
  return { kind: value, name: null }
}

function setMotion(action: string, value: string): void {
  editProfile((profile) => ({ ...profile, motions: { ...profile.motions, [action]: motionRef(value) } }))
}

function setExpression(name: string, value: string): void {
  editProfile((profile) => ({ ...profile, expressions: { ...profile.expressions, [name]: expressionRef(value) } }))
}

function setHeadBottom(event: Event): void {
  const percent = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(percent)) return
  editProfile((profile) => ({ ...profile, headBottom: Math.min(percent / 100, profile.faceBottom) }))
}

function setFaceBottom(event: Event): void {
  const percent = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(percent)) return
  editProfile((profile) => ({ ...profile, faceBottom: Math.max(percent / 100, profile.headBottom) }))
}

function motionOptions(): { value: string; label: string }[] {
  const model = activePetModel.value
  const options = [
    { value: 'auto', label: t('自动') },
    { value: 'procedural', label: t('程序化') },
    ...(model?.motions ?? []).map((motion) => ({
      value: `motion:${motion.group}#${motion.no}`,
      label: `${motion.group}#${motion.no}${motion.name === '' ? '' : ` · ${motion.name}`}`,
    })),
    { value: 'none', label: t('不用') },
  ]
  return options
}

function expressionOptions(): { value: string; label: string }[] {
  const model = activePetModel.value
  return [
    { value: 'auto', label: t('自动') },
    { value: 'procedural', label: t('程序化') },
    ...(model?.expressions ?? []).map((name) => ({ value: `expression:${name}`, label: name })),
    { value: 'none', label: t('不用') },
  ]
}

/** Adds the row's stored value when the current options do not contain it, so the select never shows a blank. */
function withOption(options: { value: string; label: string }[], value: string): { value: string; label: string }[] {
  return options.some((option) => option.value === value) ? options : [...options, { value, label: value.replace(/^kind:/, '') }]
}

const petScalePercent = computed(() => Math.round(form.pet.scale * 100))

function setScale(event: Event): void {
  const percent = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(percent)) return
  form.pet.scale = percent / 100
}

function setGreetingTimes(event: Event): void {
  form.pet.greetingTimes = parseKeywords((event.target as HTMLInputElement).value)
}

async function resetPetWindow(): Promise<void> {
  petResetError.value = ''
  try {
    await resetPetPosition()
  } catch (error) {
    petResetError.value = t('重置位置失败：{error}', { error: error instanceof Error ? error.message : String(error) })
  }
}

function keywordsOf(emotion: PetEmotion): PetKeyword[] {
  return form.pet.emotions.keywords[emotion] ?? []
}

function editKeywords(emotion: PetEmotion, change: (list: PetKeyword[]) => PetKeyword[]): void {
  form.pet.emotions = {
    ...form.pet.emotions,
    keywords: { ...form.pet.emotions.keywords, [emotion]: change(keywordsOf(emotion).map((entry) => ({ ...entry }))) },
  }
}

function addKeyword(emotion: PetEmotion): void {
  editKeywords(emotion, (list) => [...list, { text: '', weight: 3 }])
}

function setKeywordText(emotion: PetEmotion, at: number, event: Event): void {
  const text = (event.target as HTMLInputElement).value
  editKeywords(emotion, (list) => list.map((entry, index) => (index === at ? { ...entry, text } : entry)))
}

function setKeywordWeight(emotion: PetEmotion, at: number, event: Event): void {
  const weight = Number((event.target as HTMLInputElement).value)
  editKeywords(emotion, (list) => list.map((entry, index) => (index === at ? { ...entry, weight: Number.isFinite(weight) ? weight : 1 } : entry)))
}

function removeKeyword(emotion: PetEmotion, at: number): void {
  editKeywords(emotion, (list) => list.filter((_, index) => index !== at))
}

function setNegationPrefixes(event: Event): void {
  form.pet.emotions = { ...form.pet.emotions, negationPrefixes: parseKeywords((event.target as HTMLInputElement).value) }
}

function setMinWeight(event: Event): void {
  const weight = Number((event.target as HTMLInputElement).value)
  form.pet.emotions = { ...form.pet.emotions, minWeight: Number.isFinite(weight) ? weight : 1 }
}

/** The built-in table lives in the pet frontend, which is also what scores a reply — one table, not two. */
function restoreDefaultDictionary(): void {
  form.pet.emotions = {
    keywords: Object.fromEntries(
      Object.entries(DEFAULT_DICTIONARY.keywords).map(([emotion, list]) => [emotion, list.map((entry) => ({ ...entry }))]),
    ),
    negationPrefixes: [...DEFAULT_DICTIONARY.negationPrefixes],
    minWeight: DEFAULT_DICTIONARY.minWeight,
  }
}

/**
 * The trial box runs the very function the pet window runs, so its answer is the pet's answer. It reads the
 * dictionary on screen, not the saved one: that is what makes it useful while editing.
 */
const petTrial = computed<EmotionMatch | null>(() =>
  petTrialText.value.trim() === '' ? null : matchEmotion(petTrialText.value, form.pet.emotions),
)

/** The winner's primary score is the highest weight among its hits; `matchEmotion` reports the hits, not the max. */
function primaryScore(match: EmotionMatch, emotion: PetEmotion): number {
  return match.hits.reduce((best, hit) => (hit.emotion === emotion ? Math.max(best, hit.weight) : best), 0)
}

// ---- Updates -------------------------------------------------------------------------------------------------
// The version comes from package.json, which the release workflow rewrites from the tag before the UI is built.
// It is therefore the same number the installer carries and the one GitHub is asked to compare against.
const updateChecking = ref(false)
const updateResult = ref<UpdateCheck | null>(null)
const updateError = ref('')
const updateState = ref<UpdateDownload | null>(null)
let updatePoll: number | null = null
let updatePollBusy = false
let updateLaunching = false

function stopUpdatePoll(): void {
  if (updatePoll !== null) {
    window.clearInterval(updatePoll)
    updatePoll = null
  }
}

function failureText(failure: unknown): string {
  return failure instanceof Error ? failure.message : String(failure)
}

async function checkForUpdate(): Promise<void> {
  updateChecking.value = true
  updateError.value = ''
  updateResult.value = null
  updateState.value = null
  updateLaunching = false
  stopUpdatePoll()
  try {
    const result = await props.api.checkUpdate(appVersion)
    updateResult.value = result
    // The check reports a failed lookup in the payload: the version row stays usable either way.
    if (result.error !== null) {
      updateError.value = result.error
    }
  } catch (failure) {
    updateError.value = failureText(failure)
  } finally {
    updateChecking.value = false
  }
}

/** Applies one download state. `ready` is also where the shell is handed the verified file. */
function applyUpdateState(state: UpdateDownload): void {
  updateState.value = state
  if (state.error !== null && state.error !== '') {
    updateError.value = state.error
  }

  if (state.phase === 'ready' && state.path !== null) {
    stopUpdatePoll()
    if (!updateLaunching) {
      updateLaunching = true
      void launchInstaller(state.path).catch((failure: unknown) => {
        updateLaunching = false
        updateError.value = failureText(failure)
      })
    }
    return
  }

  if (state.phase === 'failed' || state.phase === 'cancelled') {
    stopUpdatePoll()
  }
}

async function pollUpdate(): Promise<void> {
  if (isPageHidden()) return
  if (updatePollBusy) return
  updatePollBusy = true
  try {
    applyUpdateState(await props.api.updateDownload())
  } catch (failure) {
    updateError.value = failureText(failure)
    stopUpdatePoll()
  } finally {
    updatePollBusy = false
  }
}

async function beginUpdate(): Promise<void> {
  updateError.value = ''
  updateLaunching = false
  try {
    applyUpdateState(await props.api.downloadUpdate())
    if (updateState.value?.phase === 'downloading' || updateState.value?.phase === 'verifying') {
      stopUpdatePoll()
      updatePoll = window.setInterval(() => void pollUpdate(), 600)
    }
  } catch (failure) {
    updateError.value = failureText(failure)
  }
}

async function cancelUpdateDownload(): Promise<void> {
  try {
    applyUpdateState(await props.api.cancelUpdate())
  } catch (failure) {
    updateError.value = failureText(failure)
  }
}

const updatePercent = computed<number | null>(() => {
  const state = updateState.value
  if (state === null || state.bytesTotal === null || state.bytesTotal <= 0) return null
  return Math.min(100, Math.floor((state.bytesDone / state.bytesTotal) * 100))
})

const updateDownloading = computed(
  () => updateState.value?.phase === 'downloading' || updateState.value?.phase === 'verifying',
)

const updateVerifying = computed(() => updateState.value?.phase === 'verifying')

function sizeLabel(bytes: number): string {
  const megabytes = bytes / (1024 * 1024)
  return megabytes >= 10 ? `${Math.round(megabytes)} MB` : `${megabytes.toFixed(1)} MB`
}

// Reduced transparency and theme are frontend-only and never saved with the settings.
const reducedTransparency = ref(readReducedTransparency())

function onReducedTransparency(value: boolean): void {
  reducedTransparency.value = value
  applyReducedTransparency(value)
}

const THEME_OPTIONS = computed<{ value: Theme; label: string }[]>(() => [
  { value: 'auto', label: themeLabel('auto') },
  { value: 'light', label: themeLabel('light') },
  { value: 'dark', label: themeLabel('dark') },
])

const LOCALE_OPTIONS = LOCALES.map((value) => ({ value, label: LOCALE_NAMES[value] }))

/**
 * A test result is a statement about a configuration. `testedWith` remembers which one, so editing the block
 * afterwards turns the old green tick grey instead of leaving it to vouch for something that no longer exists.
 */
const testedWith = ref<Record<string, string>>({})

function blockPrint(kind: string): string {
  switch (kind) {
    case 'asr':
      return JSON.stringify(form.asr)
    case 'tts':
      return JSON.stringify([form.tts, form.output.sampleRate])
    case 'kws':
      return JSON.stringify(form.local.kws)
    default:
      return JSON.stringify({ ...form.model, reasoningGears: undefined, reasoningLabels: undefined, apiKey: apiKeyEdited.value ? apiKeyDraft.value : null })
  }
}

const testStale = (kind: string) => testedWith.value[kind] !== undefined && testedWith.value[kind] !== blockPrint(kind)

onMounted(() => {
  if (!isPet.value) section.value = readStored(SECTION_KEY, 'model', (value): value is string => typeof value === 'string' && SECTIONS.some((item) => item.key === value))
  advanced.value = readStored(ADVANCED_KEY, {} as Record<string, boolean>, (value): value is Record<string, boolean> => typeof value === 'object' && value !== null)
  stopPetWatch = watchPetShowing((enabled) => {
    // The tray switch and a close of the pet window are not edits made on this page. A clean form adopts the new
    // value as its baseline too, so `dirty` does not light up for a change nobody typed; unsaved edits are left
    // alone.
    const wasClean = !dirty.value
    petSpawned.value = enabled
    form.pet.enabled = enabled
    if (wasClean && stored.value !== null) stored.value = fingerprint()
  })
  // The shell's pet preference is read first, and refresh waits for it: `apply()` reads it to decide what the
  // pet switch shows, and running the two in parallel would show the stored field instead half the time.
  void loadShellPrefs().then(() => {
      // The stored section may be the pet tab in a shell that lacks it.
      section.value = resolveSection(section.value)
      return refresh()
    })
})

onUnmounted(() => {
  stopPetWatch?.()
  stopPetWatch = null
})

/** Asked by the shell before it switches away: the views are `v-if`, so leaving discards every edit. */
function hasUnsavedChanges(): boolean {
  return dirty.value || child.value?.hasUnsavedChanges?.() === true
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="scroll settings-scroll">
      <div class="settings" :class="{ 'is-single': isPet }">
        <nav v-if="!isPet" class="settings-nav" :aria-label="t('设置分类')">
          <button
            v-for="item in SECTIONS"
            :key="item.key"
            class="nav-item"
            :class="{ active: section === item.key }"
            :aria-current="section === item.key ? 'true' : undefined"
            :data-test="`settings-nav-${item.key}`"
            @click="choose(item.key)"
          >
            <Icon :name="item.icon" :size="16" />
            <span>{{ t(item.label) }}</span>
            <span v-if="running[item.key]" class="live-dot" :title="t('settings.running')" />
          </button>
        </nav>

        <fieldset class="settings-body" data-test="settings-editor" :disabled="settingsBusy || cloneBusy">
          <section v-show="section === 'general'" class="panel" data-test="settings-section-general">
            <header class="panel-head"><h2>{{ t('settings.section.general') }}</h2></header>
            <div class="g-card">
              <SettingRow :label="t('settings.general.language')" :hint="t('settings.general.languageHint')" group>
                <Segmented
                  :model-value="locale"
                  :options="LOCALE_OPTIONS"
                  :label="t('settings.general.language')"
                  data-test="settings-language"
                  @update:model-value="setLocale($event as Locale)"
                />
              </SettingRow>
            </div>
            <div v-if="shellPrefs" class="g-card">
              <SettingRow :label="t('settings.general.autostart')" :hint="t('settings.general.autostartHint')">
                <input
                  type="checkbox"
                  class="g-switch"
                  role="switch"
                  :checked="autostart"
                  data-test="settings-autostart"
                  @change="onAutostart(($event.target as HTMLInputElement).checked)"
                />
              </SettingRow>
              <SettingRow :label="t('settings.general.tray')" :hint="t('settings.general.trayHint')">
                <input
                  type="checkbox"
                  class="g-switch"
                  role="switch"
                  :checked="trayEnabled"
                  data-test="settings-tray"
                  @change="onTrayEnabled(($event.target as HTMLInputElement).checked)"
                />
              </SettingRow>
              <p v-if="shellPrefError" class="warn-line" role="alert" data-test="settings-shell-error">{{ shellPrefError }}</p>
            </div>
            <p v-else class="callout" data-test="settings-general-unavailable">{{ t('settings.general.desktopOnly') }}</p>

            <div class="g-card" data-test="settings-update">
              <SettingRow :label="t('settings.general.version')" :hint="t('settings.general.versionHint')">
                <span class="version-chip" data-test="settings-version">v{{ appVersion }}</span>
                <button
                  v-if="shellPrefs"
                  class="ghost"
                  type="button"
                  data-test="settings-check-update"
                  :disabled="updateChecking || updateDownloading"
                  @click="checkForUpdate"
                >
                  {{ updateChecking ? t('settings.general.checking') : t('settings.general.checkUpdate') }}
                </button>
              </SettingRow>

              <p v-if="updateError" class="warn-line" role="alert" data-test="settings-update-error">{{ updateError }}</p>
              <p v-else-if="updateResult && !updateResult.available" class="hint-line" data-test="settings-update-latest">
                {{ t('settings.general.upToDate', { version: `v${updateResult.latest ?? updateResult.current}` }) }}
              </p>

              <template v-else-if="updateResult?.available">
                <p class="update-line" data-test="settings-update-available">
                  {{ t('settings.general.updateAvailable', { version: `v${updateResult.latest}` }) }}
                </p>

                <div v-if="updateDownloading" class="update-actions" data-test="settings-update-progress">
                  <span class="update-size">
                    {{ updateVerifying ? t('settings.general.updateVerifying') : t('settings.general.updateDownloading') }}
                  </span>
                  <span v-if="updatePercent !== null" class="g-chip">{{ updatePercent }}%</span>
                  <span v-else-if="(updateState?.bytesDone ?? 0) > 0" class="g-chip">{{ sizeLabel(updateState?.bytesDone ?? 0) }}</span>
                  <button class="ghost" type="button" data-test="settings-update-cancel" @click="cancelUpdateDownload">
                    {{ t('取消') }}
                  </button>
                </div>

                <p v-else-if="updateState?.phase === 'ready'" class="hint-line" data-test="settings-update-starting">
                  {{ t('settings.general.updateStarting') }}
                </p>

                <div v-else class="update-actions">
                  <span v-if="updateResult.sizeBytes !== null" class="update-size">{{ sizeLabel(updateResult.sizeBytes) }}</span>
                  <button class="primary" type="button" data-test="settings-update-install" @click="beginUpdate">
                    {{ t('settings.general.downloadAndInstall') }}
                  </button>
                </div>
              </template>

              <p v-if="updateResult?.source" class="hint-line" data-test="settings-update-source">
                {{ updateResult.source === 'api'
                  ? t('settings.general.updateSourceApi')
                  : t('settings.general.updateSourceDirect') }}
              </p>
              <p v-if="!shellPrefs" class="hint-line" data-test="settings-update-desktop-only">
                {{ t('settings.general.updateDesktopOnly') }}
              </p>
            </div>
          </section>

          <section v-show="section === 'model'" class="panel" data-test="settings-section-model">
            <header class="panel-head">
              <h2>{{ t('对话模型') }}</h2>
              <span
                class="g-chip"
                :class="consumer?.running ? 'is-ok' : modelConfigured ? 'is-accent' : 'is-warn'"
                data-test="model-effect"
              >
                <template v-if="consumer?.running">{{ t('运行中 · {model}', { model: consumer.model ?? t('模型') }) }}</template>
                <template v-else-if="modelConfigured">{{ t('已填好，保存后生效') }}</template>
                <template v-else>{{ t('未配置，助手暂时不会执行任务') }}</template>
              </span>
            </header>

            <div class="g-card">
              <div class="g-card-head">
                <h3>{{ t('连接') }}</h3>
                <TestButton
                  kind="model"
                  :label="t('测试连接')"
                  :testing="voiceTesting.model === true"
                  :result="voiceTest.model"
                  :stale="testStale('model')"
                  :unsaved="dirty"
                  data-test="model-test-row"
                  @test="testConnection('model')"
                />
              </div>

              <SettingRow :label="t('接口格式')">
                <template #hint>{{ t('选择服务商使用的接口协议：OpenAI 兼容端点选 OpenAI，Claude 或兼容 Anthropic Messages 的端点选 Anthropic。') }}</template>
                <select v-model="form.model.protocol" data-test="model-protocol">
                  <option value="openai">{{ t('OpenAI 兼容') }}</option>
                  <option value="anthropic">Anthropic (Claude)</option>
                </select>
              </SettingRow>

              <SettingRow :label="t('接口地址')">
                <template #hint>
                  {{ t('模型服务的网址，一般以 /v1 结尾，比如 Ollama 是') }} <code>http://127.0.0.1:11434/v1</code>{{ t('。留空则读环境变量') }}
                  <code>PARTNER_LLM_BASE_URL</code>{{ t('。') }}
                </template>
                <input
                  type="text"
                  v-model="form.model.baseUrl"
                  :placeholder="form.model.protocol === 'anthropic' ? 'https://api.anthropic.com/v1' : 'http://127.0.0.1:11434/v1'"
                  data-test="model-base-url"
                />
              </SettingRow>

              <SettingRow :label="t('模型')">
                <template #hint>
                  {{ t('点右边的刷新图标，可以从服务里读出可选的模型；读不到就手动填。留空则读环境变量') }} <code>PARTNER_LLM_MODEL</code>{{ t('。') }}
                </template>
                <select v-if="models.length > 0" v-model="form.model.model" data-test="model-name">
                  <option v-for="name in withStored(models, form.model.model)" :key="name" :value="name">{{ name }}</option>
                </select>
                <input v-else type="text" v-model="form.model.model" placeholder="qwen3" data-test="model-name" />
                <button
                  class="ghost icon-only"
                  type="button"
                  :disabled="probing"
                  :aria-busy="probing || undefined"
                  :title="t('从服务读取可选模型')"
                  data-test="model-probe"
                  @click.prevent="probeModels"
                >
                  <Icon name="refresh" :size="16" /><span class="sr-only">{{ probing ? t('拉取中…') : t('拉取模型列表') }}</span>
                </button>
              </SettingRow>
              <p v-if="models.length > 0 || modelProbeError" class="row-note">
                <span v-if="models.length > 0" data-test="model-count">{{ t('{length} 个可选', { length: models.length }) }}</span>
                <span v-if="modelProbeError" class="error break" data-test="model-probe-error">{{ modelProbeError }}</span>
              </p>

              <SettingRow label="API Key">
                <template #hint><span data-test="model-api-key-hint">{{ keyHint }}</span></template>
                <input
                  type="password"
                  v-model="apiKeyDraft"
                  :placeholder="apiKeySet ? t('（已设置）') : t('（未设置）')"
                  autocomplete="off"
                  data-test="model-api-key"
                  @input="apiKeyEdited = true"
                />
                <span class="g-chip" :class="apiKeySet ? 'is-ok' : ''" data-test="model-api-key-state">{{ apiKeySet ? t('已设置') : t('未设置') }}</span>
                <button class="ghost" type="button" data-test="model-api-key-clear" @click.prevent="clearKey">{{ t('清除') }}</button>
              </SettingRow>

              <SettingRow :label="t('最长等待')" :unit="t('秒')" :hint="t('模型超过这个时间还没答完，就放弃这次请求。')">
                <input type="number" v-model.number="form.model.timeoutSeconds" />
              </SettingRow>
              <SettingRow :label="t('记忆长度')" unit="token">
                <template #hint>{{ t('模型一次最多能「看」多少内容，约 1 个汉字算 1 token。改小并保存后，较早的对话会立刻被压缩。') }}</template>
                <input type="number" min="1" v-model.number="form.model.contextTokens" data-test="model-context-tokens" />
              </SettingRow>
              <SettingRow :label="t('单次回复上限')" unit="token" :hint="t('一次回复最多写多长，必须小于「记忆长度」。')">
                <input type="number" min="1" v-model.number="form.model.maxTokens" data-test="model-max-tokens" />
              </SettingRow>
            </div>

            <div class="g-card">
              <div class="g-card-head"><h3>{{ t('思考') }}</h3></div>
              <SettingRow :label="t('思考强度')" stack :hint="t('回答前想多久。左边是界面上显示的字，右边是真正发给模型的词，你可以增减。选中的「默认」用于新消息，会话里可以临时改。传值为 off 表示不向模型发这个参数。')">
                <GearTable
                  v-model="form.model.reasoningEffort"
                  v-model:gears="form.model.reasoningGears"
                  v-model:labels="form.model.reasoningLabels"
                  data-test="model-reasoning-gears"
                />
              </SettingRow>
              <SettingRow :label="t('能看图片')">
                <template #hint>
                  {{ t('开启后，遇到读不出文字的软件（游戏、画布等），助手会直接截图给模型看。纯文字模型请保持关闭，否则请求会失败。') }}
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.model.supportsImages" data-test="model-supports-images" />
              </SettingRow>
            </div>

          </section>

          <VoiceInputSettingsPanel
            v-show="section === 'input'"
            :form="form" :voice="voice" :voice-test="voiceTest" :voice-testing="voiceTesting"
            :dirty="dirty" :advanced="advanced" :toggle-advanced="toggleAdvanced"
            :test-stale="testStale" :test-connection="testConnection" :input-options="inputOptions"
            :wake-gate-on="wakeGateOn" :level-percent="levelPercent" :threshold-percent="thresholdPercent"
            :keyword-drafts="keywordDrafts" :on-engine-keywords-input="onEngineKeywordsInput"
          />

          <VoiceOutputSettingsPanel
            v-show="section === 'output'"
            :form="form" :voice="voice" :voice-test="voiceTest" :voice-testing="voiceTesting"
            :dirty="dirty" :advanced="advanced" :toggle-advanced="toggleAdvanced"
            :test-stale="testStale" :test-connection="testConnection" :output-options="outputOptions"
            :clone-name="cloneName" :clone-busy="cloneBusy" :clone-error="cloneError"
            :on-clone-file="onCloneFile" :test-tts-url="testTtsUrl"
          />

          <section v-show="section === 'desktop'" class="panel" data-test="settings-section-desktop">
            <header class="panel-head"><h2>{{ t('电脑操控') }}</h2></header>

            <!-- The one sentence on this page that is not behind an ⓘ. A hover is a good place for a footnote and a
                 bad place for the fact that this capability acts on your computer. -->
            <p class="callout" data-test="desktop-callout">
              {{ t('开启后，助手可以观察窗口并操作鼠标键盘；审批取决于当前挡位。') }}
            </p>

            <div class="g-card">
              <SettingRow :label="t('允许助手操作电脑')">
                <template #hint>
                  {{ t('默认关闭。开启后助手能观察窗口并操作鼠标键盘；审批取决于当前挡位，无人回应即拒绝。关闭时助手拿不到这些工具。') }}
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.desktop.enabled" data-test="desktop-enabled" />
              </SettingRow>
              <p v-if="desktopAgentOff" class="warn-line" data-test="desktop-agent-warning">
                {{ t('还需要启用「桌面操控」子 agent，助手才能使用这个能力。') }}
                <button class="ghost" data-test="desktop-agent-fix" @click="choose('agents')">{{ t('去启用') }}</button>
              </p>
              <SettingRow :label="t('允许执行系统命令')">
                <template #hint>
                  {{ t('默认关闭，需同时启用电脑操控。仅在全部审批挡位下每条命令需要批准，不受窗口授权影响；其他挡位直接执行。命令以系统权限访问文件，不受文件白名单约束。') }}
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.desktop.execEnabled" data-test="desktop-exec-enabled" />
              </SettingRow>
              <p class="warn-line">{{ t('通过窗口操作的应用也能按系统权限访问文件，不受文件白名单约束；需要系统级隔离时请使用系统安全措施。') }}</p>
              <SettingRow :label="t('只允许操作这些窗口')">
                <template #hint>
                  {{ t('填窗口标题里的关键词，用逗号分隔，不分大小写；留空表示不限制。真正拦住动作的是你的批准，这里只是缩小范围。') }}
                </template>
                <input
                  type="text"
                  :value="form.desktop.allowedWindowTitles.join(t('、'))"
                  :placeholder="t('记事本、计算器')"
                  data-test="desktop-allowed-titles"
                  @input="onTitlesInput"
                />
              </SettingRow>
              <SettingRow :label="t('操作时占用屏幕吗')">
                <template #hint>
                  <span data-test="desktop-input-mode-hint">{{ t(INPUT_MODES.find((mode) => mode.value === form.desktop.inputMode)?.hint ?? '') }}</span>
                  {{ t('点击和打字默认要把窗口带到前台、移动你的鼠标。选「尽量后台」后，由助手启动的浏览器和 Electron 程序可以在后台完成，其余照旧；选「只在后台」则做不到的动作直接拒绝。') }}
                </template>
                <select v-model="form.desktop.inputMode" data-test="desktop-input-mode">
                  <option v-for="mode in INPUT_MODES" :key="mode.value" :value="mode.value">{{ t(mode.label) }}</option>
                </select>
              </SettingRow>
            </div>

            <button class="ghost more" :aria-expanded="advanced.desktop === true" data-test="settings-more-desktop" @click="toggleAdvanced('desktop')">
              <Icon name="chevron-down" :size="14" class="more-chevron" :class="{ 'is-open': advanced.desktop === true }" />
              {{ t('更多选项') }}
            </button>
            <div v-show="advanced.desktop === true" class="g-card">
              <SettingRow :label="t('看屏幕后可操作多久')" :unit="t('毫秒')">
                <template #hint>
                  {{ t('助手看过一次屏幕后，这段时间内可以照着看到的内容去点、去输入；超时要重新看一遍。默认 15000，即 15 秒。') }}
                </template>
                <input type="number" min="1000" v-model.number="form.desktop.leaseTtlMs" data-test="desktop-lease-ttl" />
              </SettingRow>
            </div>
          </section>

          <PetSettingsPanel
            v-if="petAvailable" v-show="section === 'pet'"
            v-model:pet-core-consent="petCoreConsent" v-model:pet-core-path="petCorePath"
            v-model:pet-model-path="petModelPath" v-model:pet-candidate-choice="petCandidateChoice"
            v-model:pet-trial-text="petTrialText"
            :form="form" :api="api" :PET_CORE_URL="PET_CORE_URL" :PET_LICENSE_URLS="PET_LICENSE_URLS"
            :PET_ACTIONS="PET_ACTIONS" :PET_EMOTIONS="PET_EMOTIONS"
            :pet-core="petCore" :pet-core-hash="petCoreHash" :pet-core-busy="petCoreBusy" :pet-core-error="petCoreError"
            :pet-models="petModels" :pet-models-error="petModelsError" :pet-armed-model="petArmedModel"
            :pet-import-busy="petImportBusy" :pet-import-error="petImportError" :pet-candidates="petCandidates"
            :pet-scale-percent="petScalePercent" :shell-prefs="shellPrefs" :pet-spawned="petSpawned"
            :pet-shell-error="petShellError" :pet-reset-error="petResetError" :pet-retry-target="petRetryTarget" :retry-pet-window="retryPetWindow"
            :active-pet-model="activePetModel" :active-profile="activeProfile" :pet-trial="petTrial"
            :copy-text="copyText" :import-pet-core="importPetCore" :refresh-pet-models="refreshPetModels"
            :format-bytes="formatBytes" :delete-pet-model="deletePetModel" :import-pet-model="importPetModel"
            :set-scale="setScale" :reset-pet-window="resetPetWindow" :action-label="actionLabel" :emotion-label="emotionLabel"
            :motion-value="motionValue" :expression-value="expressionValue" :set-motion="setMotion" :set-expression="setExpression"
            :with-option="withOption" :motion-options="motionOptions" :expression-options="expressionOptions"
            :preview-action="previewAction" :preview-expression="previewExpression" :set-head-bottom="setHeadBottom"
            :set-face-bottom="setFaceBottom" :edit-profile="editProfile" :set-greeting-times="setGreetingTimes"
            :restore-default-dictionary="restoreDefaultDictionary" :keywords-of="keywordsOf" :set-keyword-text="setKeywordText"
            :set-keyword-weight="setKeywordWeight" :remove-keyword="removeKeyword" :add-keyword="addKeyword"
            :set-negation-prefixes="setNegationPrefixes" :set-min-weight="setMinWeight" :primary-score="primaryScore"
          />

          <section v-show="section === 'files'" class="panel" data-test="settings-section-files">
            <header class="panel-head"><h2>{{ t('文件访问') }}</h2></header>

            <div class="g-card">
              <SettingRow :label="t('允许助手读取文件')">
                <template #hint>
                  <span data-test="files-whitelist-hint">{{ t('原生文件读取默认关闭，只能访问读取白名单中获准的内容。桌面应用、命令和 MCP 不受此白名单限制。') }}</span>
                  <span data-test="files-content-hint"> {{ t('读取的文件内容会进入模型上下文和会话记录。') }}</span>
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.files.enabled" data-test="files-enabled" />
              </SettingRow>

              <div class="folders" data-test="files-roots">
                <div class="folders-head">
                  <h3>{{ t('可读取的文件夹') }}</h3>
                  <HintTip align="end">{{ t('填完整路径，一行一个。') }}</HintTip>
                </div>
                <p v-if="form.files.roots.length === 0" class="muted" data-test="files-roots-empty">
                  {{ t('还没有文件夹。开启后至少添加一个，否则无法保存。') }}
                </p>
                <div v-for="(root, index) in form.files.roots" :key="index" class="folder-row">
                  <Icon name="folder" :size="16" class="folder-icon" />
                  <input
                    type="text"
                    :value="root"
                    :aria-label="t('第 {v} 个可读取的文件夹', { v: index + 1 })"
                    :placeholder="t('D:/资料')"
                    data-test="files-root"
                    @input="form.files.roots[index] = ($event.target as HTMLInputElement).value"
                  />
                  <button
                    class="ghost icon-only"
                    :aria-label="t('移除第 {v} 个文件夹', { v: index + 1 })"
                    :title="t('移除')"
                    data-test="files-root-remove"
                    @click="form.files.roots = form.files.roots.filter((_, at) => at !== index)"
                  >
                    <Icon name="x" :size="14" /><span class="sr-only">{{ t('移除') }}</span>
                  </button>
                </div>
                <button class="ghost add" data-test="files-root-add" @click="form.files.roots = [...form.files.roots, '']">
                  <Icon name="plus" :size="14" /> {{ t('添加文件夹') }}
                </button>
              </div>

              <SettingRow :label="t('允许新建和编辑文件')">
                <template #hint>
                  <span>{{ t('默认关闭。仅支持 Windows 本地 NTFS，目标须同时位于读取和写入白名单内；不审批挡位直接执行，其他挡位每次都须批准。新建不覆盖已有文件；编辑仅支持 UTF-8 并复查预期版本，但外部进程在复查与替换之间修改的内容可能被覆盖。') }}</span>
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.files.writeEnabled" data-test="files-write-enabled" />
              </SettingRow>
              <div class="folders" data-test="files-write-roots">
                <div class="folders-head"><h3>{{ t('可写入的文件夹') }}</h3><HintTip align="end">{{ t('填完整路径，一行一个。') }}</HintTip></div>
                <p v-if="form.files.writeRoots.length === 0" class="muted">{{ t('还没有文件夹。开启后至少添加一个，否则无法保存。') }}</p>
                <div v-for="(root, index) in form.files.writeRoots" :key="index" class="folder-row">
                  <Icon name="folder" :size="16" class="folder-icon" />
                  <input type="text" :value="root" :aria-label="t('第 {v} 个可写入的文件夹', { v: index + 1 })" :placeholder="t('D:/资料')" data-test="files-write-root" @input="form.files.writeRoots[index] = ($event.target as HTMLInputElement).value" />
                  <button class="ghost icon-only" :aria-label="t('移除第 {v} 个文件夹', { v: index + 1 })" :title="t('移除')" data-test="files-write-root-remove" @click="form.files.writeRoots = form.files.writeRoots.filter((_, at) => at !== index)">
                    <Icon name="x" :size="14" /><span class="sr-only">{{ t('移除') }}</span>
                  </button>
                </div>
                <button class="ghost add" data-test="files-write-root-add" @click="form.files.writeRoots = [...form.files.writeRoots, '']"><Icon name="plus" :size="14" /> {{ t('添加文件夹') }}</button>
              </div>
            </div>
          </section>

          <section v-show="section === 'web'" class="panel" data-test="settings-section-web">
            <header class="panel-head">
              <h2>{{ t('联网搜索') }}</h2>
              <span class="g-chip" :class="form.web.enabled ? 'is-ok' : ''" data-test="web-effect">
                {{ form.web.enabled ? t('已开启') : t('已关闭') }}
              </span>
            </header>

            <!-- The only section here that is on by default, and the only one that sends what was typed off
                 this machine. That goes in a sentence rather than behind an ⓘ, for the same reason the
                 desktop callout is not behind one. -->
            <p class="callout" data-test="web-callout">
              {{ t('默认开启。开启后，助手要把某句话拿去搜的时候，那句话会发给第三方搜索引擎，因此会离开这台电脑。 搜索结果的地址会先做一次安全检查，指向本机、内网或云服务商元数据地址的一律拒绝；从网上读回来的 内容只当资料，写在里面的任何指令都不会被执行。') }}
            </p>

            <div class="g-card">
              <SettingRow :label="t('允许助手联网搜索')">
                <template #hint>
                  {{ t('关闭后助手完全拿不到联网工具，连打开已知网页也一起停用。查不到就说查不到，不会绕过去。') }}
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.web.enabled" data-test="web-enabled" />
              </SettingRow>
              <SettingRow :label="t('每次查询最多等多久')" :unit="t('秒')">
                <template #hint>
                  {{ t('从第一个引擎返回结果开始计时，到点就用手上已有的结果作答，不再等慢的那几家。可填 3–60 秒。') }}
                </template>
                <input type="number" min="3" max="60" v-model.number="form.web.timeBudgetSeconds" data-test="web-budget" />
              </SettingRow>
              <SettingRow :label="t('答案里最多列几条')" :unit="t('条')">
                <template #hint>
                  {{ t('列出来的是一组线索，不是答案。助手要据此下结论，还得逐条打开原文核对。可填 1–20。') }}
                </template>
                <input type="number" min="1" max="20" v-model.number="form.web.maxResults" data-test="web-max-results" />
              </SettingRow>
            </div>

            <div class="g-card">
              <div class="g-card-head">
                <h3>
                  {{ t('搜索引擎') }}
                  <HintTip>
                    {{ t('某一家连续失败会被自动停用一段时间，其间由其余引擎顶上；这里关掉的引擎则是长期不再使用。') }}
                  </HintTip>
                </h3>
                <button class="ghost icon-only" :title="t('刷新状态')" :aria-label="t('刷新引擎状态')" data-test="web-refresh" @click="refreshWeb()">
                  <Icon name="refresh" :size="14" /><span class="sr-only">{{ t('刷新状态') }}</span>
                </button>
                <button class="secondary" type="button" aria-haspopup="dialog" data-test="web-engine-add" @click="addWebEngine">{{ t('新增引擎') }}</button>
                <button class="ghost" type="button" data-test="web-engine-restore" @click="restoreWebDefaults">{{ t('恢复默认引擎') }}</button>
              </div>

              <p v-if="webView === null" class="muted" data-test="web-engines-unknown">
                {{ t('读不到引擎状态。开关照常保存，状态等后端可用后再刷新。') }}
              </p>
              <p v-if="webEngineRows().length === 0" class="muted" data-test="web-engines-empty">
                {{ t('还没有搜索引擎。请新增或恢复默认引擎。') }}
              </p>
              <div
                v-for="engine in webEngineRows()"
                :key="engine.id"
                class="engine-row"
                :data-test="`web-engine-${engine.id}`"
              >
                <div class="engine-identity">
                  <span class="engine-name" :title="engine.name">
                    {{ engine.name }}
                    <span v-if="engine.presetId" class="engine-tag">{{ t('默认') }}</span>
                    <span v-if="engine.singleSite" class="engine-tag" :title="t('只索引自己这一个站点')">{{ t('单一站点') }}</span>
                  </span>
                  <span class="engine-kinds">{{ engine.kinds.map(kindLabel).join(t('、')) }}</span>
                </div>
                <input
                  type="checkbox"
                  class="g-switch engine-enabled"
                  role="switch"
                  :aria-label="t('使用 {name}', { name: engine.name })"
                  :checked="engine.enabled"
                  :data-test="`web-engine-${engine.id}-enabled`"
                  @change="toggleEngine(engine.id)"
                />
                <button class="ghost engine-edit-button" type="button" aria-haspopup="dialog" :aria-label="t('编辑 {name}', { name: engine.name })" :data-test="`web-engine-${engine.id}-edit`" @click="editWebEngine(engine.id)">{{ t('编辑') }}</button>
                <button class="ghost engine-delete-button" type="button" :data-test="`web-engine-${engine.id}-delete`" @click="removeWebEngine(engine.id)">{{ t('删除') }}</button>
              </div>
              <Dialog
                :open="section === 'web' && engineDraft !== null"
                :title="isEditingEngine ? t('编辑引擎') : t('新增引擎')"
                @close="closeWebEngine"
              >
                <form v-if="engineDraft" id="web-engine-form" class="engine-edit" data-test="web-engine-form" @submit.prevent="confirmWebEngine">
                  <label class="is-wide">
                    {{ t('名称') }}
                    <input v-model.trim="engineDraft.name" :placeholder="t('例如：我的搜索引擎')" required data-test="web-engine-name" />
                  </label>
                  <label v-if="!engineDraft.presetId" class="is-wide">
                    {{ t('URL 模板，包含 {query}') }}
                    <input v-model.trim="engineDraft.urlTemplate" placeholder="https://example.com/search?q={query}" required data-test="web-engine-url-template" />
                  </label>
                  <label v-if="!engineDraft.presetId" class="is-wide">
                    {{ t('结果数组路径') }}
                    <input v-model.trim="engineDraft.resultsPath" :placeholder="t('例如：results（响应本身是数组时留空）')" />
                  </label>
                  <label v-if="!engineDraft.presetId">
                    {{ t('标题字段') }}
                    <input v-model.trim="engineDraft.titleField" placeholder="title" required data-test="web-engine-title-field" />
                  </label>
                  <label v-if="!engineDraft.presetId">
                    {{ t('链接字段') }}
                    <input v-model.trim="engineDraft.urlField" placeholder="url" required data-test="web-engine-url-field" />
                  </label>
                  <label v-if="!engineDraft.presetId" class="is-wide">
                    {{ t('摘要字段（可选）') }}
                    <input v-model.trim="engineDraft.snippetField" :placeholder="t('例如：snippet 或 content.text')" aria-describedby="web-engine-snippet-hint" />
                    <span id="web-engine-snippet-hint" class="muted">{{ t('填写每条结果中摘要文本的字段名或路径，嵌套字段用点连接。留空时仍返回标题和链接，不提取摘要。') }}</span>
                  </label>
                </form>
                <template #footer>
                  <button class="ghost" type="button" data-test="web-engine-cancel" @click="closeWebEngine">{{ t('取消') }}</button>
                  <button class="primary" type="submit" form="web-engine-form" data-test="web-engine-confirm">{{ t('确认') }}</button>
                </template>
              </Dialog>
            </div>

            <button
              class="ghost more"
              :aria-expanded="advanced.web === true"
              data-test="settings-more-web"
              @click="toggleAdvanced('web')"
            >
              <Icon name="chevron-down" :size="14" class="more-chevron" :class="{ 'is-open': advanced.web === true }" />
              {{ t('更多选项') }}
            </button>
            <div v-show="advanced.web === true" class="g-card">
              <SettingRow :label="t('代理')">
                <template #hint>
                  {{ t('形如') }} <code>http://127.0.0.1:7890</code>{{ t('；留空表示直连。只支持 http 和 https 代理。') }}
                </template>
                <input
                  type="text"
                  :value="form.web.proxy ?? ''"
                  :placeholder="t('留空 = 直连')"
                  data-test="web-proxy"
                  @input="form.web.proxy = ($event.target as HTMLInputElement).value"
                />
              </SettingRow>
              <p class="row-note" data-test="web-health-path">{{ t('熔断状态文件：{v}', { v: webView?.healthPath ?? '—' }) }}</p>
            </div>
          </section>

          <EngineSettingsPanel
            v-show="section === 'engines'"
            v-model:engine-manual-open="engineManualOpen"
            v-model:engine-manual-root="engineManualRoot"
            :form="form"
            :engine-package="enginePackage"
            :engine-package-note="enginePackageNote"
            :download-running="downloadRunning"
            :all-engines-installed="allEnginesInstalled"
            :download-text="downloadText"
            :detect-engines="detectEngines"
            :start-engine-download="startEngineDownload"
            :cancel-engine-download="cancelEngineDownload"
            :apply-engine-root="applyEngineRoot"
            :show-engine="showEngine"
            :on-engine-arguments-input="onEngineArgumentsInput"
          />

          <section v-show="section === 'appearance'" class="panel" data-test="settings-section-appearance">
            <header class="panel-head"><h2>{{ t('settings.section.appearance') }}</h2></header>
            <div class="g-card">
              <SettingRow :label="t('settings.appearance.theme')" :hint="t('settings.appearance.themeHint')">
                <Segmented
                  :model-value="theme ?? 'auto'"
                  :options="THEME_OPTIONS"
                  :label="t('settings.appearance.theme')"
                  data-test="settings-theme"
                  @update:model-value="emit('set-theme', $event as Theme)"
                />
              </SettingRow>
              <SettingRow :label="t('settings.appearance.reduce')" :hint="t('settings.appearance.reduceHint')">
                <input
                  type="checkbox"
                  class="g-switch"
                  role="switch"
                  :checked="reducedTransparency"
                  data-test="settings-reduce-transparency"
                  @change="onReducedTransparency(($event.target as HTMLInputElement).checked)"
                />
              </SettingRow>
            </div>
          </section>

          <div v-if="embedded" class="embedded" :data-test="`settings-embedded-${section}`">
            <SkillsView v-if="section === 'skills'" ref="child" :api="api" @changed="onChildChanged" />
            <McpView v-else-if="section === 'mcp'" ref="child" :api="api" @changed="onChildChanged" />
            <PromptsView v-else ref="child" :api="api" @changed="onChildChanged" @navigate="onChildNavigate" />
          </div>
        </fieldset>
      </div>
    </div>

    <SaveBar
      v-if="!embedded"
      prefix="settings"
      :dirty="dirty"
      :busy="settingsBusy || cloneBusy"
      :status="status"
      :error="error"
      can-discard
      @save="persist"
      @discard="refresh"
    />
  </div>
</template>

<style scoped src="./settings-panel.css"></style>
