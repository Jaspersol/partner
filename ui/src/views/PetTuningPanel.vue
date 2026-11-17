<script setup lang="ts">
import { ref, watch } from 'vue'
import type { PartnerApiSurface, PetDiagnostics, PetModelInfo, PetModelProfile, PetParameterTrack } from '../api'
import { PARAMETER_CHANNELS, proceduralDuration, type ActionName } from '../pet/actions'
import { t } from '../i18n'

const props = defineProps<{ profile: PetModelProfile; model: PetModelInfo; api: PartnerApiSurface; actions: readonly string[] }>()
const emit = defineEmits<{ change: [profile: PetModelProfile] }>()
const diagnostics = ref<PetDiagnostics | null>(null)
const diagnosticError = ref('')
watch(() => props.model.id, () => { diagnostics.value = null; diagnosticError.value = '' })

function update(profile: Partial<PetModelProfile>) {
  emit('change', { ...props.profile, ...profile })
}
function mapChannel(channel: string, value: string) {
  const parameters = { ...props.profile.parameters }
  if (value.trim()) parameters[channel] = value.trim()
  else delete parameters[channel]
  update({ parameters })
}
// A legacy spelling is a suggestion only; it is never applied until the user selects it.
function legacyCandidate(channel: string): string {
  return channel.replace(/^Param/, 'PARAM_').replace(/([a-z])([A-Z])/g, '$1_$2').replace(/([A-Z])([A-Z][a-z])/g, '$1_$2').toUpperCase()
}
function actionConfig(action: string) {
  return props.profile.procedural?.[action] ?? {}
}
function updateAction(action: string, patch: { amplitude?: number; extraTracks?: PetParameterTrack[] }) {
  update({ procedural: { ...props.profile.procedural, [action]: { ...actionConfig(action), ...patch } } })
}
function changeTrack(action: string, index: number, patch: Partial<PetParameterTrack>) {
  const tracks = [...(actionConfig(action).extraTracks ?? [])]
  tracks[index] = { ...tracks[index]!, ...patch }
  updateAction(action, { extraTracks: tracks })
}
function addTrack(action: string) {
  const duration = proceduralDuration(action as ActionName)
  if (duration === null) return
  updateAction(action, { extraTracks: [...(actionConfig(action).extraTracks ?? []), { id: '', keys: [{ at: 0, value: 0 }, { at: duration / 2, value: 1 }, { at: duration, value: 0 }] }] })
}
function removeTrack(action: string, index: number) {
  updateAction(action, { extraTracks: (actionConfig(action).extraTracks ?? []).filter((_, i) => i !== index) })
}
function setKey(action: string, trackIndex: number, keyIndex: number, field: 'at' | 'value', value: string) {
  const track = actionConfig(action).extraTracks?.[trackIndex]
  if (!track) return
  const keys = track.keys.map((key, i) => i === keyIndex ? { ...key, [field]: Number(value) } : key)
  changeTrack(action, trackIndex, { keys })
}
function insertKey(action: string, trackIndex: number, index: number) {
  const track = actionConfig(action).extraTracks?.[trackIndex]
  if (!track || !track.keys[index] || !track.keys[index + 1]) return
  const keys = [...track.keys]
  keys.splice(index + 1, 0, { at: (keys[index]!.at + keys[index + 1]!.at) / 2, value: 0 })
  changeTrack(action, trackIndex, { keys })
}
function removeKey(action: string, trackIndex: number, index: number) {
  const track = actionConfig(action).extraTracks?.[trackIndex]
  if (track) changeTrack(action, trackIndex, { keys: track.keys.filter((_, i) => i !== index) })
}
async function refreshDiagnostics() {
  diagnosticError.value = ''
  try {
    diagnostics.value = props.api.petDiagnostics ? await props.api.petDiagnostics() : null
  } catch (error) {
    diagnosticError.value = error instanceof Error ? error.message : String(error)
  }
}
</script>

<template>
  <div class="g-card" data-test="pet-tuning">
    <div class="g-card-head"><h3>{{ t('程序化动作调校') }}</h3></div>
    <p>{{ t('模型参数列表来自描述文件，只作候选；实际参数和范围以运行中的桌宠诊断为准。') }}</p>
    <p v-for="warning in model.warnings ?? []" :key="warning" class="warn-line">{{ warning }}</p>
    <label>{{ t('模型动作倍率') }} <input type="number" min="-4" max="4" step="0.1" :value="profile.amplitude ?? 1" data-test="pet-amplitude" @input="update({ amplitude: Number(($event.target as HTMLInputElement).value) })" /></label>
    <p>{{ t('倍率为负时反向；动作倍率与模型倍率相乘。模型 motion 仍优先。') }}</p>
    <details>
      <summary>{{ t('参数通道映射') }}</summary>
      <p>{{ t('留空使用标准 ID；手动选择或输入模型实际 ID，不会自动猜测。') }}</p>
      <datalist id="pet-parameter-candidates">
        <option v-for="parameter in model.parameters ?? []" :key="parameter.id" :value="parameter.id">{{ parameter.name }} ({{ parameter.groupId ?? '' }})</option>
      </datalist>
      <label v-for="channel in PARAMETER_CHANNELS" :key="channel" class="tuning-row">
        <code>{{ channel }}</code>
        <input :value="profile.parameters?.[channel] ?? ''" :list="`pet-candidates-${channel}`" :placeholder="channel" :data-test="`pet-channel-${channel}`" @input="mapChannel(channel, ($event.target as HTMLInputElement).value)" />
        <datalist :id="`pet-candidates-${channel}`">
          <option :value="legacyCandidate(channel)">{{ t('旧版 ID 候选') }}</option>
          <option v-for="parameter in model.parameters ?? []" :key="parameter.id" :value="parameter.id">{{ parameter.name }} ({{ parameter.groupId ?? '' }})</option>
        </datalist>
      </label>
    </details>
    <details>
      <summary>{{ t('动作倍率和附加参数轨道') }}</summary>
      <div v-for="action in actions" :key="action" class="action-config" :data-test="`pet-tuning-${action}`">
        <strong>{{ action }}</strong>
        <label>{{ t('倍率') }} <input type="number" min="-4" max="4" step="0.1" :value="actionConfig(action).amplitude ?? 1" @input="updateAction(action, { amplitude: Number(($event.target as HTMLInputElement).value) })" /></label>
        <div v-for="(track, trackIndex) in actionConfig(action).extraTracks ?? []" :key="trackIndex" class="track">
          <label>{{ t('参数 ID') }} <input list="pet-parameter-candidates" :value="track.id" @input="changeTrack(action, trackIndex, { id: ($event.target as HTMLInputElement).value })" /></label>
          <button type="button" @click="removeTrack(action, trackIndex)">{{ t('删除轨道') }}</button>
          <div v-for="(key, keyIndex) in track.keys" :key="keyIndex" class="tuning-row">
            <label>{{ t('时间（秒）') }} <input type="number" step="0.01" :value="key.at" :disabled="keyIndex === 0 || keyIndex === track.keys.length - 1" @input="setKey(action, trackIndex, keyIndex, 'at', ($event.target as HTMLInputElement).value)" /></label>
            <label>{{ t('偏移') }} <input type="number" step="0.1" :value="key.value" :disabled="keyIndex === 0 || keyIndex === track.keys.length - 1" @input="setKey(action, trackIndex, keyIndex, 'value', ($event.target as HTMLInputElement).value)" /></label>
            <button v-if="keyIndex < track.keys.length - 1" type="button" @click="insertKey(action, trackIndex, keyIndex)">{{ t('插入关键帧') }}</button>
            <button v-if="keyIndex > 0 && keyIndex < track.keys.length - 1" type="button" @click="removeKey(action, trackIndex, keyIndex)">{{ t('移除') }}</button>
          </div>
        </div>
        <button type="button" :disabled="proceduralDuration(action as ActionName) === null" @click="addTrack(action)">{{ t('添加参数轨道') }}</button>
      </div>
      <p>{{ t('轨道首尾为零，关键帧时间必须严格递增，末帧固定为动作时长；数值按模型参数范围钳制。') }}</p>
    </details>
    <div>
      <button type="button" data-test="pet-diagnostics-refresh" @click="refreshDiagnostics">{{ t('读取动作诊断') }}</button>
      <p>{{ t('诊断和预览只反映桌宠已加载的已保存设置，不反映未保存的编辑。') }}</p>
      <p v-if="diagnosticError" role="alert">{{ diagnosticError }}</p>
      <p v-else-if="!diagnostics || diagnostics.modelId !== model.id">{{ t('当前模型尚无运行时诊断，请打开桌宠并保存设置。') }}</p>
      <ul v-else data-test="pet-diagnostics">
        <li v-for="result in diagnostics.actions" :key="result.action">
          {{ result.action }}: {{ result.kind }}
          <span v-for="parameter in result.parameters" :key="parameter.channel"> · {{ parameter.channel }} → {{ parameter.id }} [{{ parameter.min }}, {{ parameter.max }}]</span>
          <span v-if="result.missing.length"> · {{ t('缺少参数') }}: {{ result.missing.join(', ') }}</span>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.g-card { background: var(--glass-1); border: 1px solid var(--glass-edge); border-radius: var(--radius-md); box-shadow: var(--glass-hi), var(--shadow-1); padding: var(--space-2) var(--space-5) var(--space-3); min-inline-size: 0; }
.g-card-head { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); min-block-size: 3rem; padding-block-start: var(--space-1); }
.g-card-head h3 { font-size: 0.8125rem; color: var(--text-2); }
.tuning-row { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); margin-block: var(--space-2); }
.tuning-row code { min-inline-size: 12rem; }
.action-config, .track { border-block-start: 1px solid var(--glass-edge); padding-block: var(--space-2); }
.action-config label, .track label { margin-inline-end: var(--space-2); }
input[type='number'] { max-inline-size: 6rem; }
</style>
