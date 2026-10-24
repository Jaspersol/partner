<script setup lang="ts">
import { locale, t } from '../i18n'
import { computed, onMounted, ref } from 'vue'
import Dialog from '../components/Dialog.vue'
import HintTip from '../components/HintTip.vue'
import Icon from '../components/Icon.vue'
import { gearName, setGearLabels } from '../gears'
import SaveBar from '../components/SaveBar.vue'
import Segmented from '../components/Segmented.vue'
import SettingRow from '../components/SettingRow.vue'
import type {
  AgentsSettings,
  AgentsView,
  AgentScenarioView,
  PartnerApiSurface,
  SubAgentModelSettings,
  SubAgentSettings,
  SubAgentView,
} from '../api'
import { ApiError } from '../api'

/**
 * The prompts page: what the main agent is told, and the sub-agents it may hand work to.
 *
 * <para>
 * One document, one save. The stored shape is not the view shape — the view carries the defaults, the effective
 * prompts and the limits, the stored shape carries only what a person can change — and keeping them apart is
 * what lets 「恢复默认」 mean "store null" rather than "store a copy of today's default text".
 * </para>
 *
 * <para>
 * The effective prompt is read-only and collapsible. The generated parts (the delegation block, the report
 * contract) are half of what the model actually sees, and a person who cannot read them is editing blind.
 * </para>
 */
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ changed: []; navigate: [key: string] }>()

const view = ref<AgentsView | null>(null)
const error = ref('')
/** Every rule a rejected save broke, shown as a list rather than one concatenated sentence. */
const errors = ref<string[]>([])
const notice = ref('')

/** Which agent the right column shows: 'main', a stored sub-agent's id, or a draft's local key. */
const selectedKey = ref('main')

interface SubDraft {
  /** Stable while the page is open: a stored id, or a local key for a draft the server has not named yet. */
  key: string
  id: string
  name: string
  enabled: boolean
  scenario: string
  prompt: string
  maxRounds: number
  /** Null = follow the main model. */
  model: SubAgentModelSettings | null
  /** Whether the key field was touched. Untouched means "keep whatever is stored". */
  apiKeyEdited: boolean
  /** What the GET said about a stored key, so the hint survives the draft being edited. */
  apiKeySet: boolean
}

const main = ref<{ prompt: string }>({ prompt: '' })
const subs = ref<SubDraft[]>([])

/**
 * The stored form, for telling "changed" from "unchanged".
 *
 * A snapshot rather than a dirty flag: `restore()` rewrites every draft from the server, and that has to make
 * the page clean in the same move.
 */
const stored = ref<string | null>(null)

/** The add form. Closed until asked for: an empty row on the page is a question nobody asked. */
const adding = ref(false)
const newName = ref('')
const newScenario = ref('')
const addError = ref('')
let draftCounter = 0

/** Which effective prompt preview is open, by key ('main' for the main agent). Closed by default. */
const previewOpen = ref<string | null>(null)

const limit = computed(() => view.value?.limits.promptTokens ?? 0)
const maxRounds = computed(() => view.value?.limits.maxRounds ?? 10)
const defaultMaxRounds = computed(() => view.value?.limits.defaultMaxRounds ?? 5)

const mainSelected = computed(() => selectedKey.value === 'main')
const selectedSub = computed(() => subs.value.find((agent) => agent.key === selectedKey.value) ?? null)
const selectedSubView = computed(
  () => view.value?.subAgents.find((agent) => agent.id !== '' && agent.id === selectedSub.value?.id) ?? null,
)

/**
 * A local mirror of the server's prompt measurement (`PromptSize.Estimate`).
 *
 * <para>
 * The page cannot ask the server how big a draft it has not saved is, and a Save button that only learns the
 * answer from a 400 is a Save button that was offered for a prompt that could never be stored. The rule is
 * copied rather than invented: a CJK character is one token, every four other characters are one. The server
 * stays authoritative — its number is what the page shows for stored prompts, and its 400 is what refuses one.
 * </para>
 */
function estimateTokens(text: string): number {
  let wide = 0
  for (const character of text) {
    if ((character.codePointAt(0) ?? 0) >= 0x2e80) {
      wide++
    }
  }

  return wide + Math.floor((text.length - wide + 3) / 4)
}

/**
 * What the server would say about a draft.
 *
 * The effective prompt is the body plus a generated block. The block cannot be recomputed here (the server
 * generates it), so it is measured as the difference between the stored effective prompt and its stored body.
 * An unchanged draft returns the server's own number; a changed one returns the same rule applied to the new
 * body plus that block.
 */
function promptTokensFor(draft: string, body: string, effective: string, measured: number): number {
  const normalized = draft.trim() === '' ? body : draft
  if (normalized === body) {
    return measured
  }

  const suffix = effective.startsWith(body + '\n\n') ? effective.slice(body.length + 2) : ''
  return estimateTokens(normalized) + estimateTokens(suffix)
}

/** The main agent's draft, measured. */
const mainTokens = computed(() => {
  const current = view.value
  if (current === null) {
    return 0
  }

  return promptTokensFor(main.value.prompt, current.main.prompt ?? current.main.defaultPrompt, current.main.effectivePrompt, current.main.promptTokens)
})

/** One sub-agent draft, measured. A draft the server has not named yet has no generated block to add. */
function subTokens(agent: SubDraft): number {
  const stored = view.value?.subAgents.find((item) => item.id !== '' && item.id === agent.id)
  if (stored === undefined) {
    return estimateTokens(agent.prompt)
  }

  return promptTokensFor(agent.prompt, stored.prompt ?? stored.defaultPrompt, stored.effectivePrompt, stored.promptTokens)
}

const selectedSubTokens = computed(() => (selectedSub.value === null ? 0 : subTokens(selectedSub.value)))

/**
 * Whether any prompt on the page is over the limit, measured the way the server would.
 *
 * A draft is included: the reason to disable Save is to say so *before* the round trip, and a prompt that was
 * fine when it was stored stops being fine the moment it is typed past the limit.
 */
const overLimit = computed(() => {
  const current = view.value
  if (current === null) {
    return false
  }

  return (
    mainTokens.value > current.limits.promptTokens ||
    subs.value.some((agent) => subTokens(agent) > current.limits.promptTokens)
  )
})

function fingerprint(): string {
  return JSON.stringify({
    main: main.value.prompt,
    subs: subs.value.map((agent) => ({
      id: agent.id,
      name: agent.name,
      enabled: agent.enabled,
      scenario: agent.scenario,
      prompt: agent.prompt,
      maxRounds: agent.maxRounds,
      model: agent.model,
      // Write-only, exactly like the settings page's model key: a draft is either a value or nothing.
      apiKey: agent.apiKeyEdited ? (agent.model?.apiKey ?? '') : null,
    })),
  })
}

const dirty = computed(() => stored.value !== null && fingerprint() !== stored.value)

function blankSub(scenario: AgentScenarioView): SubDraft {
  return {
    key: `draft-${++draftCounter}`,
    id: '',
    name: '',
    enabled: true,
    scenario: scenario.key,
    prompt: '',
    maxRounds: defaultMaxRounds.value,
    model: null,
    apiKeyEdited: false,
    apiKeySet: false,
  }
}

/**
 * The draft for one stored sub-agent.
 *
 * A `null` stored prompt becomes an empty box rather than a copy of the default: null means "use the default",
 * the textarea shows that default as its placeholder, and copying the text in would light up 「已修改」 the
 * moment the page opened — an override the person never asked for.
 */
function draftFrom(agent: SubAgentView): SubDraft {
  return {
    key: agent.id,
    id: agent.id,
    name: agent.name,
    enabled: agent.enabled,
    scenario: agent.scenario,
    prompt: agent.prompt ?? '',
    maxRounds: agent.maxRounds,
    model: agent.model === null ? null : { ...agent.model },
    apiKeyEdited: false,
    apiKeySet: agent.model?.apiKeySet ?? false,
  }
}

function restore(current: AgentsView) {
  main.value = { prompt: current.main.prompt ?? '' }
  // Rebuilt in the server's order, so the list on screen is the list that was stored.
  subs.value = current.subAgents.map(draftFrom)

  // A selection that no longer exists (a deleted agent) falls back to the main agent rather than to nothing.
  if (selectedKey.value !== 'main' && !subs.value.some((agent) => agent.key === selectedKey.value)) {
    selectedKey.value = 'main'
  }

  stored.value = fingerprint()
}

async function refresh() {
  error.value = ''
  errors.value = []
  try {
    const loaded = await props.api.agents()
    view.value = loaded
    restore(loaded)
  } catch (failure) {
    error.value = String(failure)
  }
}

/** The stored shape, built from the drafts. Blank text is null: "nothing typed" and "use the default" are one thing. */
function payload(): AgentsSettings {
  return {
    main: { prompt: main.value.prompt.trim() === '' ? null : main.value.prompt },
    subAgents: subs.value.map((agent): SubAgentSettings => {
      const model: SubAgentModelSettings | null =
        agent.model === null
          ? null
          : {
              protocol: agent.model.protocol,
              baseUrl: agent.model.baseUrl,
              model: agent.model.model,
              timeoutSeconds: agent.model.timeoutSeconds,
              contextTokens: agent.model.contextTokens,
              maxTokens: agent.model.maxTokens,
              supportsImages: agent.model.supportsImages,
              reasoningEffort: agent.model.reasoningEffort,
            }

      // An untouched key does not travel: the server's rule is "absent keeps what is stored". An edited one is
      // sent even when blank — that is the explicit clear.
      if (model !== null && agent.apiKeyEdited) {
        model.apiKey = agent.model?.apiKey ?? ''
      }

      return {
        id: agent.id,
        name: agent.name,
        enabled: agent.enabled,
        scenario: agent.scenario,
        prompt: agent.prompt.trim() === '' ? null : agent.prompt,
        maxRounds: agent.maxRounds,
        model,
      }
    }),
  }
}

async function save() {
  error.value = ''
  errors.value = []
  notice.value = ''

  try {
    const saved = await props.api.saveAgents(payload())
    view.value = saved.agents
    restore(saved.agents)
    notice.value = t('已保存 · {toLocaleTimeString}', { toLocaleTimeString: new Date().toLocaleTimeString(locale.value) })
    emit('changed')
  } catch (failure) {
    if (failure instanceof ApiError) {
      errors.value = [...failure.errors]
      error.value = failure.errors.length > 0 ? '' : failure.message
    } else {
      error.value = String(failure)
    }
  }
}

/**
 * Throws the drafts away and re-reads the server: the page never keeps an edit nobody stored. The save bar asks
 * 「确认放弃」 in place before this is called, so there is no native dialog to block the webview.
 */
async function discard() {
  adding.value = false
  await refresh()
}

function resetMain() {
  main.value = { prompt: '' }
}

function resetSubPrompt() {
  const current = selectedSub.value
  if (current !== null) {
    current.prompt = ''
  }
}

/**
 * The model selector. Turning custom on starts from the main model's defaults rather than zeros: a custom
 * endpoint with no address is a save the server will refuse, and the person would have to find every field.
 */
function setCustomModel(on: boolean) {
  const current = selectedSub.value
  if (current === null) {
    return
  }

  current.model = on
    ? {
        protocol: 'openai',
        baseUrl: null,
        model: null,
        timeoutSeconds: 120,
        contextTokens: 32768,
        maxTokens: 4096,
        supportsImages: false,
        reasoningEffort: 'medium',
      }
    : null
  current.apiKeyEdited = false
}

function editKey(agent: SubDraft, value: string) {
  if (agent.model === null) {
    return
  }

  agent.model.apiKey = value
  agent.apiKeyEdited = true
}

/** The stored key's state: the draft never holds the key, only whether it was replaced. */
function keySetFor(agent: SubDraft): boolean {
  const storedAgent = view.value?.subAgents.find((item) => item.id !== '' && item.id === agent.id)
  return agent.apiKeySet || (storedAgent?.model?.apiKeySet ?? false)
}

/** Scenarios a new sub-agent may take: unowned, and not already taken by a draft on this page. */
const freeScenarios = computed(() => {
  const claimed = new Set(subs.value.filter((agent) => agent.id === '').map((agent) => agent.scenario))
  return (view.value?.scenarios ?? []).filter((scenario) => scenario.ownerId === null && !claimed.has(scenario.key))
})

const canAdd = computed(() => freeScenarios.value.length > 0)

/** Why the add button is disabled, in the one case where it is. */
const addBlockedReason = computed(() => (canAdd.value ? '' : t('两个场景都已有子 agent')))

function openAdd() {
  addError.value = ''
  newName.value = ''
  newScenario.value = freeScenarios.value[0]?.key ?? ''
  adding.value = true
}

/**
 * Adds a draft, not a stored agent. It goes to the server with the rest of the page on Save, with `id: ""` —
 * the server names it, so two windows cannot pick the same id.
 */
function commitAdd() {
  const scenario = freeScenarios.value.find((item) => item.key === newScenario.value)
  if (scenario === undefined) {
    addError.value = t('先选一个场景')
    return
  }
  if (newName.value.trim() === '') {
    addError.value = t('子 agent 需要一个名字')
    return
  }

  const draft = blankSub(scenario)
  draft.name = newName.value.trim()
  subs.value = [...subs.value, draft]
  selectedKey.value = draft.key
  adding.value = false
}

/** The built-in agent: no delete, and its scenario is locked. */
function isBuiltin(agent: SubDraft): boolean {
  return agent.id === 'desktop' || view.value?.subAgents.some((item) => item.id === agent.id && item.builtin) === true
}

/** Choosing an agent. A pending 「确认删除」 belongs to the agent it was pressed on, so it does not follow the selection. */
function pick(key: string) {
  selectedKey.value = key
  confirmingRemove.value = false
}

/** Deleting a sub-agent arms first: the button turns into 「确认删除」, and picking another agent disarms it. */
const confirmingRemove = ref(false)

function removeSelected() {
  const current = selectedSub.value
  if (current === null || isBuiltin(current)) {
    return
  }
  if (!confirmingRemove.value) {
    confirmingRemove.value = true
    return
  }

  confirmingRemove.value = false
  subs.value = subs.value.filter((agent) => agent !== current)
  selectedKey.value = 'main'
}

/** The scenario a sub-agent's detail shows, with its description; the key is what is stored. */
function scenarioOf(agent: SubDraft): AgentScenarioView | undefined {
  return view.value?.scenarios.find((scenario) => scenario.key === agent.scenario)
}

/**
 * Whether a scenario can actually be reached.
 *
 * Read from the server's answer rather than recomputed: "available" is "the capability is on **and** an enabled
 * agent owns it", and a page that re-derived it from the two switches would disagree with the server the moment
 * either rule changed. A draft the server has not seen yet has no answer, and is not called unavailable.
 */
function availableOf(agent: SubDraft): boolean {
  return scenarioOf(agent)?.available !== false
}

/** The scenario's `when` for the detail pane, by key: a draft's own object is not in the server's list yet. */
function whenOf(key: string): string {
  return view.value?.scenarios.find((scenario) => scenario.key === key)?.when ?? ''
}

/** The scenario's name for the detail pane, by key, falling back to the key the server would store. */
function nameOf(key: string): string {
  return view.value?.scenarios.find((scenario) => scenario.key === key)?.name ?? key
}

/** The selected agent's capability, so the page can say when it is not on. */
const capabilityOff = computed(() => {
  const current = selectedSubView.value
  if (current === null || view.value === null) {
    return ''
  }
  if (current.scenario === 'desktop' && !view.value.desktopEnabled) {
    return t('对应能力没有开启：桌面操控')
  }
  if (current.scenario === 'file-read' && !view.value.files.enabled) {
    return t('对应能力没有开启：读文件')
  }
  if (current.scenario === 'web-search' && !view.value.webEnabled) {
    return t('对应能力没有开启：联网搜索')
  }
  return ''
})

/**
 * The thinking gears a custom model may use: the same menu the settings page edits. A page that cannot read it
 * falls back to the six usual words, and an unknown value in a stored sub-agent still shows (the dial adds it).
 */
/** The segments for one agent: the menu, plus the agent's own value when the menu no longer carries it. */
function gearChoices(current: string) {
  const list = current !== '' && !gears.value.includes(current) ? [...gears.value, current] : gears.value
  return list.map((value) => ({ value, label: gearName(value) }))
}

const gears = ref<string[]>(['off', 'low', 'medium', 'high', 'xhigh', 'max'])

onMounted(async () => {
  await refresh()
  try {
    const settings = await props.api.settings()
    if (settings.model.reasoningGears?.length > 0) gears.value = [...settings.model.reasoningGears]
    setGearLabels(settings.model.reasoningLabels)
  } catch {
    // The usual six are a fine menu.
  }
})

function hasUnsavedChanges(): boolean {
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="scroll prompts-scroll">
      <div v-if="view === null" class="g-page">
        <p v-if="error" class="error" data-test="error">{{ error }}</p>
        <p v-else class="pending">{{ t('正在读取指令…') }}</p>
      </div>

      <div v-else class="g-page is-wide">
        <!--
          The server's own warnings, at the top rather than beside the field they concern: a problem is usually a
          pair of settings (a capability on, the agent that would use it off), and no single field is the place.
        -->
        <ul v-if="view.problems.length > 0" class="problems" data-test="prompts-problems">
          <li v-for="(problem, index) in view.problems" :key="index">{{ problem }}</li>
        </ul>

        <div class="split">
          <section class="list-column" :aria-label="t('Agent 列表')">
            <ul class="agents g-glass" role="list">
              <li
                :class="{ active: mainSelected }"
                data-test="prompts-main"
                tabindex="0"
                @click="pick('main')"
                @keydown.enter="pick('main')"
                @keydown.space.prevent="pick('main')"
              >
                <Icon name="prompt" :size="16" />
                <span class="agent-name">{{ t('主 Agent') }}</span>
              </li>

              <li
                v-for="agent in subs"
                :key="agent.key"
                :class="{ active: agent.key === selectedKey }"
                data-test="prompts-agent"
                tabindex="0"
                @click="pick(agent.key)"
                @keydown.enter="pick(agent.key)"
                @keydown.space.prevent="pick(agent.key)"
              >
                <input
                  type="checkbox"
                  class="g-switch is-small"
                  role="switch"
                  :checked="agent.enabled"
                  :aria-label="t('启用「{name}」', { name: agent.name || t('（未命名）') })"
                  data-test="prompts-agent-enabled"
                  @click.stop
                  @change="agent.enabled = ($event.target as HTMLInputElement).checked"
                />
                <span class="agent-name">{{ agent.name || t('（未命名）') }}</span>
                <span v-if="isBuiltin(agent)" class="g-chip" data-test="prompts-agent-builtin">{{ t('内置') }}</span>
                <span class="scenario">{{ nameOf(agent.scenario) }}</span>
                <span v-if="!availableOf(agent)" class="g-chip is-warn" data-test="prompts-agent-unavailable">
                  {{ t('暂不可用') }}
                  <HintTip align="end">{{ t('要么对应的能力没开，要么没有启用的子 agent 负责它。') }}</HintTip>
                </span>
              </li>
            </ul>

            <button
              class="ghost add"
              :disabled="!canAdd"
              :title="addBlockedReason"
              data-test="prompts-add"
              @click="openAdd"
            >
              <Icon name="plus" :size="14" /> {{ t('新增子 agent') }}
            </button>
          </section>

          <section class="detail g-glass" :aria-label="t('Agent 详情')">
            <!-- Main agent: a prompt, a default, and the generated delegation block underneath. -->
            <template v-if="mainSelected">
              <header class="detail-head">
                <h2>{{ t('主 Agent') }}</h2>
                <span v-if="main.prompt.trim() !== ''" class="g-chip is-accent" data-test="prompts-main-modified">{{ t('已修改') }}</span>
                <HintTip>
                  {{ t('主 Agent 负责和你对话，并决定要不要把活交给子 agent。「实际发送的指令」里有它读得到的任务清单， 那部分由服务端生成，不能改。') }}
                </HintTip>
              </header>

              <div class="block">
                <h3>{{ t('指令') }} <HintTip>{{ t('告诉这个 Agent「你是谁、该怎么做事」。留空使用默认指令。') }}</HintTip></h3>
                <textarea
                  :value="main.prompt"
                  rows="12"
                  :placeholder="view.main.defaultPrompt"
                  :aria-label="t('主 Agent 的指令')"
                  data-test="prompts-main-textarea"
                  @input="main.prompt = ($event.target as HTMLTextAreaElement).value"
                />
                <div class="meter-row">
                  <button class="ghost" :disabled="main.prompt.trim() === ''" data-test="prompts-main-reset" @click="resetMain">
                    {{ t('恢复默认') }}
                  </button>
                  <span class="spacer" />
                  <span class="meter" :class="{ error: mainTokens > limit, 'is-near': mainTokens > limit * 0.85 }" data-test="prompts-main-tokens">
                    {{ t('约 {mainTokens} token / 上限 {limit}', { mainTokens: mainTokens, limit: limit }) }}
                  </span>
                </div>
                <div class="bar" aria-hidden="true">
                  <div class="bar-fill" :class="{ 'is-near': mainTokens > limit * 0.85, 'is-over': mainTokens > limit }" :style="{ width: Math.min(100, (mainTokens / Math.max(1, limit)) * 100) + '%' }" />
                </div>
              </div>

              <button
                class="ghost preview-toggle"
                :aria-expanded="previewOpen === 'main'"
                data-test="prompts-main-preview-toggle"
                @click="previewOpen = previewOpen === 'main' ? null : 'main'"
              >
                {{ previewOpen === 'main' ? t('收起实际发送的指令') : t('查看实际发送的指令') }}
              </button>
              <pre v-if="previewOpen === 'main'" class="effective" data-test="prompts-main-preview">{{ view.main.effectivePrompt }}</pre>
            </template>

            <!-- Sub-agent: the same prompt editor plus what makes it a sub-agent. -->
            <template v-else-if="selectedSub">
              <header class="detail-head">
                <input
                  v-model="selectedSub.name"
                  type="text"
                  class="name-input"
                  :aria-label="t('子 agent 名字')"
                  data-test="prompts-agent-name"
                />
                <span v-if="isBuiltin(selectedSub)" class="g-chip" data-test="prompts-agent-builtin">{{ t('内置') }}</span>
                <span class="spacer" />
                <input v-model="selectedSub.enabled" type="checkbox" class="g-switch" role="switch" :aria-label="t('启用')" data-test="prompts-agent-toggle" />
              </header>

              <div class="block">
                <div class="scenario-row">
                  <span class="label">{{ t('负责的任务') }} <HintTip>{{ whenOf(selectedSub.scenario) || t('这个子 agent 在什么情况下被叫来干活。同一类任务只能有一个子 agent。') }}</HintTip></span>
                  <span class="g-chip is-accent" data-test="prompts-agent-scenario">{{ nameOf(selectedSub.scenario) }}</span>
                </div>
                <p v-if="capabilityOff" class="warn-line" data-test="prompts-agent-capability">
                  {{ capabilityOff }}
                  <button class="ghost" data-test="prompts-agent-goto-settings" @click="emit('navigate', 'settings')">
                    {{ t('去设置页开启') }}
                  </button>
                </p>
              </div>

              <div class="block">
                <h3>{{ t('指令') }}
                  <span v-if="selectedSub.prompt.trim() !== ''" class="g-chip is-accent" data-test="prompts-agent-modified">{{ t('已修改') }}</span>
                  <HintTip>{{ t('告诉这个 Agent「你是谁、该怎么做事」。留空使用默认指令。') }}</HintTip>
                </h3>
                <textarea
                  v-model="selectedSub.prompt"
                  rows="10"
                  :placeholder="selectedSubView?.defaultPrompt ?? ''"
                  :aria-label="t('子 agent 的指令')"
                  data-test="prompts-agent-textarea"
                />
                <div class="meter-row">
                  <button class="ghost" :disabled="selectedSub.prompt.trim() === ''" data-test="prompts-agent-reset" @click="resetSubPrompt">
                    {{ t('恢复默认') }}
                  </button>
                  <span class="spacer" />
                  <span class="meter" :class="{ error: selectedSubTokens > limit, 'is-near': selectedSubTokens > limit * 0.85 }" data-test="prompts-agent-tokens">
                    {{ t('约 {selectedSubTokens} token / 上限 {limit}', { selectedSubTokens: selectedSubTokens, limit: limit }) }}
                  </span>
                </div>
                <div class="bar" aria-hidden="true">
                  <div class="bar-fill" :class="{ 'is-near': selectedSubTokens > limit * 0.85, 'is-over': selectedSubTokens > limit }" :style="{ width: Math.min(100, (selectedSubTokens / Math.max(1, limit)) * 100) + '%' }" />
                </div>
                <button
                  class="ghost preview-toggle"
                  :aria-expanded="previewOpen === selectedSub.key"
                  data-test="prompts-agent-preview-toggle"
                  @click="previewOpen = previewOpen === selectedSub.key ? null : selectedSub.key"
                >
                  {{ previewOpen === selectedSub.key ? t('收起实际发送的指令') : t('查看实际发送的指令') }}
                </button>
                <pre
                  v-if="previewOpen === selectedSub.key"
                  class="effective"
                  data-test="prompts-agent-preview"
                >{{ selectedSubView?.effectivePrompt }}</pre>
              </div>

              <div class="block rows">
                <h3>{{ t('模型') }}</h3>
                <SettingRow :label="t('使用哪个模型')" :hint="t('默认用和主 Agent 同一个模型。')">
                  <Segmented
                    :model-value="selectedSub.model === null ? 'main' : 'custom'"
                    :options="[
                      { value: 'main', label: t('跟随主 Agent') },
                      { value: 'custom', label: t('单独设置') },
                    ]"
                    :label="t('模型来源')"
                    data-test="prompts-agent-model-mode"
                    @update:model-value="setCustomModel($event === 'custom')"
                  />
                </SettingRow>

                <template v-if="selectedSub.model">
                  <SettingRow :label="t('接口格式')" :hint="t('OpenAI 兼容端点选 OpenAI；Claude 或兼容 Anthropic Messages 的端点选 Anthropic。')">
                    <select v-model="selectedSub.model.protocol" data-test="prompts-agent-protocol">
                      <option value="openai">{{ t('OpenAI 兼容') }}</option>
                      <option value="anthropic">Anthropic (Claude)</option>
                    </select>
                  </SettingRow>
                  <SettingRow :label="t('接口地址')" :hint="t('模型服务的网址，一般以 /v1 结尾。')">
                    <input v-model="selectedSub.model.baseUrl" type="text" data-test="prompts-agent-base-url" />
                  </SettingRow>
                  <SettingRow :label="t('模型')">
                    <input v-model="selectedSub.model.model" type="text" data-test="prompts-agent-model" />
                  </SettingRow>
                  <SettingRow label="API Key" :hint="t('保存后不会再显示出来。留空表示不改；本地模型一般不需要。')">
                    <input
                      type="password"
                      :value="selectedSub.apiKeyEdited ? selectedSub.model.apiKey ?? '' : ''"
                      :placeholder="keySetFor(selectedSub) ? t('（已设置，留空保持不变）') : t('（未设置）')"
                      autocomplete="off"
                      data-test="prompts-agent-api-key"
                      @input="editKey(selectedSub, ($event.target as HTMLInputElement).value)"
                    />
                    <span class="g-chip" :class="keySetFor(selectedSub) ? 'is-ok' : ''" data-test="prompts-agent-api-key-state">
                      {{ keySetFor(selectedSub) ? t('已设置') : t('未设置') }}
                    </span>
                  </SettingRow>
                  <SettingRow :label="t('思考强度')" :hint="t('回答前想多久。越高通常越准，但越慢、越费。')">
                    <Segmented
                      v-model="selectedSub.model.reasoningEffort"
                      :options="gearChoices(selectedSub.model.reasoningEffort)"
                      :label="t('思考强度')"
                      data-test="prompts-agent-reasoning"
                    />
                  </SettingRow>
                  <SettingRow :label="t('能看图片')" :hint="t('纯文字模型请保持关闭，否则请求会失败。')">
                    <input v-model="selectedSub.model.supportsImages" type="checkbox" class="g-switch" role="switch" data-test="prompts-agent-images" />
                  </SettingRow>
                  <SettingRow :label="t('最长等待')" :unit="t('秒')" :hint="t('模型超过这个时间还没答完，就放弃这次请求。')">
                    <input v-model.number="selectedSub.model.timeoutSeconds" type="number" min="1" />
                  </SettingRow>
                  <SettingRow :label="t('记忆长度')" unit="token" :hint="t('模型一次最多能「看」多少内容，约 1 个汉字算 1 token。')">
                    <input v-model.number="selectedSub.model.contextTokens" type="number" min="1" />
                  </SettingRow>
                  <SettingRow :label="t('单次回复上限')" unit="token" :hint="t('一次回复最多写多长，必须小于「记忆长度」。')">
                    <input v-model.number="selectedSub.model.maxTokens" type="number" min="1" />
                  </SettingRow>
                </template>
              </div>

              <div class="block rows">
                <SettingRow :label="t('最多尝试几次')" :unit="t('次')">
                  <template #hint>{{ t('一个任务最多试几轮（1–{maxRounds}），默认 {defaultMaxRounds}。全都失败时，会把原因告诉主 Agent。', { maxRounds: maxRounds, defaultMaxRounds: defaultMaxRounds }) }}</template>
                  <input
                    v-model.number="selectedSub.maxRounds"
                    type="number"
                    min="1"
                    :max="maxRounds"
                    data-test="prompts-agent-rounds"
                  />
                </SettingRow>
              </div>

              <div v-if="!isBuiltin(selectedSub)" class="block">
                <button
                  class="ghost danger remove"
                  :class="{ 'is-armed': confirmingRemove }"
                  data-test="prompts-agent-delete"
                  @click="removeSelected"
                >
                  <Icon name="trash" :size="14" /> {{ confirmingRemove ? t('确认删除') : t('删除这个子 agent') }}
                </button>
              </div>
            </template>
          </section>
        </div>
      </div>
    </div>

    <Dialog :open="adding" :title="t('新增子 agent')" @close="adding = false">
      <div v-if="view" data-test="prompts-add-form" class="add-form">
        <label class="add-field">
          <span>{{ t('名字') }}</span>
          <input v-model="newName" type="text" :placeholder="t('例如：读资料的')" data-test="prompts-add-name" />
        </label>
        <label class="add-field">
          <span>{{ t('负责的任务') }} <HintTip>{{ t('同一类任务只能有一个子 agent。') }}</HintTip></span>
          <select v-model="newScenario" data-test="prompts-add-scenario">
            <option
              v-for="scenario in view.scenarios"
              :key="scenario.key"
              :value="scenario.key"
              :disabled="scenario.ownerId !== null"
            >
              {{ scenario.name }}{{ scenario.ownerId !== null
                ? scenario.key === 'desktop'
                  ? t(' · 已被内置的桌面操控占用')
                  : t(' · 已被占用')
                : '' }}
            </option>
          </select>
        </label>
        <p v-if="addError" class="error" data-test="prompts-add-error">{{ addError }}</p>
      </div>
      <template #footer>
        <button class="ghost" data-test="prompts-add-cancel" @click="adding = false">{{ t('取消') }}</button>
        <button class="primary" data-test="prompts-add-commit" @click="commitAdd">{{ t('添加') }}</button>
      </template>
    </Dialog>

    <SaveBar
      prefix="prompts"
      :dirty="dirty"
      :status="notice"
      :error="error"
      :errors="errors"
      :blocked-reason="overLimit ? t('指令太长了，先缩短再保存') : ''"
      can-discard
      @save="save"
      @discard="discard"
    />
  </div>
</template>

<style scoped>
.prompts-scroll {
  flex: 1;
  min-block-size: 0;
}

/* The server's warnings: a tint rather than a border, so they read as a banner over the page rather than as
   one more card in the form. */
.problems {
  margin: 0;
  padding: var(--space-3) var(--space-5);
  list-style: none;
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--warn) 12%, transparent);
  color: var(--warn);
  font-size: var(--fs-ui);
  line-height: 1.6;
}

/* Two columns: the agents, and the one being edited. The list is narrow because a name and a switch are all
   it holds; the editor takes whatever is left. */
.split {
  display: grid;
  grid-template-columns: minmax(14rem, 18rem) minmax(0, 1fr);
  gap: var(--space-4);
  align-items: start;
}

.list-column {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
  align-content: start;
  position: sticky;
  inset-block-start: calc(var(--topbar-h) + var(--space-4));
}

.agents {
  list-style: none;
  margin: 0;
  padding: var(--space-2);
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 2px;
}

.agents > li {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--space-2);
  min-block-size: 2.75rem;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  cursor: pointer;
  min-inline-size: 0;
  outline: none;
}

.agents > li:hover {
  background: var(--surface-2);
}

.agents > li:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

/* Selection is a soft fill, the same as the rail. */
.agents > li.active {
  background: var(--accent-soft);
  color: var(--accent-pressed);
  font-weight: 500;
}

.agent-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.scenario {
  margin-inline-start: auto;
  flex: none;
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.add {
  justify-self: start;
  color: var(--text-2);
}

.detail {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-5);
  padding: var(--space-5) var(--space-6);
  min-inline-size: 0;
}

.detail-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.detail-head h2 {
  font-size: var(--fs-title);
}

.name-input {
  font-size: 1.125rem;
  font-weight: 600;
  inline-size: min(100%, 18rem);
}

.block {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
  min-inline-size: 0;
}

.block.rows {
  gap: 0;
}

.block h3 {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.label {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}

.scenario-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
}

textarea {
  font-size: var(--fs-ui);
  line-height: 1.7;
}

.meter-row {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.meter {
  color: var(--text-3);
  font-size: var(--fs-meta);
  font-variant-numeric: tabular-nums;
}

.meter.is-near {
  color: var(--warn);
}

.meter.error {
  color: var(--error);
}

.bar {
  block-size: 4px;
  border-radius: var(--radius-pill);
  background: var(--surface-2);
  overflow: hidden;
}

.bar-fill {
  block-size: 100%;
  border-radius: inherit;
  background: var(--accent);
  transition: width var(--motion-base) var(--ease);
}

.bar-fill.is-near {
  background: var(--warn);
}

.bar-fill.is-over {
  background: var(--error);
}

.preview-toggle {
  justify-self: start;
  color: var(--text-2);
}

/* Read-only, and shown as the document it is: monospace, preserved whitespace, its own scroll so a long
   prompt does not push the save bar off the page. */
.effective {
  margin: 0;
  padding: var(--space-3) var(--space-4);
  max-block-size: 24rem;
  overflow: auto;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-family: var(--font-mono);
  font-size: var(--fs-meta);
  line-height: var(--lh-code);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.warn-line {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--warn) 12%, transparent);
  color: var(--warn);
  font-size: var(--fs-meta);
}

.warn-line button {
  min-block-size: 1.75rem;
  font-size: var(--fs-meta);
}

.remove.is-armed {
  background: color-mix(in srgb, var(--error) 12%, transparent);
}

.add-form {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-4);
}

.add-field {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
}

.add-field > span {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--fs-ui);
  color: var(--text-2);
}

/* Narrow: the list goes above the editor. A 14rem list beside a form on a 600px window would leave the form
   narrower than the prompt it edits. */
@container body (max-width: 52rem) {
  .split {
    grid-template-columns: minmax(0, 1fr);
  }

  .list-column {
    position: static;
  }
}
</style>
