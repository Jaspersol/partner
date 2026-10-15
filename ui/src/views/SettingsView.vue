<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import Icon from '../components/Icon.vue'
import type { ConsumerState, DeviceList, PartnerApiSurface, RuntimeSettings, VoiceState, VoiceTestResult } from '../api'


/**
 * Fallback menu, used only until the server answers. The stored list is what this page edits, so a
 * hardcoded copy used as the *saved* value would quietly re-add gears the user removed.
 */
const DEFAULT_REASONING_GEARS = ['off', 'low', 'medium', 'high', 'xhigh', 'max']
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string]; saved: [] }>()

/** The form is a projection of the stored settings; nothing here is authoritative until it is saved. */
const form = reactive<RuntimeSettings>({
  input: {
    enabled: false,
    deviceId: null,
    silenceThreshold: 0.03,
    utteranceGapMs: 1200,
    sentenceGapMs: 450,
    windowSeconds: 8,
    asrStream: false,
    maxSplitChars: 40,
    wakeTone: true,
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
  },
  asr: { baseUrl: null, protocol: 'chat-audio', model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
  tts: { baseUrl: null, protocol: 'native', model: null, voice: null, speed: 1, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
  local: {
    tts: { enabled: false, command: null, arguments: [] as string[], workingDirectory: null, port: 45102, readyTimeoutSeconds: 30, keywords: [] as string[] },
    kws: { enabled: false, command: null, arguments: [] as string[], workingDirectory: null, port: 45103, readyTimeoutSeconds: 30, keywords: [] as string[] },
  },
  model: { baseUrl: null, model: null, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096, supportsImages: false, reasoningEffort: 'medium', reasoningGears: [...DEFAULT_REASONING_GEARS] },
  desktop: { enabled: false, leaseTtlMs: 15000, approval: 'all', allowedWindowTitles: [] as string[] },

  // Carried through untouched so the two capability pages' state survives a save made here. Not editable on
  // this page: a server row and a skill row need more room than a card in a settings form gives them.
  mcp: { enabled: false, servers: [], allowedTools: [] },
  skills: { disabled: [] },
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

/** Wake words are edited as one comma line; 中英混排按序存，引擎端只对能转拼音的生效. */
/** 输入框显示的原始串。按逗号切分回数组再 join 回显会让尾随逗号被切掉又拼没，打不进第二个词——
 *  所以编辑期间整串保真，切分只发生在保存时。 */
const keywordDrafts = ref<Record<'tts' | 'kws', string>>({ tts: '', kws: '' })

function onEngineKeywordsInput(engine: 'tts' | 'kws', event: Event) {
  const raw = (event.target as HTMLInputElement).value
  keywordDrafts.value = { ...keywordDrafts.value, [engine]: raw }
  form.local[engine].keywords = parseKeywords(raw)
}

/** 逗号分隔串 → 词表。中英文逗号都算分隔符；首尾空白丢弃，空段不入表。 */
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

/** TTS 测试的音频（base64 WAV → blob URL）。结果被替换或组件卸载时回收。 */
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

/** 电平条上限：RMS 0.2 已接近满幅说话，再往上没有分辨意义。 */
const LEVEL_CEILING = 0.2

function levelWidth(value: number): string {
  const ratio = Math.min(1, Math.max(0, value / LEVEL_CEILING))
  return `${(ratio * 100).toFixed(1)}%`
}

const levelPercent = computed(() => levelWidth(voice.value?.input.rms ?? 0))
const thresholdPercent = computed(() => levelWidth(form.input.silenceThreshold ?? 0))

/** 唤醒门只在本地引擎托管开启且端口合法时生效（后端同一条件），否则「等唤醒词」是句假话。 */
const wakeGateOn = computed(() => form.local.kws.enabled === true && (form.local.kws.port ?? 0) > 0)


const modelConfigured = computed(() => (form.model.baseUrl ?? '') !== '' && (form.model.model ?? '') !== '')
const keyHint = computed(() =>
  apiKeySet.value ? '已设置 · 留空保持不变，输入新值即替换' : '未设置（本地服务通常不需要）',
)

/** A stored value that no longer enumerates must still be selectable, or the setting would silently change. */
function withStored(available: string[], stored: string | null): string[] {
  return stored !== null && stored !== '' && !available.includes(stored) ? [stored, ...available] : available
}

async function probeModels() {
  probing.value = true
  modelProbeError.value = ''

  try {
    const probe = await props.api.listModels(form.model.baseUrl ?? undefined)
    models.value = probe.models
    modelProbeError.value = probe.error ?? ''
  } catch (failure) {
    models.value = []
    modelProbeError.value = String(failure)
  } finally {
    probing.value = false
  }
}

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
  keywordDrafts.value = { tts: form.local.tts.keywords.join(','), kws: form.local.kws.keywords.join(',') }
  Object.assign(form.desktop, {
    enabled: settings.desktop?.enabled ?? false,
    leaseTtlMs: settings.desktop?.leaseTtlMs ?? 15000,
    approval: settings.desktop?.approval ?? 'all',
    allowedWindowTitles: settings.desktop?.allowedWindowTitles ?? [],
  })
  Object.assign(form.model, {
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
  })

  // Replaced, not merged: the response is the whole truth, and holding on to what arrived earlier would
  // resurrect a server or a deny entry the person has since removed on their own page.
  form.mcp = { ...settings.mcp, servers: [...(settings.mcp?.servers ?? [])], allowedTools: [...(settings.mcp?.allowedTools ?? [])] }
  form.skills = { disabled: [...(settings.skills?.disabled ?? [])] }

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
    emit('context', '设置 · 运行时')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function persist() {
  error.value = ''
  status.value = ''

  const payload: RuntimeSettings = {
    desktop: { ...form.desktop },
    input: { ...form.input },
    output: { ...form.output },
    asr: { ...form.asr },
    tts: { ...form.tts },
    local: {
      tts: { ...form.local.tts, arguments: [...form.local.tts.arguments], keywords: [...form.local.tts.keywords] },
      kws: { ...form.local.kws, arguments: [...form.local.kws.arguments], keywords: [...form.local.kws.keywords] },
    },
    // Carried, not edited: these two have pages of their own. Dropping them here would silently undo whatever
    // was decided there the next time anybody saved a voice setting.
    mcp: form.mcp,
    skills: form.skills,
    model: {
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
    },
  }

  // Only an edited key is sent: an untouched field must not clear what is stored.
  if (apiKeyEdited.value) {
    payload.model.apiKey = apiKeyDraft.value
  }

  try {
    // The API is the source of truth: show what it stored, not what was typed.
    const saved = await props.api.saveSettings(payload)
    apply(saved.settings as RuntimeSettings & { model: { apiKeySet: boolean } })
    consumer.value = saved.consumer
    voice.value = saved.voice

    const at = new Date().toLocaleTimeString()
    status.value = saved.compaction
      ? `已保存 · ${at} · ${saved.compaction.reason}`
      : `已保存 · ${at}`

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
 * The menu is edited as an ordered list because that is the order the composer offers it in. Blanks and
 * repeats are dropped as each row is committed, so the row the user is looking at is the row that will be
 * stored.
 */
function normalizeGear(value: string): string {
  return value.trim().toLowerCase()
}

function addGear() {
  form.model.reasoningGears = [...form.model.reasoningGears, '']
}

function removeGear(index: number) {
  form.model.reasoningGears = form.model.reasoningGears.filter((_, at) => at !== index)
}

function commitGear(index: number, value: string) {
  const gears = [...form.model.reasoningGears]
  const gear = normalizeGear(value)

  if (gear === '') {
    gears.splice(index, 1)
  } else {
    gears[index] = gear
  }

  // A repeat is a menu entry the picker can never land on. The row being edited wins and the earlier copy
  // goes: keeping the copy the user did not touch would silently discard the value they just typed.
  form.model.reasoningGears = gears.filter(
    (entry, at) => at === index || (entry !== gear && gears.indexOf(entry) === at),
  )
}

/** Moves a row, so the menu order can be arranged without retyping the whole list. */
function moveGear(index: number, delta: number) {
  const target = index + delta
  const gears = [...form.model.reasoningGears]
  if (target < 0 || target >= gears.length) {
    return
  }

  const [gear] = gears.splice(index, 1)
  gears.splice(target, 0, gear)
  form.model.reasoningGears = gears
}

function useAsDefault(index: number) {
  form.model.reasoningEffort = normalizeGear(form.model.reasoningGears[index] ?? '')
}

/**
 * Which groups are folded away.
 *
 * <para>
 * Persisted, because the views are `v-if`: without it, folding the three cards nobody touches would have to be
 * redone on every tab switch, and a control that forgets itself is worse than no control.
 * </para>
 *
 * <para>
 * Everything is open on a first visit. Folding by default would shorten the page but hide five of six groups
 * from someone who came to change one thing — and the reader is the only one who knows which thing.
 * </para>
 */
const GROUPS = ['input', 'output', 'asr', 'tts', 'local', 'desktop', 'model'] as const
const FOLDED_KEY = 'partner.settings.folded'

const folded = ref<Record<string, boolean>>({})

function readFolded(): Record<string, boolean> {
  try {
    const raw = window.localStorage.getItem(FOLDED_KEY)
    if (raw === null) return {}
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null) return {}
    // Only known names: a stale key from a build whose groups differed must not come back as a state.
    const known: Record<string, boolean> = {}
    for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
      if ((GROUPS as readonly string[]).includes(key) && typeof value === 'boolean') known[key] = value
    }
    return known
  } catch {
    return {}
  }
}

function writeFolded(): void {
  try {
    window.localStorage.setItem(FOLDED_KEY, JSON.stringify(folded.value))
  } catch {
    // A sandbox without storage still folds; it just forgets.
  }
}

function toggleGroup(id: string): void {
  folded.value = { ...folded.value, [id]: folded.value[id] !== true }
  writeFolded()
}

function setAll(collapsed: boolean): void {
  folded.value = Object.fromEntries(GROUPS.map((id) => [id, collapsed]))
  writeFolded()
}

const allFolded = computed(() => GROUPS.every((id) => folded.value[id] === true))

onMounted(() => {
  folded.value = readFolded()
  void refresh()
})

/** Asked by the shell before it switches away: the views are `v-if`, so leaving discards every edit. */
function hasUnsavedChanges(): boolean {
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="settings-reload" @click="refresh">重载</button>
      <button class="ghost" data-test="settings-fold-all" @click="setAll(!allFolded)">
        {{ allFolded ? '展开全部' : '折叠全部' }}
      </button>
      <span class="spacer" />
      <!--
        Unsaved changes are said out loud, because this page is now save-on-button and the button is at the
        bottom of a long form. Without this the reader finds out by switching tabs — the views are `v-if`, so
        that discards the edits silently, turning one surprise (some switches saving, some not) into another
        (none of them saving, and no word about it).
      -->
      <span v-if="dirty" class="muted" data-test="settings-dirty">有未保存的修改</span>
      <span class="muted" data-test="settings-status">{{ status }}</span>
    </div>

    <div class="scroll">
      <div class="cards">
        <section class="card" :class="{ 'is-folded': folded.input === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-input'"
              :aria-expanded="folded.input !== true"
              aria-controls="settings-body-input"
              @click="toggleGroup('input')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              语音输入
            </button>
          </h2>
          <div v-show="folded.input !== true" :id="'settings-body-input'" class="card-body">
          <p class="card-note">关闭会立即释放麦克风设备（不保留占用）。</p>
          <label class="field">
            <span>启用麦克风输入</span>
            <input type="checkbox" v-model="form.input.enabled" data-test="input-enabled" />
          </label>
          <label class="field">
            <span>静音阈值 (0-1)</span>
            <input type="number" step="0.01" min="0" v-model.number="form.input.silenceThreshold" />
            <span class="source">低于此电平视为静音；实测默认 0.03</span>
          </label>
          <label class="field" data-test="wake-tone">
            <span>唤醒提示音</span>
            <input type="checkbox" v-model="form.input.wakeTone" />
            <span class="source">唤醒词命中时「叮」一声（需语音输出开启）</span>
          </label>
          <label class="field">
            <span>整句间隔 (ms)</span>
            <input type="number" v-model.number="form.input.utteranceGapMs" data-test="utterance-gap" />
            <span class="source">停顿超过此值切句；实测默认 1200</span>
          </label>
          <label class="field">
            <span>句间间隔 (ms)</span>
            <input type="number" v-model.number="form.input.sentenceGapMs" />
          </label>
          <label class="field">
            <span>分析窗口 (s)</span>
            <input type="number" v-model.number="form.input.windowSeconds" />
            <span class="source">环形缓冲长度；实测默认 8</span>
          </label>
          <label class="field">
            <span>无标点强切长度（字）</span>
            <input type="number" v-model.number="form.input.maxSplitChars" />
          </label>
          <label class="field">
            <span>流式识别</span>
            <input type="checkbox" v-model="form.input.asrStream" data-test="input-asr-stream" />
          </label>
          <label class="field">
            <span>输入设备</span>
            <select v-if="inputOptions.length > 0" v-model="form.input.deviceId" data-test="input-device">
              <option :value="null">（默认设备）</option>
              <option v-for="name in inputOptions" :key="name" :value="name">{{ name }}</option>
            </select>
            <input v-else type="text" v-model="form.input.deviceId" placeholder="（默认设备）" data-test="input-device" />
            <span class="source">按名称模糊匹配；枚举不到设备时可手填</span>
          </label>

          <p class="card-note" data-test="input-effect">
            <template v-if="voice?.input?.running">
              运行中 · {{ voice.input.device }} · 已识别 {{ voice.input.count }} 段<template
                v-if="wakeGateOn"
              > · {{ voice.input.awake ? '已唤醒' : '等唤醒词' }}</template>
            </template>
            <template v-else-if="voice?.input">未运行 · {{ voice.input.reason }}</template>
            <template v-else>（保存后查看运行状态）</template>
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
              电平 {{ (voice.input.rms ?? 0).toFixed(4) }} · 阈值 {{ form.input.silenceThreshold }}
              <template v-if="voice.input.thresholdMisconfigured"> · 底噪 {{ (voice.input.ambientRms ?? 0).toFixed(4) }}</template>
            </span>
          </div>
          <p
            v-if="voice?.input?.thresholdMisconfigured"
            class="card-note error"
            data-test="input-threshold-warning"
          >
            阈值 {{ form.input.silenceThreshold }} 不高于环境底噪 {{ (voice.input.ambientRms ?? 0).toFixed(4) }}：
            静音永远判不出来，一段话不会结束（也就不会有转写）。
          </p>
          </div>
        </section>

        <section class="card" :class="{ 'is-folded': folded.output === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-output'"
              :aria-expanded="folded.output !== true"
              aria-controls="settings-body-output"
              @click="toggleGroup('output')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              语音输出
            </button>
          </h2>
          <div v-show="folded.output !== true" :id="'settings-body-output'" class="card-body">
          <p class="card-note">播放期间停听麦克风，避免把自身语音当成输入。</p>
          <label class="field">
            <span>启用语音输出</span>
            <input type="checkbox" v-model="form.output.enabled" data-test="output-enabled" />
          </label>
          <label class="field">
            <span>音色</span>
            <input type="text" v-model="form.output.voice" placeholder="（服务默认音色）" />
          </label>
          <label class="field">
            <span>语速</span>
            <input type="number" step="0.05" v-model.number="form.output.speed" />
          </label>
          <label class="field">
            <span>段间静音 (ms)</span>
            <input type="number" v-model.number="form.output.gapMs" />
          </label>
          <label class="field">
            <span>播放期停听 (drain, ms)</span>
            <input type="number" v-model.number="form.output.drainMs" />
            <span class="source">播放结束后继续停听的时长；实测默认 400</span>
          </label>
          <label class="field">
            <span>采样率</span>
            <input type="number" v-model.number="form.output.sampleRate" />
            <span class="source">必须与 TTS 服务输出一致</span>
          </label>
          <label class="field">
            <span>播放时停听麦克风</span>
            <input type="checkbox" v-model="form.output.muteDuringTts" data-test="output-mute-during-tts" />
          </label>
          <label class="field">
            <span>输出设备</span>
            <select v-if="outputOptions.length > 0" v-model="form.output.deviceId" data-test="output-device">
              <option :value="null">（默认设备）</option>
              <option v-for="name in outputOptions" :key="name" :value="name">{{ name }}</option>
            </select>
            <input v-else type="text" v-model="form.output.deviceId" placeholder="（默认设备）" data-test="output-device" />
          </label>

          <p class="card-note" data-test="output-effect">
            <template v-if="voice?.output.running">运行中 · {{ voice.output.device }} · 已朗读 {{ voice.output.count }} 句</template>
            <template v-else-if="voice">未运行 · {{ voice.output.reason }}</template>
            <template v-else>（保存后查看运行状态）</template>
          </p>
          </div>
        </section>

        <section class="card" :class="{ 'is-folded': folded.asr === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-asr'"
              :aria-expanded="folded.asr !== true"
              aria-controls="settings-body-asr"
              @click="toggleGroup('asr')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              语音识别（ASR）
            </button>
          </h2>
          <div v-show="folded.asr !== true" :id="'settings-body-asr'" class="card-body">
          <p class="card-note">识别端点。留空表示不配置——此时麦克风输入不会转写。</p>
          <label class="field">
            <span>协议</span>
            <select v-model="form.asr.protocol" data-test="asr-protocol">
              <option value="chat-audio">chat-audio（chat/completions 内联音频）</option>
              <option value="openai">openai（标准 /audio/transcriptions）</option>
              <option value="sensevoice">sensevoice（本地引擎 /recognize）</option>
            </select>
          </label>
          <label class="field">
            <span>服务地址</span>
            <input type="text" v-model="form.asr.baseUrl" placeholder="http://127.0.0.1:45101" data-test="asr-base-url" />
          </label>
          <label class="field">
            <span>模型</span>
            <input type="text" v-model="form.asr.model" data-test="asr-model" />
          </label>
          <label class="field">
            <span>语言</span>
            <input type="text" v-model="form.asr.language" placeholder="Chinese" />
            <span class="source">预填为助手回合，让模型不吐 language 前缀</span>
          </label>
          <label class="field">
            <span>连接超时 (ms)</span>
            <input type="number" v-model.number="form.asr.connectTimeoutMs" />
          </label>
          <label class="field">
            <span>读取超时 (ms)</span>
            <input type="number" v-model.number="form.asr.readTimeoutMs" />
            <span class="source">故意短：卡住的块要整块重试，长停顿会冲垮环形缓冲</span>
          </label>
          <label class="field">
            <span>采样率</span>
            <input type="number" v-model.number="form.asr.sampleRate" />
          </label>
          <div class="row card-footer" data-test="asr-test-row">
            <button class="ghost" :disabled="voiceTesting.asr" data-test="test-asr" @click="testConnection('asr')">
              {{ voiceTesting.asr ? '测试中…' : '测试' }}
            </button>
            <span v-if="voiceTest.asr" class="break" data-test="test-asr-result" :class="voiceTest.asr.ok ? 'muted' : 'error'">
              {{ voiceTest.asr.ok ? '✓' : '✗' }} {{ voiceTest.asr.detail }}（{{ voiceTest.asr.latencyMs }}ms）
            </span>
          </div>
          </div>
        </section>

        <section class="card" :class="{ 'is-folded': folded.tts === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-tts'"
              :aria-expanded="folded.tts !== true"
              aria-controls="settings-body-tts"
              @click="toggleGroup('tts')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              语音合成（TTS）
            </button>
          </h2>
          <div v-show="folded.tts !== true" :id="'settings-body-tts'" class="card-body">
          <p class="card-note">合成端点。留空表示不配置——此时助手回复不会朗读。</p>
          <label class="field">
            <span>协议</span>
            <select v-model="form.tts.protocol" data-test="tts-protocol">
              <option value="native">native（服务端 /tts，流式 PCM）</option>
              <option value="openai">openai（标准 /audio/speech）</option>
            </select>
          </label>
          <label class="field">
            <span>服务地址</span>
            <input type="text" v-model="form.tts.baseUrl" placeholder="http://127.0.0.1:45102" data-test="tts-base-url" />
          </label>
          <label class="field">
            <span>语言</span>
            <input type="text" v-model="form.tts.lang" />
            <span class="source">native 协议的 lang 字段；openai 协议不用</span>
          </label>
          <label class="field">
            <span>模型</span>
            <input type="text" v-model="form.tts.model" placeholder="tts-1" />
            <span class="source">openai 协议的模型名；native 协议不用</span>
          </label>
          <label class="field">
            <span>音色 (voice)</span>
            <input type="text" v-model="form.tts.voice" placeholder="alloy" />
            <span class="source">openai 协议的音色名；native 协议用克隆参考音频</span>
          </label>
          <label class="field">
            <span>语速 (speed)</span>
            <input type="number" step="0.05" v-model.number="form.tts.speed" />
            <span class="source">openai 协议的语速；native 协议不用</span>
          </label>
          <label class="field">
            <span>采样率</span>
            <input type="number" v-model.number="form.tts.sampleRate" />
            <span class="source">必须与输出段一致，否则播放速度会错</span>
          </label>
          <label class="field">
            <span>音色克隆参考音频</span>
            <input type="text" v-model="form.tts.speakerRefPath" placeholder="（不使用克隆）" />
            <span class="source">本地路径；构建客户端时读取一次</span>
          </label>
          <label class="field">
            <span>请求超时 (s)</span>
            <input type="number" v-model.number="form.tts.timeoutSeconds" />
          </label>
          <label class="field">
            <span>播放块大小 (ms)</span>
            <input type="number" v-model.number="form.tts.chunkMs" />
          </label>
          <div class="row card-footer" data-test="tts-test-row">
            <button class="ghost" :disabled="voiceTesting.tts" data-test="test-tts" @click="testConnection('tts')">
              {{ voiceTesting.tts ? '测试中…' : '测试' }}
            </button>
            <span v-if="voiceTest.tts" class="break" data-test="test-tts-result" :class="voiceTest.tts.ok ? 'muted' : 'error'">
              {{ voiceTest.tts.ok ? '✓' : '✗' }} {{ voiceTest.tts.detail }}（{{ voiceTest.tts.latencyMs }}ms）
            </span>
            <audio v-if="testTtsUrl" controls :src="testTtsUrl" data-test="tts-test-audio" class="tts-test-audio" />
          </div>
          </div>
        </section>

        <section class="card" :class="{ 'is-folded': folded.local === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-local'"
              :aria-expanded="folded.local !== true"
              aria-controls="settings-body-local"
              @click="toggleGroup('local')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              本地引擎进程
            </button>
          </h2>
          <div v-show="folded.local !== true" :id="'settings-body-local'" class="card-body">
          <p class="card-note">
            由运行时拉起并停掉的本地语音服务进程。关闭 = 终止进程 = 真正释放显存/内存——
            只是不发请求不会释放占用。服务地址照常填在 ASR/TTS 卡里指向这组端口。
            KWS 引擎开启后，语音输入进入<b>唤醒门控</b>：平时只听不转写，
            唤醒词命中后一段话内走 ASR，说完自动回监听。
          </p>
          <template v-for="engine in ['tts', 'kws'] as const" :key="engine">
            <div class="field-group" :data-test="`local-engine-${engine}`">
              <p class="field-head">
                <strong>{{ engine === 'tts' ? 'TTS 服务（如 qwentts.cpp server）' : 'KWS+ASR 服务（如 sherpa-onnx）' }}</strong>
              </p>
              <label class="field">
                <span>启用托管</span>
                <input type="checkbox" v-model="form.local[engine].enabled" :data-test="`local-${engine}-enabled`" />
              </label>
              <label class="field">
                <span>可执行文件</span>
                <input type="text" v-model="form.local[engine].command" :data-test="`local-${engine}-command`" />
              </label>
              <label class="field">
                <span>参数（一行分隔）</span>
                <input type="text" :value="form.local[engine].arguments.join(' ')"
                  :data-test="`local-${engine}-arguments`"
                  @input="onEngineArgumentsInput(engine, $event)" />
                <span class="source">端口占位符写作 {port}</span>
              </label>
              <label class="field">
                <span>工作目录</span>
                <input type="text" v-model="form.local[engine].workingDirectory" />
              </label>
              <label class="field">
                <span>端口</span>
                <input type="number" v-model.number="form.local[engine].port" :data-test="`local-${engine}-port`" />
              </label>
              <label class="field">
                <span>就绪宽限 (s)</span>
                <input type="number" v-model.number="form.local[engine].readyTimeoutSeconds" />
              </label>
              <label class="field" v-if="engine === 'kws'">
                <span>唤醒词（逗号分隔）</span>
                <input type="text" v-model="keywordDrafts[engine]"
                  :data-test="`local-${engine}-keywords`"
                  placeholder="你好军哥，蛋哥蛋哥"
                  @input="onEngineKeywordsInput(engine, $event)" />
                <span class="source">留空 = 保留引擎当前词表；保存后下发，引擎转拼音并立即生效</span>
              </label>
            </div>
          </template>
          <div class="row card-footer" data-test="kws-test-row">
            <button class="ghost" :disabled="voiceTesting.kws" data-test="test-kws" @click="testConnection('kws')">
              {{ voiceTesting.kws ? '测试中…' : '测试' }}
            </button>
            <span v-if="voiceTest.kws" class="break" data-test="test-kws-result" :class="voiceTest.kws.ok ? 'muted' : 'error'">
              {{ voiceTest.kws.ok ? '✓' : '✗' }} {{ voiceTest.kws.detail }}（{{ voiceTest.kws.latencyMs }}ms）
            </span>
          </div>
          </div>
        </section>

        <section class="card" :class="{ 'is-folded': folded.desktop === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-desktop'"
              :aria-expanded="folded.desktop !== true"
              aria-controls="settings-body-desktop"
              @click="toggleGroup('desktop')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              桌面（M9）
            </button>
          </h2>
          <div v-show="folded.desktop !== true" :id="'settings-body-desktop'" class="card-body">
          <p class="card-note">
            本仓库里唯一<strong>不可逆</strong>的能力：打开后模型可以观察窗口，并请求移动指针或发按键。即使打开，
            <strong>每个动作都要人工批准</strong>（会话里弹出「允许 / 拒绝」，无人应答即拒绝），所以打开只是让模型
            能问，不是让它能做。关闭时工具根本不注册给模型。
          </p>
          <label class="field">
            <span>启用桌面能力</span>
            <input type="checkbox" v-model="form.desktop.enabled" data-test="desktop-enabled" />
          </label>
          <label class="field">
            <span>租约有效期（毫秒）</span>
            <input
              type="number"
              min="1000"
              v-model.number="form.desktop.leaseTtlMs"
              data-test="desktop-lease-ttl"
            />
          </label>
          <label class="field">
            <span>允许的窗口标题（逗号分隔，空=不限）</span>
            <input
              type="text"
              :value="form.desktop.allowedWindowTitles.join('、')"
              placeholder="记事本、计算器"
              data-test="desktop-allowed-titles"
              @input="onTitlesInput"
            />
          </label>
          <p class="muted">窗口标题子串、不区分大小写。真正拦住动作的是批准；这个列表只是把安装范围收窄。</p>
          </div>
        </section>

        <section class="card" :class="{ 'is-folded': folded.model === true }">
          <h2>
            <button
              class="group-toggle"
              :data-test="'settings-group-model'"
              :aria-expanded="folded.model !== true"
              aria-controls="settings-body-model"
              @click="toggleGroup('model')"
            >
              <Icon name="chevron-down" :size="14" class="group-chevron" />
              模型（对话）
            </button>
          </h2>
          <div v-show="folded.model !== true" :id="'settings-body-model'" class="card-body">
          <p class="card-note">
            这里是执行队列的那个模型。保存后会<strong>立即重启消费者</strong>；留空则退回环境变量
            <code>PARTNER_LLM_BASE_URL</code> / <code>PARTNER_LLM_MODEL</code>。
          </p>
          <label class="field">
            <span>服务地址</span>
            <input type="text" v-model="form.model.baseUrl" placeholder="http://127.0.0.1:11434/v1" data-test="model-base-url" />
          </label>
          <label class="field">
            <span>模型名</span>
            <select v-if="models.length > 0" v-model="form.model.model" data-test="model-name">
              <option v-for="name in withStored(models, form.model.model)" :key="name" :value="name">{{ name }}</option>
            </select>
            <input v-else type="text" v-model="form.model.model" placeholder="qwen3" data-test="model-name" />
          </label>
          <div class="row">
            <button class="ghost" :disabled="probing" data-test="model-probe" @click="probeModels">
              {{ probing ? '拉取中…' : '拉取模型列表' }}
            </button>
            <span v-if="models.length > 0" class="muted" data-test="model-count">{{ models.length }} 个可选</span>
            <span v-if="modelProbeError" class="error break" data-test="model-probe-error">{{ modelProbeError }}</span>
          </div>
          <label class="field">
            <span>API Key</span>
            <input
              type="password"
              v-model="apiKeyDraft"
              :placeholder="apiKeySet ? '（已设置）' : '（未设置）'"
              data-test="model-api-key"
              @input="apiKeyEdited = true"
            />
            <span class="source" data-test="model-api-key-hint">{{ keyHint }}</span>
          </label>
          <div class="row">
            <button class="ghost" data-test="model-api-key-clear" @click="clearKey">清除密钥</button>
            <span class="muted" data-test="model-api-key-state">
              {{ apiKeySet ? '已设置' : '未设置' }}
            </span>
          </div>
          <label class="field">
            <span>请求超时 (s)</span>
            <input type="number" v-model.number="form.model.timeoutSeconds" />
          </label>
          <label class="field">
            <span>上下文大小（token）</span>
            <input type="number" min="1" v-model.number="form.model.contextTokens" data-test="model-context-tokens" />
            <span class="source">模型的上下文窗口；提示预算 = 它减去输出长度，改小后保存会立即压缩一次历史</span>
          </label>
          <label class=field>
            <span>支持图片（视觉）</span>
            <input type=checkbox v-model=form.model.supportsImages data-test=model-supports-images />
          </label>
          <p class=muted>
            打开后，UIA 看不见的应用（Electron、游戏、画布）走<strong>截图给模型看</strong>这条路，不再需要 OCR——
            模型自己读图比本地识别准。关闭时截图工具根本不注册：把图发给纯文本模型只会让请求失败。
          </p>

          <label class="field">
            <span>输出长度（token）</span>
            <input type="number" min="1" v-model.number="form.model.maxTokens" data-test="model-max-tokens" />
            <span class="source">单次补全的上限，必须小于上下文大小</span>
          </label>

          <div class="field">
            <span>思考挡位</span>

            <!--
              The default and the menu are shown together because they are one setting seen from two sides:
              the composer offers this list, and this value is what it preselects. Editing either without
              the other would let the two disagree on screen.
            -->
            <label class="gear-default">
              <span class="muted">默认</span>
              <input
                type="text"
                v-model="form.model.reasoningEffort"
                placeholder="medium"
                data-test="model-reasoning-effort"
              />
            </label>

            <ul class="gear-list" data-test="model-reasoning-gears">
              <li v-for="(gear, index) in form.model.reasoningGears" :key="index" class="gear-row">
                <input
                  :value="gear"
                  :aria-label="`第 ${index + 1} 档`"
                  data-test="model-gear"
                  @change="commitGear(index, ($event.target as HTMLInputElement).value)"
                />
                <!-- The cell is always present so the row's four buttons stay in one column; only the
                     default row has anything to say in it. -->
                <span class="badge" :data-test="gear === form.model.reasoningEffort ? 'model-gear-default' : undefined">
                  {{ gear === form.model.reasoningEffort ? '默认' : '' }}
                </span>
                <button
                  class="ghost"
                  :disabled="gear === form.model.reasoningEffort || normalizeGear(gear) === ''"
                  :aria-label="`把第 ${index + 1} 档设为默认`"
                  data-test="model-gear-set-default"
                  @click="useAsDefault(index)"
                >
                  <Icon name="star" :size="14" />
                </button>
                <button
                  class="ghost"
                  :disabled="index === 0"
                  :aria-label="`上移第 ${index + 1} 档`"
                  data-test="model-gear-up"
                  @click="moveGear(index, -1)"
                >
                  <Icon name="arrow-up" :size="14" />
                </button>
                <button
                  class="ghost"
                  :disabled="index === form.model.reasoningGears.length - 1"
                  :aria-label="`下移第 ${index + 1} 档`"
                  data-test="model-gear-down"
                  @click="moveGear(index, 1)"
                >
                  <Icon name="arrow-down" :size="14" />
                </button>
                <button
                  class="ghost"
                  :aria-label="`删除第 ${index + 1} 档`"
                  data-test="model-gear-remove"
                  @click="removeGear(index)"
                >
                  ×
                </button>
              </li>
            </ul>

            <button class="ghost" data-test="model-gear-add" @click="addGear">＋ 加一档</button>

            <span class="source">
              随每次请求发 <code>reasoning_effort</code>；随消息发的那一档覆盖这里。清单是菜单不是限制——
              服务端认的别的词（minimal、none…）加进来即可，<code>off</code> 表示<strong>不发这个字段</strong>。
              会话里的下拉按这个顺序显示。保存后立即重启消费者。
            </span>
          </div>

          <p class="card-note" data-test="model-effect">
            <template v-if="consumer?.running">消费者运行中 · {{ consumer.model ?? '模型' }}（{{ consumer.reason }}）</template>
            <template v-else-if="modelConfigured">已填好地址与模型名，保存后生效</template>
            <template v-else>未配置模型：队列不会被执行</template>
          </p>
          <div class="row card-footer" data-test="model-test-row">
            <button class="ghost" :disabled="voiceTesting.model" data-test="test-model" @click="testConnection('model')">
              {{ voiceTesting.model ? '测试中…' : '测试' }}
            </button>
            <span v-if="voiceTest.model" class="break" data-test="test-model-result" :class="voiceTest.model.ok ? 'muted' : 'error'">
              {{ voiceTest.model.ok ? '✓' : '✗' }} {{ voiceTest.model.detail }}（{{ voiceTest.model.latencyMs }}ms）
            </span>
          </div>
          </div>
        </section>
      </div>
    </div>

    <div class="toolbar footer-actions">
      <button class="primary" data-test="settings-save" @click="persist">保存设置</button>
      <span v-if="error" class="error" data-test="error">{{ error }}</span>
    </div>
  </div>
</template>

<style scoped>
.footer-actions {
  border-block-start: 1px solid var(--line);
  border-block-end: none;
}

.card-note code {
  padding: 0.1em 0.3em;
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  background: var(--surface-0);
  font-size: 0.92em;
}

/* 输入电平：条 + 阈值刻度 + 读数，一行放下。 */
.level {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin-block-start: var(--space-1);
}

.level-track {
  position: relative;
  flex: 1 1 auto;
  block-size: 0.5rem;
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  background: var(--surface-0);
  overflow: hidden;
}

.level-fill {
  block-size: 100%;
  background: var(--accent, currentColor);
  transition: width 120ms linear;
}

/* 阈值刻度：够不着它的电平就是「静音」，判不出静音的一段话永远不会结束。 */
.level-mark {
  position: absolute;
  inset-block: 0;
  inline-size: 2px;
  background: var(--line);
}

.level-readout {
  flex: none;
  font-variant-numeric: tabular-nums;
  font-size: 0.9em;
}

/* Connectivity test sits at the card's end: result then audio trail it to the right. */
.card-footer {
  margin-top: var(--space-3);
  padding-top: var(--space-2);
  border-top: 1px dashed var(--line);
  align-items: center;
}

.card-footer button {
  flex: none;
}

.tts-test-audio {
  inline-size: min(320px, 100%);
  block-size: 2.2rem;
}

.gear-default {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.gear-default input {
  inline-size: 12ch;
}

.gear-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  /* The rows are a control group, so they read as one block rather than as loose list items. */
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  padding: var(--space-2);
  background: var(--surface-1);
}

/* A grid, not a flex row: the default badge is present on only one row, and with flex the buttons on
   every other row would slide left to close the gap. Named tracks keep each column in line. */
.gear-row {
  display: grid;
  grid-template-columns: 14ch 3em repeat(4, 2.25em);
  align-items: center;
  gap: var(--space-1);
}

.gear-row input {
  min-inline-size: 0;
}

.gear-row .badge {
  font-size: var(--fs-meta);
  color: var(--text-3);
  text-align: start;
  white-space: nowrap;
}
</style>
