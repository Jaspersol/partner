<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import type { ConsumerState, DeviceList, PartnerApiSurface, RuntimeSettings, VoiceState } from '../api'

const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string] }>()

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
  asr: { baseUrl: null, model: 'qwen3-asr', language: 'Chinese', connectTimeoutMs: 2000, readTimeoutMs: 3000, sampleRate: 16000 },
  tts: { baseUrl: null, lang: 'zh', sampleRate: 24000, speakerRefPath: null, timeoutSeconds: 600, chunkMs: 100 },
  model: { baseUrl: null, model: null, timeoutSeconds: 120, contextTokens: 32768, maxTokens: 4096 },
  desktop: { enabled: false, leaseTtlMs: 15000, allowedWindowTitles: [] as string[] },
})

/** The key is write-only: it is never read back, so the field starts empty and tracks whether it was edited. */
const apiKeySet = ref(false)
const apiKeyDraft = ref('')
const apiKeyEdited = ref(false)

/** Titles are edited as one comma-separated line; empty entries are dropped rather than stored. */
function onTitlesInput(event: Event) {
  const raw = (event.target as HTMLInputElement).value
  form.desktop.allowedWindowTitles = raw
    .split(/[,，]/)
    .map((title) => title.trim())
    .filter((title) => title !== '')
}

const status = ref('')
const error = ref('')
const consumer = ref<ConsumerState | null>(null)
const voice = ref<VoiceState | null>(null)
const devices = ref<DeviceList>({ inputs: [], outputs: [] })
const models = ref<string[]>([])
const modelProbeError = ref('')
const probing = ref(false)

const inputOptions = computed(() => withStored(devices.value.inputs, form.input.deviceId))
const outputOptions = computed(() => withStored(devices.value.outputs, form.output.deviceId))

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
  Object.assign(form.desktop, {
    enabled: settings.desktop?.enabled ?? false,
    leaseTtlMs: settings.desktop?.leaseTtlMs ?? 15000,
    allowedWindowTitles: settings.desktop?.allowedWindowTitles ?? [],
  })
  Object.assign(form.model, {
    baseUrl: settings.model.baseUrl,
    model: settings.model.model,
    timeoutSeconds: settings.model.timeoutSeconds,
    contextTokens: settings.model.contextTokens,
    maxTokens: settings.model.maxTokens,
  })

  apiKeySet.value = 'apiKeySet' in settings.model ? Boolean(settings.model.apiKeySet) : false
  apiKeyDraft.value = ''
  apiKeyEdited.value = false
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
    model: {
      baseUrl: form.model.baseUrl === '' ? null : form.model.baseUrl,
      model: form.model.model === '' ? null : form.model.model,
      timeoutSeconds: form.model.timeoutSeconds,
      contextTokens: form.model.contextTokens,
      maxTokens: form.model.maxTokens,
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
  } catch (failure) {
    error.value = String(failure)
  }
}

function clearKey() {
  apiKeyDraft.value = ''
  apiKeyEdited.value = true
}

onMounted(refresh)
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="settings-reload" @click="refresh">重载</button>
      <span class="spacer" />
      <span class="muted" data-test="settings-status">{{ status }}</span>
    </div>

    <div class="scroll">
      <div class="cards">
        <section class="card">
          <h2>语音输入</h2>
          <p class="card-note">关闭会立即释放麦克风设备（不保留占用）。</p>
          <label class="field">
            <span>启用麦克风输入</span>
            <input type="checkbox" v-model="form.input.enabled" data-test="input-enabled" @change="persist" />
          </label>
          <label class="field">
            <span>静音阈值 (0-1)</span>
            <input type="number" step="0.01" min="0" v-model.number="form.input.silenceThreshold" />
            <span class="source">低于此电平视为静音；实测默认 0.03</span>
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
            <input type="checkbox" v-model="form.input.asrStream" @change="persist" />
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
            <template v-if="voice?.input.running">运行中 · {{ voice.input.device }} · 已识别 {{ voice.input.count }} 段</template>
            <template v-else-if="voice">未运行 · {{ voice.input.reason }}</template>
            <template v-else>（保存后查看运行状态）</template>
          </p>
        </section>

        <section class="card">
          <h2>语音输出</h2>
          <p class="card-note">播放期间停听麦克风，避免把自身语音当成输入。</p>
          <label class="field">
            <span>启用语音输出</span>
            <input type="checkbox" v-model="form.output.enabled" @change="persist" />
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
            <input type="checkbox" v-model="form.output.muteDuringTts" @change="persist" />
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
        </section>

        <section class="card">
          <h2>语音识别（ASR）</h2>
          <p class="card-note">OpenAI 兼容的识别端点。留空表示不配置——此时麦克风输入不会转写。</p>
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
        </section>

        <section class="card">
          <h2>语音合成（TTS）</h2>
          <p class="card-note">留空表示不配置——此时助手回复不会朗读。</p>
          <label class="field">
            <span>服务地址</span>
            <input type="text" v-model="form.tts.baseUrl" placeholder="http://127.0.0.1:45102" data-test="tts-base-url" />
          </label>
          <label class="field">
            <span>语言</span>
            <input type="text" v-model="form.tts.lang" />
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
        </section>

        <section class="card">
          <h2>桌面（M9）</h2>
          <p class="card-note">
            本仓库里唯一**不可逆**的能力：打开后模型可以观察窗口，并请求移动指针或发按键。即使打开，
            **每个动作都要人工批准**（会话里弹出「允许 / 拒绝」，无人应答即拒绝），所以打开只是让模型
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
        </section>

        <section class="card">
          <h2>模型（对话）</h2>
          <p class="card-note">
            这里是执行队列的那个模型。保存后会**立即重启消费者**；留空则退回环境变量
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
          <label class="field">
            <span>输出长度（token）</span>
            <input type="number" min="1" v-model.number="form.model.maxTokens" data-test="model-max-tokens" />
            <span class="source">单次补全的上限，必须小于上下文大小</span>
          </label>

          <p class="card-note" data-test="model-effect">
            <template v-if="consumer?.running">消费者运行中 · {{ consumer.model ?? '模型' }}（{{ consumer.reason }}）</template>
            <template v-else-if="modelConfigured">已填好地址与模型名，保存后生效</template>
            <template v-else>未配置模型：队列不会被执行</template>
          </p>
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
.view {
  display: flex;
  flex-direction: column;
  min-block-size: 0;
  block-size: 100%;
}

.spacer {
  flex: 1;
}

.footer-actions {
  border-block-start: 1px solid var(--line);
  border-block-end: none;
}

.card-note code {
  padding: 0.1em 0.3em;
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  background: var(--bg);
  font-size: 0.92em;
}
</style>
