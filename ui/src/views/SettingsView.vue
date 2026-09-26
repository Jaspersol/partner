<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import type { PartnerApiSurface, VoiceSettings } from '../api'

const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string] }>()

const form = reactive<VoiceSettings>({
  inputEnabled: false,
  inputDeviceId: null,
  silenceThreshold: 0.03,
  utteranceGapMs: 1200,
  sentenceGapMs: 450,
  windowSeconds: 8,
  asrStream: false,
  outputEnabled: false,
  outputDeviceId: null,
  speed: 1,
  gapMs: 0,
  drainMs: 400,
  muteDuringTts: true,
})

const status = ref('')
const error = ref('')

function apply(settings: VoiceSettings) {
  Object.assign(form, settings)
}

async function refresh() {
  error.value = ''
  try {
    apply(await props.api.settings())
    emit('context', '设置 · 语音')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function persist() {
  error.value = ''
  status.value = ''

  try {
    // The API is the source of truth: show what it stored, not what was typed.
    apply(await props.api.saveSettings({ ...form }))
    status.value = `已保存 · ${new Date().toLocaleTimeString()}`
  } catch (failure) {
    error.value = String(failure)
  }

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
            <input type="checkbox" v-model="form.inputEnabled" data-test="input-enabled" @change="persist" />
          </label>
          <label class="field">
            <span>静音阈值 (0-1)</span>
            <input type="number" step="0.01" min="0" v-model.number="form.silenceThreshold" />
            <span class="source">低于此电平视为静音；实测默认 0.03</span>
          </label>
          <label class="field">
            <span>整句间隔 (ms)</span>
            <input type="number" v-model.number="form.utteranceGapMs" data-test="utterance-gap" />
            <span class="source">停顿超过此值切句；实测默认 1200</span>
          </label>
          <label class="field">
            <span>句间间隔 (ms)</span>
            <input type="number" v-model.number="form.sentenceGapMs" />
          </label>
          <label class="field">
            <span>分析窗口 (s)</span>
            <input type="number" v-model.number="form.windowSeconds" />
            <span class="source">环形缓冲长度；实测默认 8</span>
          </label>
          <label class="field">
            <span>流式识别</span>
            <input type="checkbox" v-model="form.asrStream" @change="persist" />
          </label>
        </section>

        <section class="card">
          <h2>语音输出</h2>
          <p class="card-note">播放期间停听麦克风，避免把自身语音当成输入。</p>
          <label class="field">
            <span>启用语音输出</span>
            <input type="checkbox" v-model="form.outputEnabled" @change="persist" />
          </label>
          <label class="field">
            <span>语速</span>
            <input type="number" step="0.05" v-model.number="form.speed" />
          </label>
          <label class="field">
            <span>播放期停听 (drain, ms)</span>
            <input type="number" v-model.number="form.drainMs" />
            <span class="source">播放结束后继续停听的时长；实测默认 400</span>
          </label>
          <label class="field">
            <span>播放时停听麦克风</span>
            <input type="checkbox" v-model="form.muteDuringTts" @change="persist" />
          </label>
        </section>

        <section class="card">
          <h2>设备与端点</h2>
          <p class="card-note">留空表示使用系统默认设备。</p>
          <label class="field">
            <span>输入设备 ID</span>
            <input type="text" v-model="form.inputDeviceId" placeholder="（默认设备）" />
          </label>
          <label class="field">
            <span>输出设备 ID</span>
            <input type="text" v-model="form.outputDeviceId" placeholder="（默认设备）" />
          </label>
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
</style>
