<script setup lang="ts">
import HintTip from '../components/HintTip.vue'
import SettingRow from '../components/SettingRow.vue'
import { t, tr } from '../i18n'
import type { EnginePackageStatus, RuntimeSettings } from '../api'

const ENGINE_NAMES = { tts: tr('朗读引擎'), kws: tr('唤醒与识别引擎') } as const
defineProps<{
  form: RuntimeSettings
  enginePackage: EnginePackageStatus | null
  enginePackageNote: string
  downloadRunning: boolean
  allEnginesInstalled: boolean
  downloadText: string
  detectEngines: () => void
  startEngineDownload: () => void
  cancelEngineDownload: () => void
  applyEngineRoot: () => void
  showEngine: (engine: 'tts' | 'kws') => boolean
  onEngineArgumentsInput: (engine: 'tts' | 'kws', event: Event) => void
}>()
const engineManualOpen = defineModel<boolean>('engineManualOpen', { required: true })
const engineManualRoot = defineModel<string>('engineManualRoot', { required: true })
</script>

<template>
          <section class="panel" data-test="settings-section-engines">
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
</template>

<style scoped src="./settings-panel.css"></style>
