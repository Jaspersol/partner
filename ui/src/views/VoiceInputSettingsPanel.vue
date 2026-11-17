<script setup lang="ts">
import Icon from '../components/Icon.vue'
import HintTip from '../components/HintTip.vue'
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
  inputOptions: string[]
  wakeGateOn: boolean
  levelPercent: string
  thresholdPercent: string
  keywordDrafts: Record<'tts' | 'kws', string>
  onEngineKeywordsInput: (engine: 'tts' | 'kws', event: Event) => void
}>()
</script>

<template>
          <section class="panel" data-test="settings-section-input">
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
</template>

<style scoped src="./settings-panel.css"></style>
