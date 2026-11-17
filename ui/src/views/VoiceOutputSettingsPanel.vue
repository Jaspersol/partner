<script setup lang="ts">
import { ref } from 'vue'
import Icon from '../components/Icon.vue'
import SettingRow from '../components/SettingRow.vue'
import TestButton from '../components/TestButton.vue'
import { t } from '../i18n'
import type { RuntimeSettings, VoiceState, VoiceTestResult } from '../api'

defineProps<{
  form: RuntimeSettings
  voice: VoiceState | null
  voiceTest: Record<string, VoiceTestResult | null>
  voiceTesting: Record<string, boolean>
  dirty: boolean
  advanced: Record<string, boolean>
  toggleAdvanced: (key: string) => void
  testStale: (kind: string) => boolean
  testConnection: (kind: 'asr' | 'tts' | 'kws' | 'model') => void
  outputOptions: string[]
  cloneName: string
  cloneBusy: boolean
  cloneError: string
  onCloneFile: (event: Event) => void
  testTtsUrl: string
}>()
const cloneInput = ref<HTMLInputElement | null>(null)
</script>

<template>
          <section class="panel" data-test="settings-section-output">
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
</template>

<style scoped src="./settings-panel.css"></style>
