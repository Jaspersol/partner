<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import type { PartnerApiSurface, VoiceSettings } from '../api'

const props = defineProps<{ api: PartnerApiSurface }>()

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
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save() {
  error.value = ''
  status.value = ''

  try {
    // The API is the source of truth: show what was stored, not what was typed.
    apply(await props.api.saveSettings({ ...form }))
    status.value = '已保存'
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)
defineExpose({ refresh })
</script>

<template>
  <section class="pane form">
    <h2>语音输入</h2>
    <label><input type="checkbox" v-model="form.inputEnabled" data-test="input-enabled" /> 启用麦克风输入</label>
    <label>静音阈值 (0-1) <input type="number" step="0.01" v-model.number="form.silenceThreshold" /></label>
    <label>整句间隔 (ms) <input type="number" v-model.number="form.utteranceGapMs" data-test="utterance-gap" /></label>
    <label>分析窗口 (s) <input type="number" v-model.number="form.windowSeconds" /></label>
    <label><input type="checkbox" v-model="form.asrStream" /> 流式识别</label>

    <h2>语音输出</h2>
    <label><input type="checkbox" v-model="form.outputEnabled" /> 启用语音输出</label>
    <label>语速 <input type="number" step="0.05" v-model.number="form.speed" /></label>
    <label>播放期停听 (drain, ms) <input type="number" v-model.number="form.drainMs" /></label>
    <label><input type="checkbox" v-model="form.muteDuringTts" /> 播放时停听麦克风</label>

    <div class="row">
      <button @click="save" data-test="settings-save">保存设置</button>
      <button @click="refresh" data-test="settings-reload">重载</button>
      <span class="muted" data-test="settings-status">{{ status }}</span>
    </div>

    <p v-if="error" class="error" data-test="error">{{ error }}</p>
  </section>
</template>
