<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import Icon from '../components/Icon.vue'
import HintTip from '../components/HintTip.vue'
import GearTable from '../components/GearTable.vue'
import SaveBar from '../components/SaveBar.vue'
import Segmented from '../components/Segmented.vue'
import SettingRow from '../components/SettingRow.vue'
import TestButton from '../components/TestButton.vue'
import { INPUT_MODES } from '../api'
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
  WebEngineView,
  WebView,
} from '../api'


/**
 * Fallback menu, used only until the server answers. The stored list is what this page edits, so a
 * hardcoded copy used as the *saved* value would quietly re-add gears the user removed.
 */
const DEFAULT_REASONING_GEARS = ['off', 'low', 'medium', 'high', 'xhigh', 'max']
const props = defineProps<{ api: PartnerApiSurface; theme?: Theme | undefined }>()
const emit = defineEmits<{ saved: []; navigate: [key: string]; 'set-theme': [theme: Theme] }>()

/** The form is a projection of the stored settings; nothing here is authoritative until it is saved. */
const form = reactive<RuntimeSettings>({
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
  desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', inputMode: 'foreground', allowedWindowTitles: [] as string[] },

  // Carried through untouched so the two capability pages' state survives a save made here. Not editable on
  // this page: a server row and a skill row need more room than a card in a settings form gives them.
  mcp: { enabled: false, servers: [], allowedTools: [] },
  skills: { disabled: [] },

  // Also carried, not edited: the prompts page owns the agents section. Dropping it here would reset every
  // prompt and sub-agent the next time anyone saved a voice setting.
  agents: { main: { prompt: null }, subAgents: [] },
  files: { enabled: false, roots: [] },

  // The one section here that defaults to ON, and the one that sends what you typed off this machine. The
  // default is deliberate (not an oversight): the search is useless if nobody ever finds the switch, and the
  // section says in as many words where the queries go.
  web: { enabled: true, timeBudgetSeconds: 12, maxResults: 8, proxy: null, disabledEngines: [] as string[] },

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

function apply(settings: RuntimeSettings & { model: { apiKeySet?: boolean } }) {
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

  // Replaced, not merged: the response is the whole truth, and holding on to what arrived earlier would
  // resurrect a server or a deny entry the person has since removed on their own page.
  form.mcp = { ...settings.mcp, servers: [...(settings.mcp?.servers ?? [])], allowedTools: [...(settings.mcp?.allowedTools ?? [])] }
  form.skills = { disabled: [...(settings.skills?.disabled ?? [])] }

  // Carried, never edited here. Replaced rather than merged for the same reason: the prompts page is the one
  // place that writes this section, and this page's only job is to hand it back unchanged.
  form.agents = {
    main: { prompt: settings.agents?.main?.prompt ?? null },
    subAgents: (settings.agents?.subAgents ?? []).map((agent) => ({ ...agent })),
  }
  form.files = { enabled: settings.files?.enabled ?? false, roots: [...(settings.files?.roots ?? [])] }

  // A response from an older build carries no `web` at all; the fallback is the same default the section
  // starts from, so saving from a stale page writes back what the switch is showing rather than turning the
  // capability off behind the user's back.
  form.web = {
    enabled: settings.web?.enabled ?? true,
    timeBudgetSeconds: settings.web?.timeBudgetSeconds ?? 12,
    maxResults: settings.web?.maxResults ?? 8,
    proxy: settings.web?.proxy ?? null,
    disabledEngines: [...(settings.web?.disabledEngines ?? [])],
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
  error.value = ''
  try {
    apply(await props.api.settings())
    voice.value = await props.api.voice()
    devices.value = await props.api.devices()
  } catch (failure) {
    error.value = String(failure)
  }

  await refreshWeb()
  await refreshEnginePackage()
  await refreshPetCore()
  await refreshPetModels()
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

async function refreshWeb() {
  try {
    webView.value = await props.api.web()
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

const ENGINE_NAMES = { tts: tr('朗读引擎'), kws: tr('唤醒与识别引擎') } as const

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
    fillEngineDefaults()
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
  const off = form.web.disabledEngines
  form.web.disabledEngines = off.includes(id) ? off.filter((each) => each !== id) : [...off, id]
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

/**
 * One sentence describing an engine's current state. Paused and failed are reported explicitly: "no results
 * this time" must not read as "nothing on the web" when the engine is actually circuit-broken.
 */
function engineState(engine: WebEngineView): string {
  if (form.web.disabledEngines.includes(engine.id)) {
    return t('已关闭')
  }
  if (engine.cooling) {
    const until = engine.until === null ? '' : t('，{toLocaleTimeString} 后再试', { toLocaleTimeString: new Date(engine.until).toLocaleTimeString(locale.value) })
    return t('连续失败 {strikes} 次，暂停中{until}', { strikes: engine.strikes, until: until })
  }
  if (engine.strikes > 0 && engine.lastReason !== null) {
    return t('上次失败：{lastReason}', { lastReason: engine.lastReason })
  }
  return t('正常')
}

function engineStateClass(engine: WebEngineView): string {
  if (form.web.disabledEngines.includes(engine.id)) {
    return 'is-off'
  }
  return engine.cooling || engine.strikes > 0 ? 'is-warn' : 'is-ok'
}

const cloneInput = ref<HTMLInputElement | null>(null)
const cloneBusy = ref(false)
const cloneError = ref('')
const cloneName = computed(() => (form.tts.speakerRefPath ?? '').split(/[\/]/).pop() ?? '')

/** Uploads the chosen file and stores its on-disk path: the browser cannot supply a path and the engine needs one. */
async function onCloneFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
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
  error.value = ''
  status.value = ''

  const payload: RuntimeSettings = {
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
    files: { enabled: form.files.enabled, roots: form.files.roots.map((root) => root.trim()).filter((root) => root !== '') },
    // An empty string means unset, not a proxy named ""; otherwise this field blocks the whole save.
    web: {
      enabled: form.web.enabled,
      timeBudgetSeconds: form.web.timeBudgetSeconds,
      maxResults: form.web.maxResults,
      proxy: form.web.proxy === null || form.web.proxy.trim() === '' ? null : form.web.proxy.trim(),
      disabledEngines: [...form.web.disabledEngines],
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
    if (shellPrefs && payload.pet.enabled !== wasSpawned) {
      try {
        await setPetEnabled(payload.pet.enabled)
        petSpawned.value = payload.pet.enabled
      } catch (shellFailure) {
        shellPrefError.value = t('settings.general.petFailed', { error: failureText(shellFailure) })
      }
    }

    apply(saved.settings as RuntimeSettings & { model: { apiKeySet: boolean } })
    consumer.value = saved.consumer
    voice.value = saved.voice
    // The disabled list is stored now, so the engine rows have to be re-read to show it as it was saved.
    await refreshWeb()

    const at = new Date().toLocaleTimeString(locale.value)
    status.value = saved.compaction
      ? t('已保存 · {at} · {reason}', { at: at, reason: saved.compaction.reason })
      : t('已保存 · {at}', { at: at })

    // The composer's picker is fed from the shell, so it has to hear about the save — otherwise the gears
    // just edited here would not be the ones offered in the session view.
    emit('saved')
  } catch (failure) {
    error.value = String(failure)
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
  { key: 'model', label: 'settings.section.model', icon: 'sparkle' },
  { key: 'input', label: 'settings.section.input', icon: 'mic' },
  { key: 'output', label: 'settings.section.output', icon: 'speaker' },
  { key: 'desktop', label: 'settings.section.desktop', icon: 'crosshair' },
  { key: 'pet', label: 'settings.section.pet', icon: 'pet' },
  { key: 'files', label: 'settings.section.files', icon: 'folder' },
  { key: 'web', label: 'settings.section.web', icon: 'search' },
  { key: 'engines', label: 'settings.section.engines', icon: 'terminal' },
  { key: 'appearance', label: 'settings.section.appearance', icon: 'contrast' },
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

const section = ref('model')

/**
 * Which 「更多选项」 are open. Remembered, because the views are `v-if`: without it, opening the options you
 * actually use would have to be redone on every tab switch. Closed on a first visit — the point is that most
 * people do not need them. The model section has none: its three limits sit in the connection card.
 */
const advanced = ref<Record<string, boolean>>({})

/** A stored section that no longer exists (the pet tab in a build without it) falls back to the model page. */
function resolveSection(key: string): string {
  return visibleSections.value.some((item) => item.key === key) ? key : 'model'
}

function choose(key: string): void {
  section.value = key
  writeStored(SECTION_KEY, key)
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

/** Whether to offer the pet tab: false only in a shell built without the pet (see `petSupported`). */
const petAvailable = ref(true)

/** The sections actually offered: the pet tab only exists where the shell can honour it. */
const visibleSections = computed(() => SECTIONS.filter((item) => item.key !== 'pet' || petAvailable.value))

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
  shellPrefError.value = ''
  try {
    await resetPetPosition()
  } catch (error) {
    shellPrefError.value = t('重置位置失败：{error}', { error: error instanceof Error ? error.message : String(error) })
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
  section.value = readStored(SECTION_KEY, 'model', (value): value is string => typeof value === 'string' && SECTIONS.some((item) => item.key === value))
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
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="scroll settings-scroll">
      <div class="settings">
        <nav class="settings-nav" :aria-label="t('设置分类')">
          <button
            v-for="item in visibleSections"
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

        <div class="settings-body">
          <section v-show="section === 'general'" class="panel" data-test="settings-section-general">
            <header class="panel-head"><h2>{{ t('settings.section.general') }}</h2></header>
            <div class="g-card">
              <SettingRow :label="t('settings.general.language')" :hint="t('settings.general.languageHint')">
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

          <section v-show="section === 'input'" class="panel" data-test="settings-section-input">
            <header class="panel-head">
              <h2>{{ t('语音输入') }}</h2>
              <span v-if="wakeGateOn" class="g-chip is-accent" data-test="wake-mode">
                {{ t('唤醒模式') }}
                <HintTip align="end">{{ t('平时只听不转写；叫到唤醒词后的一段话才会转写，说完自动回到待命。') }}</HintTip>
              </span>
            </header>

            <div class="g-card">
              <div class="g-card-head"><h3>{{ t('麦克风') }}</h3></div>
              <SettingRow :label="t('开启语音输入')" :hint="t('关闭后会立刻释放麦克风，其他软件可以使用。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.input.enabled" data-test="input-enabled" />
              </SettingRow>
              <SettingRow :label="t('麦克风')" :hint="t('选你正在用的那个，名称支持模糊匹配。列表为空时可以手动输入。')">
                <select v-if="inputOptions.length > 0" v-model="form.input.deviceId" data-test="input-device">
                  <option :value="null">{{ t('（默认设备）') }}</option>
                  <option v-for="name in inputOptions" :key="name" :value="name">{{ name }}</option>
                </select>
                <input v-else type="text" v-model="form.input.deviceId" :placeholder="t('（默认设备）')" data-test="input-device" />
              </SettingRow>

              <p class="status-line" data-test="input-effect">
                <template v-if="voice?.input?.running">
                  {{ t('运行中 · {device} · 已识别 {count} 段', { device: voice.input.device, count: voice.input.count }) }}<template v-if="wakeGateOn"> · {{ voice.input.awake ? t('已唤醒') : t('等唤醒词') }}</template>
                </template>
                <template v-else-if="voice?.input">{{ t('未运行 · {reason}', { reason: voice.input.reason }) }}</template>
                <template v-else>{{ t('保存后可以看到运行状态') }}</template>
              </p>

              <!--
                电平条：死麦、安静房间、说话低于阈值三种情况在文字状态上长得一模一样，只有实时电平能分开。
                阈值刻度画在同一条上，因为「电平够不够得着阈值」正是用户要判断的那件事。
              -->
              <div v-if="voice?.input?.running" class="level" data-test="input-level">
                <div class="level-track">
                  <div class="level-fill" :style="{ width: levelPercent }"></div>
                  <div class="level-mark" :style="{ left: thresholdPercent }"></div>
                </div>
                <span class="level-readout">
                  {{ t('音量 {toFixed}', { toFixed: (voice.input.rms ?? 0).toFixed(4) }) }}
                  <HintTip align="end">
                    {{ t('竖线是「安静音量线」{silenceThreshold}：音量够不到它，就当作你没在说话。', { silenceThreshold: form.input.silenceThreshold }) }}
                    <template v-if="voice.input.thresholdMisconfigured">{{ t('环境底噪约 {toFixed}。', { toFixed: (voice.input.ambientRms ?? 0).toFixed(4) }) }}</template>
                  </HintTip>
                </span>
              </div>
              <p v-if="voice?.input?.thresholdMisconfigured" class="warn-line" data-test="input-threshold-warning">
                {{ t('安静音量线 {silenceThreshold} 不高于环境底噪 {toFixed}： 一直判断不出你说完了，一段话不会结束，也就不会有转写。', { silenceThreshold: form.input.silenceThreshold, toFixed: (voice.input.ambientRms ?? 0).toFixed(4) }) }}
              </p>
            </div>

            <div class="g-card">
              <div class="g-card-head"><h3>{{ t('唤醒') }}</h3></div>
              <SettingRow :label="t('唤醒词')" :disabled="!form.local.kws.enabled">
                <template #hint>
                  {{ t('用逗号分隔多个，例如「小克小克，小柯小柯」。留空则保留引擎里现有的词。需要先开启「本地引擎」里的唤醒与识别引擎。') }}
                </template>
                <input
                  type="text"
                  :value="keywordDrafts.kws"
                  :placeholder="t('小克小克，小柯小柯')"
                  data-test="local-kws-keywords"
                  @input="onEngineKeywordsInput('kws', $event)"
                />
              </SettingRow>
              <SettingRow :label="t('唤醒时叮一声')" :hint="t('听到唤醒词时响一下。需要同时开启语音输出。')" data-test="wake-tone">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.input.wakeTone" />
              </SettingRow>

              <SettingRow :label="t('说话时打断助手')" :hint="t('助手正在说话时，你开口说一段话（持续约半秒）就让它停下。没有回声消除，外放时助手自己的声音会被麦克风听见而打断自己，建议只在戴耳机时打开。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.input.bargeIn" data-test="barge-in" />
              </SettingRow>
            </div>

            <div class="g-card">
              <div class="g-card-head">
                <h3>{{ t('识别服务') }}</h3>
                <TestButton
                  kind="asr"
                  :label="t('测试')"
                  :testing="voiceTesting.asr === true"
                  :result="voiceTest.asr"
                  :stale="testStale('asr')"
                  :unsaved="dirty"
                  data-test="asr-test-row"
                  @test="testConnection('asr')"
                />
              </div>
              <SettingRow :label="t('接口类型')" :hint="t('按你的识别服务支持的方式选，不确定就看它的文档。')">
                <select v-model="form.asr.protocol" data-test="asr-protocol">
                  <option value="sensevoice">{{ t('本地') }}</option>
                  <option value="openai">{{ t('通用标准接口 · audio/transcriptions') }}</option>
                  <option v-if="form.asr.protocol === 'chat-audio'" value="chat-audio">{{ t('对话接口 · chat/completions（旧）') }}</option>
                </select>
              </SettingRow>
              <SettingRow :label="t('服务地址')" :hint="t('识别服务（ASR）的网址。留空则不会把语音转成文字。')">
                <input type="text" v-model="form.asr.baseUrl" placeholder="http://127.0.0.1:45101" data-test="asr-base-url" />
              </SettingRow>
              <SettingRow :label="t('模型')" :hint="t('识别服务里的模型名。')">
                <input type="text" v-model="form.asr.model" data-test="asr-model" />
              </SettingRow>
            </div>

            <div class="g-card">
              <div class="g-card-head"><h3>{{ t('断句') }}</h3></div>
              <SettingRow :label="t('多小声算安静')" :hint="t('音量低于这个值，就当作你没在说话。环境吵就调大，老是收不到你说话就调小。默认 0.003。')">
                <input type="number" step="0.001" min="0" v-model.number="form.input.silenceThreshold" />
              </SettingRow>
              <SettingRow :label="t('停顿多久算说完')" :unit="t('毫秒')" :hint="t('你停顿超过这个时间，就当这句话说完了。默认 1200，即 1.2 秒。')">
                <input type="number" v-model.number="form.input.utteranceGapMs" data-test="utterance-gap" />
              </SettingRow>
            </div>

            <button class="ghost more" :aria-expanded="advanced.input === true" data-test="settings-more-input" @click="toggleAdvanced('input')">
              <Icon name="chevron-down" :size="14" class="more-chevron" :class="{ 'is-open': advanced.input === true }" />
              {{ t('更多选项') }}
            </button>
            <div v-show="advanced.input === true" class="g-card">
              <div class="g-card-head"><h3>{{ t('断句') }}</h3></div>
              <SettingRow :label="t('唤醒后等你开口')" :unit="t('毫秒')" :hint="t('叫醒助手后，最多等你多久开口；一开口就改按「停顿多久算说完」判断。默认 5000，即 5 秒。')" data-test="wake-timeout">
                <input type="number" v-model.number="form.input.wakeTimeoutMs" />
              </SettingRow>
              <SettingRow :label="t('边说边识别')" :hint="t('开启后一句一句识别，反应更快；关闭则等整段说完再识别。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.input.asrStream" data-test="input-asr-stream" />
              </SettingRow>
              <SettingRow :label="t('句内停顿')" :unit="t('毫秒')" :hint="t('只在「边说边识别」开启时有用：停顿超过这个时间，就先识别前面那一句。默认 450。')">
                <input type="number" v-model.number="form.input.sentenceGapMs" />
              </SettingRow>
              <SettingRow :label="t('单次识别最长')" :unit="t('秒')" :hint="t('一次最多把多长的录音送去识别。默认 8 秒。')">
                <input type="number" v-model.number="form.input.windowSeconds" />
              </SettingRow>
              <div class="g-card-head"><h3>{{ t('识别服务') }}</h3></div>
              <SettingRow :label="t('语言')" :hint="t('告诉模型你说的是什么语言，如 Chinese。可以避免结果前面多出「language:」之类的前缀。')">
                <input type="text" v-model="form.asr.language" placeholder="Chinese" />
              </SettingRow>
              <SettingRow :label="t('连接等待')" :unit="t('毫秒')" :hint="t('连不上服务时，等多久放弃。')">
                <input type="number" v-model.number="form.asr.connectTimeoutMs" />
              </SettingRow>
              <SettingRow :label="t('单次等待')" :unit="t('毫秒')" :hint="t('设得比较短：卡住的那一段会整段重试，等太久会让录音堆积丢失。')">
                <input type="number" v-model.number="form.asr.readTimeoutMs" />
              </SettingRow>
              <SettingRow :label="t('采样率')" unit="Hz" :hint="t('录音的音质参数，一般不用改。默认 16000。')">
                <input type="number" v-model.number="form.asr.sampleRate" />
              </SettingRow>
            </div>
          </section>

          <section v-show="section === 'output'" class="panel" data-test="settings-section-output">
            <header class="panel-head"><h2>{{ t('语音输出') }}</h2></header>

            <div class="g-card">
              <div class="g-card-head"><h3>{{ t('朗读') }}</h3></div>
              <SettingRow :label="t('开启朗读')" :hint="t('助手的回复会念出来。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.output.enabled" data-test="output-enabled" />
              </SettingRow>
              <SettingRow :label="t('扬声器')" :hint="t('选声音从哪里出来。')">
                <select v-if="outputOptions.length > 0" v-model="form.output.deviceId" data-test="output-device">
                  <option :value="null">{{ t('（默认设备）') }}</option>
                  <option v-for="name in outputOptions" :key="name" :value="name">{{ name }}</option>
                </select>
                <input v-else type="text" v-model="form.output.deviceId" :placeholder="t('（默认设备）')" data-test="output-device" />
              </SettingRow>
              <SettingRow :label="t('语速')" :hint="t('1 是正常，0.8 慢一点，1.2 快一点。')">
                <input type="number" step="0.05" v-model.number="form.output.speed" />
              </SettingRow>
              <SettingRow :label="t('朗读时不听麦克风')" :hint="t('避免助手把自己的声音当成你在说话。建议保持开启。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.output.muteDuringTts" data-test="output-mute-during-tts" />
              </SettingRow>
              <SettingRow :label="t('最小句长')" :unit="t('字')" :hint="t('朗读时，断句点之间不足这个字数就连成一句，不单独断开。默认 10。')" data-test="min-sentence-chars">
                <input type="number" v-model.number="form.output.minSentenceChars" />
              </SettingRow>
              <SettingRow :label="t('无标点时多少字断开')" :unit="t('字')" :hint="t('整句没有标点时，超过这个字数就强行断句。默认 40。')" data-test="max-split-chars">
                <input type="number" v-model.number="form.output.maxSplitChars" />
              </SettingRow>
              <p class="status-line" data-test="output-effect">
                <template v-if="voice?.output.running">{{ t('运行中 · {device} · 已朗读 {count} 句', { device: voice.output.device, count: voice.output.count }) }}</template>
                <template v-else-if="voice">{{ t('未运行 · {reason}', { reason: voice.output.reason }) }}</template>
                <template v-else>{{ t('保存后可以看到运行状态') }}</template>
              </p>
            </div>

            <div class="g-card">
              <div class="g-card-head">
                <h3>{{ t('朗读服务') }}</h3>
                <TestButton
                  kind="tts"
                  :label="t('测试')"
                  :testing="voiceTesting.tts === true"
                  :result="voiceTest.tts"
                  :stale="testStale('tts')"
                  :unsaved="dirty"
                  data-test="tts-test-row"
                  @test="testConnection('tts')"
                />
              </div>
              <SettingRow :label="t('接口类型')" :hint="t('选和你的朗读服务一致的那种。')">
                <select v-model="form.tts.protocol" data-test="tts-protocol">
                  <option value="local">{{ t('本地') }}</option>
                  <option value="openai">{{ t('通用标准接口 · audio/speech') }}</option>
                  <option v-if="form.tts.protocol === 'native'" value="native">{{ t('本地服务 · 流式（旧）') }}</option>
                </select>
              </SettingRow>
              <SettingRow :label="t('服务地址')" :hint="t('朗读服务（TTS）的网址。留空则助手的回复不会被念出来。')">
                <input type="text" v-model="form.tts.baseUrl" placeholder="http://127.0.0.1:45102" data-test="tts-base-url" />
              </SettingRow>
              <template v-if="form.tts.protocol === 'native'">
                <SettingRow :label="t('语言')" :hint="t('本地服务用的语言代码，例如 zh。')">
                  <input type="text" v-model="form.tts.lang" />
                </SettingRow>
                <SettingRow :label="t('克隆音色的参考音频')" :hint="t('填本地音频文件的路径，朗读时会模仿这个声音。只在启动时读取一次。')">
                  <input type="text" v-model="form.tts.speakerRefPath" :placeholder="t('（不使用克隆）')" />
                </SettingRow>
              </template>
              <template v-else-if="form.tts.protocol === 'local'">
                <SettingRow :label="t('音色')" :hint="t('留空用引擎启动时的默认音色。也可以填 engines/tts/output/elaborate 里的音色名，例如 晓晓、心悦。')">
                  <input type="text" v-model="form.tts.voice" :placeholder="t('（引擎默认）')" data-test="tts-local-voice" />
                </SettingRow>
                <SettingRow :label="t('克隆音色')" :hint="t('选一段清晰的人声录音（.wav，几秒到十几秒），朗读时会模仿它的声音。选了之后，上面的音色名不再生效。')">
                  <div class="clone-pick">
                    <span class="clone-name" data-test="tts-clone-name">{{ cloneName || t('未选择') }}</span>
                    <input ref="cloneInput" type="file" accept=".wav,audio/wav,audio/x-wav" hidden data-test="tts-clone-file" @change="onCloneFile" />
                    <button class="ghost" type="button" data-test="tts-clone-pick" :disabled="cloneBusy" @click="cloneInput?.click()">{{ t('选择音频文件') }}</button>
                    <button v-if="form.tts.speakerRefPath" class="ghost" type="button" data-test="tts-clone-clear" @click="form.tts.speakerRefPath = null">{{ t('清除') }}</button>
                  </div>
                  <p v-if="cloneError" class="pkg-note" role="alert" data-test="tts-clone-error">{{ cloneError }}</p>
                </SettingRow>
                <SettingRow :label="t('参考文本')" :hint="t('参考录音里实际说的话，逐字填写。填了克隆更像；留空按零样本克隆，只取音色。填错会乱读。')">
                  <input type="text" v-model="form.tts.speakerRefText" :placeholder="t('（留空 = 零样本克隆）')" data-test="tts-clone-ref-text" />
                </SettingRow>
              </template>
              <template v-else-if="form.tts.protocol === 'openai'">
                <SettingRow :label="t('模型')" :hint="t('例如 tts-1。')">
                  <input type="text" v-model="form.tts.model" placeholder="tts-1" />
                </SettingRow>
                <SettingRow :label="t('音色')" :hint="t('服务提供的音色名，例如 alloy。')">
                  <input type="text" v-model="form.tts.voice" placeholder="alloy" />
                </SettingRow>
                <SettingRow :label="t('语速')" :hint="t('这个接口自己的语速参数。')">
                  <input type="number" step="0.05" v-model.number="form.tts.speed" />
                </SettingRow>
              </template>
              <audio v-if="testTtsUrl" controls :src="testTtsUrl" data-test="tts-test-audio" class="tts-test-audio" />
            </div>

            <button class="ghost more" :aria-expanded="advanced.output === true" data-test="settings-more-output" @click="toggleAdvanced('output')">
              <Icon name="chevron-down" :size="14" class="more-chevron" :class="{ 'is-open': advanced.output === true }" />
              {{ t('更多选项') }}
            </button>
            <div v-show="advanced.output === true" class="g-card">
              <SettingRow :label="t('句间停顿')" :unit="t('毫秒')" :hint="t('两段话之间加入的停顿。用克隆音色时固定为 0。')">
                <input type="number" v-model.number="form.output.gapMs" />
              </SettingRow>
              <SettingRow :label="t('念完后再静音')" :unit="t('毫秒')" :hint="t('朗读结束后继续不听麦克风的时间，避免尾音被听进去。默认 400。')">
                <input type="number" v-model.number="form.output.drainMs" />
              </SettingRow>
              <SettingRow :label="t('播放采样率')" unit="Hz" :hint="t('必须和朗读服务输出的一致，否则声音会变快或变慢。')">
                <input type="number" v-model.number="form.output.sampleRate" />
              </SettingRow>
              <SettingRow :label="t('服务返回采样率')" unit="Hz" :hint="t('必须和服务实际返回的一致，否则播放速度会不对。')">
                <input type="number" v-model.number="form.tts.sampleRate" />
              </SettingRow>
              <SettingRow :label="t('最长等待')" :unit="t('秒')" :hint="t('超过这个时间还没合成完，就放弃这一句。')">
                <input type="number" v-model.number="form.tts.timeoutSeconds" />
              </SettingRow>
              <SettingRow :label="t('分段大小')" :unit="t('毫秒')" :hint="t('边合成边播放时每一小段的长度，一般不用改。')">
                <input type="number" v-model.number="form.tts.chunkMs" />
              </SettingRow>
            </div>
          </section>

          <section v-show="section === 'desktop'" class="panel" data-test="settings-section-desktop">
            <header class="panel-head"><h2>{{ t('电脑操控') }}</h2></header>

            <!-- The one sentence on this page that is not behind an ⓘ. A hover is a good place for a footnote and a
                 bad place for the fact that this capability acts on your computer. -->
            <p class="callout" data-test="desktop-callout">
              {{ t('开启后，助手可以观察你的窗口，并申请控制鼠标键盘。每一步都需要你批准。') }}
            </p>

            <div class="g-card">
              <SettingRow :label="t('允许助手操作电脑')">
                <template #hint>
                  {{ t('默认关闭。开启后助手能看你的窗口，并申请点击、按键；每个动作都要你点「允许」，没人回应就当拒绝。关闭时助手根本拿不到这些工具。') }}
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.desktop.enabled" data-test="desktop-enabled" />
              </SettingRow>
              <p v-if="desktopAgentOff" class="warn-line" data-test="desktop-agent-warning">
                {{ t('还需要启用「桌面操控」子 agent，助手才能使用这个能力。') }}
                <button class="ghost" data-test="desktop-agent-fix" @click="emit('navigate', 'prompts')">{{ t('去启用') }}</button>
              </p>
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

          <section v-if="petAvailable" v-show="section === 'pet'" class="panel" data-test="settings-section-pet">
            <header class="panel-head">
              <h2>{{ t('settings.section.pet') }}</h2>
              <span class="g-chip" :class="form.pet.enabled ? 'is-ok' : ''" data-test="pet-effect">
                {{ form.pet.enabled ? t('已开启') : t('已关闭') }}
              </span>
              <HintTip align="end">
                {{ t('桌宠只读取会话里已经定稿的助手回复，按关键词判断情绪，不参与对话，也不会额外请求模型。') }}
              </HintTip>
            </header>

            <div class="g-card">
              <SettingRow :label="t('启用桌宠')">
                <template #hint>
                  {{ t('开启后，桌面上会出现一个 Live2D 角色。它只读取会话日志里已经定稿的回复，不参与对话，也不额外请求模型。') }}
                </template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.pet.enabled" data-test="pet-enabled" />
              </SettingRow>
              <SettingRow :label="t('置顶')" :hint="t('开启后桌宠始终在其他窗口之上；关掉它可以被别的窗口盖住。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.pet.alwaysOnTop" data-test="pet-always-on-top" />
              </SettingRow>
              <SettingRow :label="t('透明区域鼠标穿透')">
                <template #hint>{{ t('开启后，点击角色没画到的透明部分会落到后面的窗口上；关闭则整块窗口都吃点击。') }}</template>
                <input type="checkbox" class="g-switch" role="switch" v-model="form.pet.clickThroughTransparent" data-test="pet-click-through" />
              </SettingRow>
              <SettingRow :label="t('播放动作音效')" :hint="t('动作自带音效时播放。默认关闭，免得桌宠突然出声。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.pet.motionSound" data-test="pet-motion-sound" />
              </SettingRow>
            </div>

            <div class="g-card" data-test="pet-core">
              <div class="g-card-head">
                <h3>
                  Cubism Core
                  <HintTip>
                    {{ t('Live2D 的运行时，不随应用分发：需要你自己从 Live2D 官网下载 Cubism 5 SDK for Web R5，并同意它的协议。只接受 CubismSdkForWeb-5-r.5.zip，整包哈希会逐字节校验。') }}
                  </HintTip>
                </h3>
                <span class="g-chip" :class="petCore?.installed ? 'is-ok' : 'is-warn'" data-test="pet-core-state">
                  <template v-if="petCore?.installed">{{ t('已安装 R{sdk} · {hash}', { sdk: petCore.sdk, hash: petCoreHash }) }}</template>
                  <template v-else>{{ t('未安装') }}</template>
                </span>
              </div>

              <SettingRow :label="t('下载地址')" :hint="t('在浏览器里打开，下载 R5 的 zip。导入之后它只留在本机，仓库和安装包都不含它。')">
                <code class="pkg-root" data-test="pet-core-url">{{ PET_CORE_URL }}</code>
                <button class="ghost" type="button" data-test="pet-core-copy" @click="copyText(PET_CORE_URL, t('已复制下载地址'))">{{ t('复制') }}</button>
              </SettingRow>

              <SettingRow :label="t('我已从 Live2D 官网下载并同意其协议')">
                <template #hint>{{ t('这是导入的前提：Core 只由你自己下载，本应用不分发、不镜像。') }}</template>
                <input type="checkbox" class="g-switch" role="switch" v-model="petCoreConsent" data-test="pet-core-consent" />
              </SettingRow>

              <SettingRow :label="t('Core 压缩包')" :hint="t('填 CubismSdkForWeb-5-r.5.zip 的完整路径，然后点导入。')">
                <div class="pkg-actions">
                  <input
                    type="text"
                    v-model="petCorePath"
                    :placeholder="t('例如 D:/Downloads/CubismSdkForWeb-5-r.5.zip')"
                    data-test="pet-core-path"
                    @keydown.enter.prevent="importPetCore"
                  />
                  <button class="primary" type="button" data-test="pet-core-import" :disabled="!petCoreConsent || petCoreBusy || petCorePath.trim() === ''" @click="importPetCore">
                    {{ petCoreBusy ? t('导入中…') : t('导入') }}
                  </button>
                </div>
              </SettingRow>
              <p v-if="petCoreError" class="warn-line" role="alert" data-test="pet-core-error">{{ petCoreError }}</p>
            </div>

            <div class="g-card" data-test="pet-models">
              <div class="g-card-head">
                <h3>{{ t('模型') }}</h3>
                <button class="ghost icon-only" type="button" :title="t('重新读取')" :aria-label="t('重新读取模型列表')" data-test="pet-models-refresh" @click="refreshPetModels">
                  <Icon name="refresh" :size="14" /><span class="sr-only">{{ t('刷新') }}</span>
                </button>
              </div>

              <!-- Said in a sentence rather than behind an ⓘ: whether the app carries models at all is not a
                   footnote, and "it did not work" reads very differently depending on the answer. -->
              <p class="callout" data-test="pet-models-notice">
                {{ t('模型不随应用分发，版权属于各自的作者。仓库和安装包都不含任何模型，你自己导入的只留在本机。') }}
              </p>

              <p v-if="petModels.length === 0" class="muted" data-test="pet-models-empty">{{ t('还没有导入模型。') }}</p>
              <div v-for="model in petModels" :key="model.id" class="pet-model-row" :data-test="`pet-model-${model.id}`">
                <span class="pet-model-name" :title="model.name">
                  {{ model.name }}
                  <span v-if="model.id === form.pet.activeModelId" class="engine-tag">{{ t('当前') }}</span>
                </span>
                <span class="pet-model-meta" data-test="pet-model-meta">
                  {{ t('{count} 个动作 · {expressions} 个表情 · {size}', { count: model.motions.length, expressions: model.expressions.length, size: formatBytes(model.bytes) }) }}
                  · {{ model.hasHitAreas ? t('有点击区域') : t('没有点击区域') }}
                </span>
                <button
                  class="ghost"
                  type="button"
                  :disabled="model.id === form.pet.activeModelId"
                  :data-test="`pet-model-current-${model.id}`"
                  @click="form.pet.activeModelId = model.id"
                >{{ t('设为当前') }}</button>
                <button class="ghost" type="button" :data-test="`pet-model-delete-${model.id}`" @click="deletePetModel(model.id)">
                  <Icon name="trash" :size="14" /> {{ petArmedModel === model.id ? t('确认删除') : t('删除') }}
                </button>
              </div>
              <p v-if="petModelsError" class="warn-line" role="alert" data-test="pet-models-error">{{ petModelsError }}</p>

              <SettingRow :label="t('导入模型')" :hint="t('填模型所在的文件夹，或者一个 zip 的完整路径。只复制 model3.json 引用到的文件。')">
                <div class="pkg-actions">
                  <input
                    type="text"
                    v-model="petModelPath"
                    :placeholder="t('例如 D:/Live2D/猫猫 或 D:/models/cat.zip')"
                    data-test="pet-model-path"
                    @keydown.enter.prevent="importPetModel()"
                  />
                  <button class="primary" type="button" data-test="pet-model-import" :disabled="petImportBusy || petModelPath.trim() === ''" @click="importPetModel()">
                    {{ petImportBusy ? t('导入中…') : t('导入') }}
                  </button>
                </div>
              </SettingRow>

              <!-- Several model3.json in one archive: the server reports the relative paths and imports nothing
                   until one is named. -->
              <div v-if="petCandidates.length > 0" class="pet-candidates" data-test="pet-model-candidates">
                <p class="row-note">{{ t('这个包里有一个以上的 model3.json，选一个要导入的：') }}</p>
                <SettingRow :label="t('model3.json')">
                  <select v-model="petCandidateChoice" data-test="pet-model-candidate">
                    <option v-for="candidate in petCandidates" :key="candidate" :value="candidate">{{ candidate }}</option>
                  </select>
                  <button class="primary" type="button" data-test="pet-model-candidate-confirm" @click="importPetModel(petCandidateChoice)">{{ t('导入这个') }}</button>
                </SettingRow>
              </div>
              <p v-if="petImportError" class="warn-line" role="alert" data-test="pet-model-error">{{ petImportError }}</p>
            </div>

            <div class="g-card" data-test="pet-appearance">
              <div class="g-card-head"><h3>{{ t('外观') }}</h3></div>
              <SettingRow :label="t('缩放')" unit="%">
                <template #hint>{{ t('50%–200%，乘以模型的画布尺寸得到窗口大小，保存后立即生效。') }}</template>
                <input
                  type="range"
                  min="50"
                  max="200"
                  step="5"
                  :value="petScalePercent"
                  :aria-label="t('缩放')"
                  data-test="pet-scale"
                  @input="setScale"
                />
                <span class="pet-readout" data-test="pet-scale-readout">{{ petScalePercent }}%</span>
              </SettingRow>
              <!-- Immediate, like the other shell commands: where the window sits is not a setting in
                   runtime.json, so there is nothing here for SaveBar to save. -->
              <SettingRow v-if="shellPrefs" :label="t('重置位置')">
                <template #hint>{{ t('把桌宠放回第一次打开时的位置。拖动过之后想归位就点它。') }}</template>
                <button class="ghost" type="button" data-test="pet-reset-position" @click="resetPetWindow">{{ t('重置位置') }}</button>
              </SettingRow>
            </div>

            <div class="g-card" data-test="pet-mapping">
              <div class="g-card-head">
                <h3>
                  {{ t('动作映射') }}
                  <HintTip>
                    {{ t('每个动作先按这张表找模型的 motion，找不到就用程序化参数轨道，参数也不存在就跳过。改的是当前模型，换模型各记各的。') }}
                  </HintTip>
                </h3>
                <span v-if="activePetModel" class="g-chip" data-test="pet-mapping-model">{{ activePetModel.name }}</span>
              </div>

              <p v-if="activePetModel === null" class="muted" data-test="pet-mapping-nomodel">
                {{ t('还没有选当前模型。导入一个模型并「设为当前」之后，这里才会列出它的动作和表情。') }}
              </p>

              <template v-else>
                <SettingRow v-for="action in PET_ACTIONS" :key="action" :label="actionLabel(action)" :data-test="`pet-action-${action}`">
                  <select
                    :value="motionValue(activeProfile.motions[action])"
                    :aria-label="actionLabel(action)"
                    :data-test="`pet-action-select-${action}`"
                    @change="setMotion(action, ($event.target as HTMLSelectElement).value)"
                  >
                    <option v-for="option in withOption(motionOptions(), motionValue(activeProfile.motions[action]))" :key="option.value" :value="option.value">
                      {{ option.label }}
                    </option>
                  </select>
                  <button
                    class="g-btn"
                    type="button"
                    :disabled="!petSpawned"
                    :title="petSpawned ? undefined : t('桌宠没有打开，先在「通用」里启用')"
                    :data-test="`pet-action-preview-${action}`"
                    @click="previewAction(action)"
                  >
                    {{ t('预览') }}
                  </button>
                </SettingRow>

                <SettingRow v-for="emotion in PET_EMOTIONS" :key="`expression-${emotion}`" :label="t('表情：{emotion}', { emotion: emotionLabel(emotion) })">
                  <select
                    :value="expressionValue(activeProfile.expressions[emotion])"
                    :aria-label="t('表情：{emotion}', { emotion: emotionLabel(emotion) })"
                    :data-test="`pet-expression-select-${emotion}`"
                    @change="setExpression(emotion, ($event.target as HTMLSelectElement).value)"
                  >
                    <option v-for="option in withOption(expressionOptions(), expressionValue(activeProfile.expressions[emotion]))" :key="option.value" :value="option.value">
                      {{ option.label }}
                    </option>
                  </select>
                  <button
                    class="g-btn"
                    type="button"
                    :disabled="!petSpawned"
                    :title="petSpawned ? undefined : t('桌宠没有打开，先在「通用」里启用')"
                    :data-test="`pet-expression-preview-${emotion}`"
                    @click="previewExpression(emotion)"
                  >
                    {{ t('预览') }}
                  </button>
                </SettingRow>

                <!-- Only meaningful without the model's own hit areas: with them the model says where its head
                     and face are, and these percentages would be a second opinion nobody asked for. -->
                <template v-if="activePetModel.hasHitAreas === false">
                  <SettingRow :label="t('头部下沿')" unit="%">
                    <template #hint>{{ t('没有点击区域时，按模型高度的这个比例切分头部。默认 35%。') }}</template>
                    <input type="range" min="0" max="100" step="1" :value="Math.round(activeProfile.headBottom * 100)" data-test="pet-head-bottom" @input="setHeadBottom" />
                    <span class="pet-readout" data-test="pet-head-bottom-readout">{{ Math.round(activeProfile.headBottom * 100) }}%</span>
                  </SettingRow>
                  <SettingRow :label="t('面部下沿')" unit="%">
                    <template #hint>{{ t('这个比例以下是面部，其余是身体。默认 55%，必须不小于头部下沿。') }}</template>
                    <input type="range" min="0" max="100" step="1" :value="Math.round(activeProfile.faceBottom * 100)" data-test="pet-face-bottom" @input="setFaceBottom" />
                    <span class="pet-readout" data-test="pet-face-bottom-readout">{{ Math.round(activeProfile.faceBottom * 100) }}%</span>
                  </SettingRow>
                </template>
              </template>
            </div>

            <div class="g-card" data-test="pet-behaviour">
              <div class="g-card-head"><h3>{{ t('行为') }}</h3></div>
              <SettingRow :label="t('待机动作间隔')" :unit="t('秒')">
                <template #hint>{{ t('安静这么久之后随机做一个待机动作。下限 5 秒，上限不超过 3600 秒，且不能小于下限。') }}</template>
                <span class="pet-range">
                  <input type="number" min="5" max="3600" v-model.number="form.pet.idleMinSeconds" :aria-label="t('待机动作最短间隔')" data-test="pet-idle-min" />
                  <span class="pet-range-sep">–</span>
                  <input type="number" min="5" max="3600" v-model.number="form.pet.idleMaxSeconds" :aria-label="t('待机动作最长间隔')" data-test="pet-idle-max" />
                </span>
              </SettingRow>
              <SettingRow :label="t('多久没互动就睡觉')" :unit="t('分钟')" :hint="t('0 表示不睡。睡着后点一下就会醒。最多 1440 分钟。')">
                <input type="number" min="0" max="1440" v-model.number="form.pet.sleepAfterMinutes" data-test="pet-sleep-minutes" />
              </SettingRow>
              <SettingRow :label="t('问候时间')">
                <template #hint>{{ t('用逗号分隔的 HH:mm，一天各触发一次，最多 12 个。留空则不主动打招呼。') }}</template>
                <input
                  type="text"
                  :value="form.pet.greetingTimes.join(',')"
                  :placeholder="t('09:00, 14:00')"
                  data-test="pet-greeting-times"
                  @input="setGreetingTimes"
                />
              </SettingRow>
              <SettingRow :label="t('情绪冷却')" :unit="t('秒')" :hint="t('同一个情绪在这段时间内不会重复触发，免得连续几句都播同一个动作。0–600 秒。')">
                <input type="number" min="0" max="600" v-model.number="form.pet.emotionCooldownSeconds" data-test="pet-emotion-cooldown" />
              </SettingRow>
              <SettingRow :label="t('表情保持')" :unit="t('秒')" :hint="t('表情在这么多秒后回到默认。1–120 秒。')">
                <input type="number" min="1" max="120" v-model.number="form.pet.expressionHoldSeconds" data-test="pet-expression-hold" />
              </SettingRow>
              <SettingRow :label="t('快速拖拽阈值')" unit="px/s">
                <template #hint>{{ t('松手速度超过这个值算「甩出去」，会播惊讶和回弹。100–20000 物理像素每秒。') }}</template>
                <input type="number" min="100" max="20000" v-model.number="form.pet.fastDragPxPerSecond" data-test="pet-fast-drag" />
              </SettingRow>
            </div>

            <div class="g-card" data-test="pet-emotions">
              <div class="g-card-head">
                <h3>
                  {{ t('情绪字典') }}
                  <HintTip>
                    {{ t('桌宠按这张表给每条回复打分：每种情绪取命中关键词里的最高权重作为主分，主分最高的胜出。同一个函数也给下面的试算打分，所以试算结果就是真实结果。') }}
                  </HintTip>
                </h3>
                <button class="ghost" type="button" data-test="pet-emotions-restore" @click="restoreDefaultDictionary">{{ t('恢复默认') }}</button>
              </div>

              <div v-for="emotion in PET_EMOTIONS" :key="`dict-${emotion}`" class="pet-dict">
                <h4 class="pet-dict-head">{{ emotionLabel(emotion) }}</h4>
                <div v-for="(entry, index) in keywordsOf(emotion)" :key="index" class="pet-keyword-row">
                  <input
                    type="text"
                    :value="entry.text"
                    maxlength="32"
                    :aria-label="t('第 {v} 个关键词', { v: index + 1 })"
                    :data-test="`pet-keyword-${emotion}`"
                    @input="setKeywordText(emotion, index, $event)"
                  />
                  <input
                    type="number"
                    min="1"
                    max="10"
                    :value="entry.weight"
                    :aria-label="t('第 {v} 个关键词的权重', { v: index + 1 })"
                    :data-test="`pet-keyword-weight-${emotion}`"
                    @input="setKeywordWeight(emotion, index, $event)"
                  />
                  <button class="ghost icon-only" type="button" :title="t('移除')" :aria-label="t('移除第 {v} 个关键词', { v: index + 1 })" :data-test="`pet-keyword-remove-${emotion}`" @click="removeKeyword(emotion, index)">
                    <Icon name="x" :size="14" /><span class="sr-only">{{ t('移除') }}</span>
                  </button>
                </div>
                <button class="ghost add" type="button" :data-test="`pet-keyword-add-${emotion}`" @click="addKeyword(emotion)">
                  <Icon name="plus" :size="14" /> {{ t('添加关键词') }}
                </button>
              </div>

              <SettingRow :label="t('否定前缀')">
                <template #hint>{{ t('命中位置前两个字符内出现这些词，这次命中作废，所以「不开心」不算开心。逗号分隔，最多 32 个，每个不超过 8 个字符。') }}</template>
                <input type="text" :value="form.pet.emotions.negationPrefixes.join(',')" :placeholder="t('不, 没, 别, not')" data-test="pet-negations" @input="setNegationPrefixes" />
              </SettingRow>
              <SettingRow :label="t('最低权重')" :hint="t('命中权重要达到这个值才算数，1–10。默认 1：只要有命中就触发。')">
                <input type="number" min="1" max="10" :value="form.pet.emotions.minWeight" data-test="pet-min-weight" @input="setMinWeight" />
              </SettingRow>

              <SettingRow :label="t('试算')" stack :hint="t('粘贴一段回复，用上面这张表实时打分。不会保存，也不会发给桌宠。')">
                <textarea v-model="petTrialText" rows="2" :placeholder="t('例如：哈哈，这个太好笑了，不过被你夸得有点害羞')" data-test="pet-trial-input" />
                <div class="pet-trial" data-test="pet-trial-result">
                  <template v-if="petTrial === null">
                    <span class="muted">{{ t('输入一段文字就能看到结果。') }}</span>
                  </template>
                  <template v-else>
                    <span class="g-chip" :class="petTrial.emotion === null ? '' : 'is-ok'" data-test="pet-trial-winner">
                      {{ petTrial.emotion === null ? t('没有命中任何情绪') : t('胜出：{emotion}', { emotion: emotionLabel(petTrial.emotion) }) }}
                    </span>
                    <span v-for="emotion in PET_EMOTIONS" :key="`trial-${emotion}`" class="pet-trial-score" :data-test="`pet-trial-score-${emotion}`">
                      {{ emotionLabel(emotion) }} {{ primaryScore(petTrial, emotion) }} / {{ petTrial.totals[emotion] }}
                    </span>
                    <span v-if="petTrial.hits.length > 0" class="pet-trial-hits" data-test="pet-trial-hits">
                      {{ t('命中：{hits}', { hits: petTrial.hits.map((hit) => `${hit.text}@${hit.at}`).join(t('、')) }) }}
                    </span>
                  </template>
                </div>
              </SettingRow>
            </div>

            <div class="g-card" data-test="pet-notices">
              <div class="g-card-head"><h3>{{ t('声明') }}</h3></div>
              <p class="row-note" data-test="pet-notice-attribution">
                {{ t('桌宠使用 Live2D Cubism。Cubism Core 的版权归 Live2D Inc.，由你自行下载并同意其协议；Cubism Framework 随桌宠前端分发，适用 Live2D Open Software License。') }}
              </p>
              <p class="row-note">
                <span class="pet-link-label">{{ t('Open Software License') }}</span>
                <code class="pkg-root" data-test="pet-license-open">{{ PET_LICENSE_URLS.open }}</code>
                <button class="ghost" type="button" data-test="pet-license-open-copy" @click="copyText(PET_LICENSE_URLS.open, t('已复制链接'))">{{ t('复制') }}</button>
              </p>
              <p class="row-note">
                <span class="pet-link-label">{{ t('Proprietary Software License') }}</span>
                <code class="pkg-root" data-test="pet-license-proprietary">{{ PET_LICENSE_URLS.proprietary }}</code>
                <button class="ghost" type="button" data-test="pet-license-proprietary-copy" @click="copyText(PET_LICENSE_URLS.proprietary, t('已复制链接'))">{{ t('复制') }}</button>
              </p>
              <p class="row-note" data-test="pet-notice-third-party">
                {{ t('其他第三方组件与完整许可清单见安装目录下的 THIRD-PARTY-NOTICES.md（notices/THIRD-PARTY-NOTICES.md）。') }}
              </p>
            </div>
          </section>

          <section v-show="section === 'files'" class="panel" data-test="settings-section-files">
            <header class="panel-head"><h2>{{ t('文件访问') }}</h2></header>

            <div class="g-card">
              <SettingRow :label="t('允许助手读取文件')">
                <template #hint>
                  <span data-test="files-whitelist-hint">{{ t('只读，默认关闭。助手只能读下面列出的文件夹和里面的所有内容，其他位置它看不到。') }}</span>
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
              </div>

              <p v-if="webView === null" class="muted" data-test="web-engines-unknown">
                {{ t('读不到引擎状态。开关照常保存，状态等后端可用后再刷新。') }}
              </p>
              <p v-else-if="webView.engines.length === 0" class="muted" data-test="web-engines-empty">
                {{ t('这一版没有内置任何搜索引擎。') }}
              </p>
              <div
                v-for="engine in webView?.engines ?? []"
                :key="engine.id"
                class="engine-row"
                :data-test="`web-engine-${engine.id}`"
              >
                <span class="engine-name" :title="engine.name">
                  {{ engine.name }}
                  <span v-if="engine.singleSite" class="engine-tag" :title="t('只索引自己这一个站点')">{{ t('单一站点') }}</span>
                </span>
                <span class="engine-kinds">{{ engine.kinds.map(kindLabel).join(t('、')) }}</span>
                <span class="engine-state" :class="engineStateClass(engine)" data-test="web-engine-state">{{ engineState(engine) }}</span>
                <input
                  type="checkbox"
                  class="g-switch"
                  role="switch"
                  :aria-label="t('使用 {name}', { name: engine.name })"
                  :checked="!form.web.disabledEngines.includes(engine.id)"
                  :data-test="`web-engine-${engine.id}-enabled`"
                  @change="toggleEngine(engine.id)"
                />
              </div>
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

          <section v-show="section === 'engines'" class="panel" data-test="settings-section-engines">
            <header class="panel-head">
              <h2>{{ t('本地引擎') }}</h2>
              <span class="g-chip">{{ t('进阶') }}</span>
              <HintTip align="end">
                {{ t('由 Partner 帮你启动和关闭的本地语音程序。关闭会真正结束进程、释放显存和内存；只是不再请求，并不会释放。 识别和朗读服务的地址仍在各自页面填，指向这里设置的端口。') }}
              </HintTip>
            </header>

            <div v-if="enginePackage" class="g-card" data-test="engine-package">
              <div class="g-card-head">
                <h3>
                  {{ t('引擎包') }}
                  <HintTip>
                    {{ t('引擎的运行环境和模型（约 3GB）不在安装包里，放在应用目录下的 engines 文件夹。 下载完成后会自动填好下面的设置；已经有一份的话，用「手动指定」指到它所在的文件夹。') }}
                  </HintTip>
                </h3>
                <button class="ghost" type="button" data-test="engine-detect" :disabled="downloadRunning" @click="detectEngines">{{ t('检测') }}</button>
              </div>

              <SettingRow v-for="engine in (['tts', 'kws'] as const)" :key="engine" :label="t(ENGINE_NAMES[engine])">
                <template #hint>
                  <span v-if="!enginePackage[engine].installed" :data-test="`engine-missing-${engine}`">
                    {{ t('缺少：{items}', { items: enginePackage[engine].missing.slice(0, 3).join(t('、')) }) }}{{ enginePackage[engine].missing.length > 3 ? t(' 等 {count} 项', { count: enginePackage[engine].missing.length }) : '' }}
                  </span>
                </template>
                <span class="g-chip" :class="enginePackage[engine].installed ? 'is-ok' : ''" :data-test="`engine-state-${engine}`">
                  {{ enginePackage[engine].installed ? t('已就绪') : t('未安装') }}
                </span>
              </SettingRow>

              <SettingRow :label="t('位置')" :hint="enginePackage.customRoot ? t('手动指定的位置。') : t('应用目录下的默认位置。')">
                <code class="pkg-root" data-test="engine-root">{{ enginePackage.root }}</code>
              </SettingRow>

              <template v-if="!allEnginesInstalled || enginePackage.customRoot">
                <SettingRow v-if="!allEnginesInstalled" :label="t('下载')" :hint="t('下载缺少的引擎和模型到上面的位置，支持断点续传。')">
                  <div class="pkg-actions">
                    <button
                      v-if="!downloadRunning"
                      class="primary"
                      type="button"
                      data-test="engine-download"
                      @click="startEngineDownload"
                    >{{ t('下载引擎和模型') }}</button>
                    <template v-else>
                      <span class="pkg-progress" role="status" data-test="engine-progress">{{ downloadText }}</span>
                      <button class="ghost" type="button" data-test="engine-cancel" @click="cancelEngineDownload">{{ t('取消') }}</button>
                    </template>
                  </div>
                </SettingRow>

                <SettingRow :label="t('手动指定')" :hint="t('引擎和模型已经在别处时，填它们所在的文件夹（里面是 tts 和 kws-asr）。留空确认则回到默认位置。')">
                  <div class="pkg-actions">
                    <button
                      v-if="!engineManualOpen"
                      class="ghost"
                      type="button"
                      data-test="engine-manual"
                      :disabled="downloadRunning"
                      @click="engineManualOpen = true; engineManualRoot = enginePackage.customRoot ? enginePackage.root : ''"
                    >{{ t('手动指定…') }}</button>
                    <template v-else>
                      <input type="text" v-model="engineManualRoot" :placeholder="t('例如 D:\\engines')" data-test="engine-manual-root" @keydown.enter.prevent="applyEngineRoot" />
                      <button class="primary" type="button" data-test="engine-manual-apply" @click="applyEngineRoot">{{ t('确定') }}</button>
                      <button class="ghost" type="button" @click="engineManualOpen = false">{{ t('取消') }}</button>
                    </template>
                  </div>
                </SettingRow>
              </template>

              <p v-if="enginePackageNote" class="pkg-note" role="status" data-test="engine-note">{{ enginePackageNote }}</p>
            </div>

            <template v-for="engine in (['tts', 'kws'] as const)" :key="engine">
            <div v-if="showEngine(engine)" class="g-card" :data-test="`local-engine-${engine}`">
              <div class="g-card-head">
                <h3>
                  {{ engine === 'tts' ? t('朗读引擎') : t('唤醒与识别引擎') }}
                  <HintTip>{{ engine === 'tts' ? t('例如 qwentts.cpp server。') : t('例如 sherpa-onnx。开启后语音输入进入「唤醒模式」。') }}</HintTip>
                </h3>
              </div>
              <SettingRow :label="t('由 Partner 启动')" :hint="t('开启后，启动时自动运行这个程序，关闭时自动结束。')">
                <input type="checkbox" class="g-switch" role="switch" v-model="form.local[engine].enabled" :data-test="`local-${engine}-enabled`" />
              </SettingRow>
              <SettingRow :label="t('程序路径')" :hint="t('要启动的程序的完整路径。')">
                <input type="text" v-model="form.local[engine].command" :data-test="`local-${engine}-command`" />
              </SettingRow>
              <SettingRow :label="t('启动参数')">
                <template #hint>{{ t('用空格分隔；端口用') }} <code>{port}</code> {{ t('代替。') }}</template>
                <input
                  type="text"
                  :value="form.local[engine].arguments.join(' ')"
                  :data-test="`local-${engine}-arguments`"
                  @input="onEngineArgumentsInput(engine, $event)"
                />
              </SettingRow>
              <SettingRow :label="t('运行目录')" :hint="t('程序启动时所在的文件夹。留空则不指定。')">
                <input type="text" v-model="form.local[engine].workingDirectory" />
              </SettingRow>
              <SettingRow :label="t('端口')" :hint="t('本地服务监听的端口。')">
                <input type="number" v-model.number="form.local[engine].port" :data-test="`local-${engine}-port`" />
              </SettingRow>
              <SettingRow :label="t('启动等待')" :unit="t('秒')" :hint="t('启动后最多等多久，还没准备好就算失败。')">
                <input type="number" v-model.number="form.local[engine].readyTimeoutSeconds" />
              </SettingRow>
            </div>
            </template>
          </section>

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
        </div>
      </div>
    </div>

    <SaveBar
      prefix="settings"
      :dirty="dirty"
      :status="status"
      :error="error"
      can-discard
      @save="persist"
      @discard="refresh"
    />
  </div>
</template>

<style scoped>
.settings-scroll {
  flex: 1;
  min-block-size: 0;
}

/* Two columns: the categories, and one category at a time. The nav sticks under the top bar so switching never
   needs a scroll back up. */
.settings {
  display: grid;
  grid-template-columns: 11rem minmax(0, var(--column-form));
  gap: var(--space-6);
  justify-content: center;
  padding: calc(var(--topbar-h) + var(--space-4)) var(--space-5) 7rem;
}

.settings-nav {
  position: sticky;
  inset-block-start: calc(var(--topbar-h) + var(--space-4));
  align-self: start;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-1);
}

.nav-item {
  justify-content: flex-start;
  gap: var(--space-3);
  min-block-size: 2.5rem;
  padding: 0 var(--space-3);
  border-color: transparent;
  background: transparent;
  box-shadow: none;
  color: var(--text-2);
}

.nav-item:hover:not(:disabled) {
  background: var(--surface-2);
  color: var(--text);
}

.nav-item.active {
  background: var(--accent-soft);
  color: var(--accent-pressed);
  font-weight: 500;
}

.live-dot {
  margin-inline-start: auto;
  inline-size: 6px;
  block-size: 6px;
  border-radius: 50%;
  background: var(--ok);
}

.settings-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  min-inline-size: 0;
}

.panel {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-5);
  align-content: start;
  min-inline-size: 0;
}

.panel-head {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.panel-head h2 {
  font-size: var(--fs-title);
  margin-inline-end: auto;
}

/* A grouped list in a glass card: a title row, then rows that separate themselves with a hairline. */
.g-card {
  background: var(--glass-1);
  border: 1px solid var(--glass-edge);
  border-radius: var(--radius-md);
  box-shadow: var(--glass-hi), var(--shadow-1);
  padding: var(--space-2) var(--space-5) var(--space-3);
  min-inline-size: 0;
}

.g-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  min-block-size: 3rem;
  padding-block-start: var(--space-1);
}

.g-card-head h3 {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  font-size: 0.8125rem;
  color: var(--text-2);
}

.row-note {
  display: flex;
  gap: var(--space-3);
  margin: 0;
  padding-block-end: var(--space-2);
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.status-line {
  margin: 0;
  padding-block: var(--space-2);
  border-block-start: 1px solid var(--glass-edge);
  color: var(--text-2);
  font-size: var(--fs-meta);
}

.warn-line {
  margin: 0 0 var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--warn) 12%, transparent);
  color: var(--warn);
  font-size: var(--fs-meta);
}

.warn-line button {
  min-block-size: 1.75rem;
  font-size: var(--fs-meta);
}

.version-chip {
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}

.update-line {
  margin: 0;
  padding-block: var(--space-2);
  font-size: var(--fs-ui);
}

.update-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-3);
  padding-block: var(--space-2);
  border-block-start: 1px solid var(--glass-edge);
}

.update-size {
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.hint-line {
  margin: 0;
  padding-block: var(--space-2);
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.callout {
  margin: 0;
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--warn) 12%, transparent);
  color: var(--text);
  font-size: var(--fs-ui);
}

.more {
  justify-self: start;
  color: var(--text-2);
}

.more-chevron {
  transition: transform var(--motion-fast) var(--ease);
  transform: rotate(-90deg);
}

.more-chevron.is-open {
  transform: none;
}

.icon-only {
  flex: none;
  inline-size: 2.25rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.level {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding-block: var(--space-2) var(--space-1);
}

.level-track {
  position: relative;
  flex: 1 1 auto;
  block-size: 0.5rem;
  border-radius: var(--radius-pill);
  background: var(--surface-2);
  overflow: hidden;
}

.level-fill {
  block-size: 100%;
  border-radius: var(--radius-pill);
  background: var(--accent);
  transition: width 120ms linear;
}

/* The threshold: a level that cannot reach it is silence, and silence that is never detected means an utterance
   that never ends. */
.level-mark {
  position: absolute;
  inset-block: 0;
  inline-size: 2px;
  background: var(--text);
  opacity: 0.55;
}

.level-readout {
  flex: none;
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  font-variant-numeric: tabular-nums;
  font-size: var(--fs-meta);
  color: var(--text-2);
}

.tts-test-audio {
  inline-size: 100%;
  block-size: 2.25rem;
  margin-block: var(--space-2);
}

.folders {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
  padding-block: var(--space-3) var(--space-2);
  border-block-start: 1px solid var(--glass-edge);
}

.folders-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.folder-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.folder-row input {
  flex: 1;
  min-inline-size: 0;
}

.folder-icon {
  flex: none;
  color: var(--text-3);
}

.add {
  justify-self: start;
}

/* One line per engine: who it is, what it indexes, how it is doing, and the switch. The state is the part
   that matters — an engine that is quietly paused is why a search came back thin. */
.engine-row {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-block-size: 2.5rem;
  padding-block: var(--space-1);
  border-block-start: 1px solid var(--glass-edge);
}

.engine-name {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  flex: none;
  max-inline-size: 12rem;
  font-size: var(--fs-ui);
  color: var(--text);
}

.engine-tag {
  flex: none;
  padding: 0 var(--space-2);
  border-radius: var(--radius-pill);
  background: var(--surface-2);
  color: var(--text-3);
  font-size: 0.6875rem;
}

.engine-kinds {
  flex: 1 1 auto;
  min-inline-size: 0;
  color: var(--text-3);
  font-size: var(--fs-meta);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.engine-state {
  flex: none;
  max-inline-size: 14rem;
  font-size: var(--fs-meta);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--text-2);
}

.engine-state.is-ok {
  color: var(--text-3);
}

.engine-state.is-warn {
  color: var(--warn);
}

.engine-state.is-off {
  color: var(--text-3);
  opacity: 0.7;
}

.pkg-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-2);
  min-inline-size: 0;
}

.pkg-actions input[type='text'] {
  flex: 1 1 14rem;
  min-inline-size: 0;
}

.pkg-root {
  overflow-wrap: anywhere;
  color: var(--text-2);
}

.pkg-progress {
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
}

.clone-pick {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex-wrap: wrap;
  justify-content: flex-end;
}

.clone-name {
  color: var(--text-2);
  overflow-wrap: anywhere;
}

.pkg-note {
  margin: var(--space-2) 0 0;
  color: var(--text-2);
  overflow-wrap: anywhere;
}

/* One imported model on a line: name, what it offers, and the two verbs that act on it. The metadata is the
   part that decides whether a model is worth using, so it sits between them rather than in a tooltip. */
.pet-model-row {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-block-size: 2.5rem;
  padding-block: var(--space-1);
  border-block-start: 1px solid var(--glass-edge);
}

.pet-model-name {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  flex: none;
  max-inline-size: 12rem;
  color: var(--text);
  font-size: var(--fs-ui);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.pet-model-meta {
  flex: 1 1 auto;
  min-inline-size: 0;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.pet-candidates {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
  padding-block-end: var(--space-2);
}

/* A percentage that changes as the slider moves: tabular figures keep it from jittering. */
.pet-readout {
  flex: none;
  min-inline-size: 3.5rem;
  text-align: end;
  color: var(--text-2);
  font-variant-numeric: tabular-nums;
  font-size: var(--fs-meta);
}

.pet-range {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}

.pet-range input {
  inline-size: 6rem;
}

.pet-range-sep {
  color: var(--text-3);
}

.pet-dict {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
  padding-block: var(--space-3) var(--space-2);
  border-block-start: 1px solid var(--glass-edge);
}

.pet-dict-head {
  color: var(--text-2);
  font-size: var(--fs-meta);
}

.pet-keyword-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.pet-keyword-row input[type='text'] {
  flex: 1 1 auto;
  min-inline-size: 0;
}

.pet-keyword-row input[type='number'] {
  flex: none;
  inline-size: 5rem;
}

.pet-trial {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-2);
  padding-block-start: var(--space-2);
}

.pet-trial-score {
  color: var(--text-2);
  font-size: var(--fs-meta);
  font-variant-numeric: tabular-nums;
}

.pet-trial-hits {
  color: var(--text-3);
  font-size: var(--fs-meta);
  overflow-wrap: anywhere;
}

.pet-link-label {
  min-inline-size: 11rem;
  color: var(--text-2);
  font-size: var(--fs-meta);
}

@container body (max-width: 52rem) {
  .settings {
    grid-template-columns: minmax(0, 1fr);
    gap: var(--space-4);
  }

  .settings-nav {
    position: static;
    display: flex;
    overflow-x: auto;
    padding-block-end: var(--space-1);
  }

  .nav-item {
    flex: none;
    border-radius: var(--radius-pill);
  }

  .live-dot {
    margin-inline-start: var(--space-1);
  }
}
</style>
