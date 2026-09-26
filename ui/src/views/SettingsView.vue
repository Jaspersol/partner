<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import type { ConsumerState, PartnerApiSurface, RuntimeSettings } from '../api'

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
  model: { baseUrl: null, model: null, timeoutSeconds: 120 },
})

/** The key is write-only: it is never read back, so the field starts empty and tracks whether it was edited. */
const apiKeySet = ref(false)
const apiKeyDraft = ref('')
const apiKeyEdited = ref(false)

const status = ref('')
const error = ref('')
const consumer = ref<ConsumerState | null>(null)

const modelConfigured = computed(() => (form.model.baseUrl ?? '') !== '' && (form.model.model ?? '') !== '')
const keyHint = computed(() =>
  apiKeySet.value ? '已设置 · 留空保持不变，输入新值即替换' : '未设置（本地服务通常不需要）',
)

function apply(settings: RuntimeSettings & { model: { apiKeySet?: boolean } }) {
  Object.assign(form.input, settings.input)
  Object.assign(form.output, settings.output)
  Object.assign(form.asr, settings.asr)
  Object.assign(form.tts, settings.tts)
  Object.assign(form.model, {
    baseUrl: settings.model.baseUrl,
    model: settings.model.model,
    timeoutSeconds: settings.model.timeoutSeconds,
  })

  apiKeySet.value = 'apiKeySet' in settings.model ? Boolean(settings.model.apiKeySet) : false
  apiKeyDraft.value = ''
  apiKeyEdited.value = false
}

async function refresh() {
  error.value = ''
  try {
    apply(await props.api.settings())
    emit('context', '设置 · 运行时')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function persist() {
  error.value = ''
  status.value = ''

  const payload: RuntimeSettings = {
    input: { ...form.input },
    output: { ...form.output },
    asr: { ...form.asr },
    tts: { ...form.tts },
    model: {
      baseUrl: form.model.baseUrl === '' ? null : form.model.baseUrl,
      model: form.model.model === '' ? null : form.model.model,
      timeoutSeconds: form.model.timeoutSeconds,
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
    status.value = `已保存 · ${new Date().toLocaleTimeString()}`
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
            <span>输入设备 ID</span>
            <input type="text" v-model="form.input.deviceId" placeholder="（默认设备）" />
          </label>
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
            <span>输出设备 ID</span>
            <input type="text" v-model="form.output.deviceId" placeholder="（默认设备）" />
          </label>
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
            <input type="text" v-model="form.model.model" placeholder="qwen3" data-test="model-name" />
          </label>
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
