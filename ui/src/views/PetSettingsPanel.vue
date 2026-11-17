<script setup lang="ts">
import Icon from '../components/Icon.vue'
import HintTip from '../components/HintTip.vue'
import SettingRow from '../components/SettingRow.vue'
import PetTuningPanel from './PetTuningPanel.vue'
import { t } from '../i18n'
import type { PartnerApiSurface, RuntimeSettings, PetCoreStatus, PetModelInfo, PetModelProfile, PetKeyword } from '../api'
import type { EmotionMatch } from '../pet/emotion'

type Emotion = 'smile' | 'angry' | 'cry' | 'shy'
type Option = { value: string; label: string }
defineProps<{
  form: RuntimeSettings; api: PartnerApiSurface
  PET_CORE_URL: string; PET_LICENSE_URLS: { open: string; proprietary: string }
  PET_ACTIONS: readonly string[]; PET_EMOTIONS: readonly Emotion[]
  petCore: PetCoreStatus | null; petCoreHash: string; petCoreBusy: boolean; petCoreError: string
  petModels: PetModelInfo[]; petModelsError: string; petArmedModel: string | null
  petImportBusy: boolean; petImportError: string; petCandidates: string[]
  petScalePercent: number; shellPrefs: boolean; petSpawned: boolean
  petShellError: string; petResetError: string; petRetryTarget: boolean | null; retryPetWindow: () => void
  activePetModel: PetModelInfo | null; activeProfile: PetModelProfile; petTrial: EmotionMatch | null
  copyText: (text: string, message: string) => void; importPetCore: () => void
  refreshPetModels: () => void; formatBytes: (bytes: number) => string
  deletePetModel: (id: string) => void; importPetModel: (model3?: string) => void
  setScale: (event: Event) => void; resetPetWindow: () => void
  actionLabel: (action: string) => string; emotionLabel: (emotion: string) => string
  motionValue: (ref: PetModelProfile['motions'][string] | undefined) => string
  expressionValue: (ref: PetModelProfile['expressions'][string] | undefined) => string
  setMotion: (action: string, value: string) => void; setExpression: (name: string, value: string) => void
  withOption: (options: Option[], value: string) => Option[]; motionOptions: () => Option[]; expressionOptions: () => Option[]
  previewAction: (action: string) => void; previewExpression: (emotion: string) => void
  setHeadBottom: (event: Event) => void; setFaceBottom: (event: Event) => void
  editProfile: (change: (profile: PetModelProfile) => PetModelProfile) => void
  setGreetingTimes: (event: Event) => void; restoreDefaultDictionary: () => void
  keywordsOf: (emotion: Emotion) => PetKeyword[]; setKeywordText: (emotion: Emotion, at: number, event: Event) => void
  setKeywordWeight: (emotion: Emotion, at: number, event: Event) => void; removeKeyword: (emotion: Emotion, at: number) => void
  addKeyword: (emotion: Emotion) => void; setNegationPrefixes: (event: Event) => void; setMinWeight: (event: Event) => void
  primaryScore: (match: EmotionMatch, emotion: Emotion) => number
}>()
const petCoreConsent = defineModel<boolean>('petCoreConsent', { required: true })
const petCorePath = defineModel<string>('petCorePath', { required: true })
const petModelPath = defineModel<string>('petModelPath', { required: true })
const petCandidateChoice = defineModel<string>('petCandidateChoice', { required: true })
const petTrialText = defineModel<string>('petTrialText', { required: true })
</script>

<template>
          <section class="panel" data-test="settings-section-pet">
            <header class="panel-head">
              <h2>{{ t('settings.section.pet') }}</h2>
              <span class="g-chip" :class="form.pet.enabled ? 'is-ok' : ''" data-test="pet-effect">
                {{ form.pet.enabled ? t('已开启') : t('已关闭') }}
              </span>
              <HintTip align="end">
                {{ t('桌宠只读取会话里已经定稿的助手回复，按关键词判断情绪，不参与对话，也不会额外请求模型。') }}
              </HintTip>
            </header>

            <div v-if="petShellError" class="g-card" role="alert" data-test="pet-shell-error">
              <p>{{ t('settings.pet.savedButWindowFailed') }} {{ petShellError }}</p>
              <button v-if="petRetryTarget !== null" class="ghost" type="button" data-test="pet-shell-retry" @click="retryPetWindow">{{ t('app.retry') }}</button>
            </div>
            <p v-if="petResetError" class="warn-line" role="alert" data-test="pet-reset-error">{{ petResetError }}</p>

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
                  <Icon name="refresh" :size="14" />
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
            <PetTuningPanel
              v-if="activePetModel"
              :profile="activeProfile"
              :model="activePetModel"
              :api="api"
              :actions="PET_ACTIONS"
              @change="editProfile(() => $event)"
            />

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
                    <Icon name="x" :size="14" />
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
</template>

<style scoped src="./settings-panel.css"></style>
