<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Icon from '../components/Icon.vue'
import HintTip from '../components/HintTip.vue'
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
    notice.value = `已保存 · ${new Date().toLocaleTimeString()}`
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

/** Throws the drafts away and re-reads the server: the page never keeps an edit nobody stored. */
async function discard() {
  if (!window.confirm('放弃未保存的修改？')) {
    return
  }

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
const addBlockedReason = computed(() => (canAdd.value ? '' : '两个场景都已有子 agent'))

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
    addError.value = '先选一个场景'
    return
  }
  if (newName.value.trim() === '') {
    addError.value = '子 agent 需要一个名字'
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

function removeSelected() {
  const current = selectedSub.value
  if (current === null || isBuiltin(current)) {
    return
  }
  if (!window.confirm(`删除子 agent「${current.name || '（未命名）'}」？`)) {
    return
  }

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
    return '对应能力没有开启：桌面操控'
  }
  if (current.scenario === 'file-read' && !view.value.files.enabled) {
    return '对应能力没有开启：读文件'
  }
  return ''
})

onMounted(refresh)

function hasUnsavedChanges(): boolean {
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="prompts-refresh" @click="refresh">刷新</button>
      <span class="spacer" />
      <span v-if="dirty" class="muted" data-test="prompts-dirty">有未保存的修改</span>
      <span class="muted" data-test="prompts-status">{{ notice }}</span>
    </div>

    <div v-if="view === null" class="scroll">
      <p v-if="error" class="error notice" data-test="error">{{ error }}</p>
      <p v-else class="pending">正在读取提示词…</p>
    </div>

    <div v-else class="scroll">
      <!--
        The server's own warnings, at the top rather than beside the field they concern: a problem is usually a
        pair of settings (a capability on, the agent that would use it off), and no single field is the place.
      -->
      <ul v-if="view.problems.length > 0" class="problems" data-test="prompts-problems">
        <li v-for="(problem, index) in view.problems" :key="index">{{ problem }}</li>
      </ul>

      <div class="split">
        <section class="list-column" aria-label="Agent 列表">
          <ul class="agents" role="list">
            <li
              :class="{ active: mainSelected }"
              data-test="prompts-main"
              tabindex="0"
              @click="selectedKey = 'main'"
              @keydown.enter="selectedKey = 'main'"
              @keydown.space.prevent="selectedKey = 'main'"
            >
              <Icon name="prompt" :size="16" />
              <span class="agent-name">主 Agent</span>
            </li>

            <li
              v-for="agent in subs"
              :key="agent.key"
              :class="{ active: agent.key === selectedKey }"
              data-test="prompts-agent"
              tabindex="0"
              @click="selectedKey = agent.key"
              @keydown.enter="selectedKey = agent.key"
              @keydown.space.prevent="selectedKey = agent.key"
            >
              <input
                type="checkbox"
                :checked="agent.enabled"
                :aria-label="`启用「${agent.name || '（未命名）'}」`"
                data-test="prompts-agent-enabled"
                @click.stop
                @change="agent.enabled = ($event.target as HTMLInputElement).checked"
              />
              <span class="agent-name">{{ agent.name || '（未命名）' }}</span>
              <span v-if="isBuiltin(agent)" class="badge" data-test="prompts-agent-builtin">内置</span>
              <span class="scenario">{{ nameOf(agent.scenario) }}</span>
              <span v-if="!availableOf(agent)" class="unavailable" data-test="prompts-agent-unavailable">
                暂不可用
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
            ＋ 新增子 agent
          </button>

          <div v-if="adding" class="add-form" data-test="prompts-add-form">
            <label class="field">
              <span>名字</span>
              <input v-model="newName" type="text" data-test="prompts-add-name" />
            </label>
            <label class="field">
              <span>场景</span>
              <select v-model="newScenario" data-test="prompts-add-scenario">
                <option
                  v-for="scenario in view.scenarios"
                  :key="scenario.key"
                  :value="scenario.key"
                  :disabled="scenario.ownerId !== null"
                >
                  {{ scenario.name }}{{ scenario.ownerId !== null
                    ? scenario.key === 'desktop'
                      ? ' · 已被内置的桌面操控占用'
                      : ' · 已被占用'
                    : '' }}
                </option>
              </select>
            </label>
            <p v-if="addError" class="error" data-test="prompts-add-error">{{ addError }}</p>
            <div class="row">
              <button class="primary" data-test="prompts-add-commit" @click="commitAdd">添加</button>
              <button class="ghost" data-test="prompts-add-cancel" @click="adding = false">取消</button>
            </div>
          </div>
        </section>

        <section class="detail" aria-label="Agent 详情">
          <!-- Main agent: a prompt, a default, and the generated delegation block underneath. -->
          <template v-if="mainSelected">
            <h2 class="detail-title">
              主 Agent
              <span v-if="main.prompt.trim() !== ''" class="badge" data-test="prompts-main-modified">已修改</span>
              <HintTip>
                主 agent 判断该不该把活交给子 agent。下面的「模型实际看到的提示词」里有它读得到的场景清单，
                那部分由服务端生成，不能改。
              </HintTip>
            </h2>

            <label class="field prompt-box">
              <span>提示词</span>
              <textarea
                :value="main.prompt"
                rows="14"
                :placeholder="view.main.defaultPrompt"
                data-test="prompts-main-textarea"
                @input="main.prompt = ($event.target as HTMLTextAreaElement).value"
              />
            </label>

            <div class="row">
              <button
                class="ghost"
                :disabled="main.prompt.trim() === ''"
                data-test="prompts-main-reset"
                @click="resetMain"
              >
                恢复默认
              </button>
              <span
                class="muted"
                :class="{ error: mainTokens > limit }"
                data-test="prompts-main-tokens"
              >
                约 {{ mainTokens }} token / 上限 {{ limit }}
              </span>
            </div>

            <button
              class="ghost preview-toggle"
              :aria-expanded="previewOpen === 'main'"
              data-test="prompts-main-preview-toggle"
              @click="previewOpen = previewOpen === 'main' ? null : 'main'"
            >
              {{ previewOpen === 'main' ? '收起' : '展开' }}模型实际看到的提示词
            </button>
            <pre v-if="previewOpen === 'main'" class="effective" data-test="prompts-main-preview">{{ view.main.effectivePrompt }}</pre>
          </template>

          <!-- Sub-agent: the same prompt editor plus what makes it a sub-agent. -->
          <template v-else-if="selectedSub">
            <h2 class="detail-title">
              <input
                v-model="selectedSub.name"
                type="text"
                aria-label="子 agent 名字"
                data-test="prompts-agent-name"
              />
              <span v-if="isBuiltin(selectedSub)" class="badge" data-test="prompts-agent-builtin">内置</span>
            </h2>

            <label class="field">
              <span>启用</span>
              <input v-model="selectedSub.enabled" type="checkbox" data-test="prompts-agent-toggle" />
            </label>

            <div class="field">
              <span>场景 <HintTip>{{ whenOf(selectedSub.scenario) }}</HintTip></span>
              <span class="scenario-text" data-test="prompts-agent-scenario">
                {{ nameOf(selectedSub.scenario) }}
              </span>
            </div>

            <p v-if="capabilityOff" class="card-note error" data-test="prompts-agent-capability">
              {{ capabilityOff }}
              <button class="ghost" data-test="prompts-agent-goto-settings" @click="emit('navigate', 'settings')">
                去设置页开启
              </button>
            </p>

            <div class="field">
              <span>模型</span>
              <select
                :value="selectedSub.model === null ? 'main' : 'custom'"
                data-test="prompts-agent-model-mode"
                @change="setCustomModel(($event.target as HTMLSelectElement).value === 'custom')"
              >
                <option value="main">跟随主模型（默认）</option>
                <option value="custom">自定义</option>
              </select>
            </div>

            <template v-if="selectedSub.model">
              <label class="field">
                <span>服务地址</span>
                <input v-model="selectedSub.model.baseUrl" type="text" data-test="prompts-agent-base-url" />
              </label>
              <label class="field">
                <span>模型名</span>
                <input v-model="selectedSub.model.model" type="text" data-test="prompts-agent-model" />
              </label>
              <label class="field">
                <span>API Key</span>
                <input
                  type="password"
                  :value="selectedSub.apiKeyEdited ? selectedSub.model.apiKey ?? '' : ''"
                  :placeholder="keySetFor(selectedSub) ? '（已设置，留空保持不变）' : '（未设置）'"
                  data-test="prompts-agent-api-key"
                  @input="editKey(selectedSub, ($event.target as HTMLInputElement).value)"
                />
                <span class="source" data-test="prompts-agent-api-key-state">
                  {{ keySetFor(selectedSub) ? '已设置' : '未设置' }}
                </span>
              </label>
              <label class="field">
                <span>请求超时 (s)</span>
                <input v-model.number="selectedSub.model.timeoutSeconds" type="number" min="1" />
              </label>
              <label class="field">
                <span>上下文大小（token）</span>
                <input v-model.number="selectedSub.model.contextTokens" type="number" min="1" />
              </label>
              <label class="field">
                <span>输出长度（token）</span>
                <input v-model.number="selectedSub.model.maxTokens" type="number" min="1" />
              </label>
              <label class="field">
                <span>支持图片（视觉）</span>
                <input v-model="selectedSub.model.supportsImages" type="checkbox" data-test="prompts-agent-images" />
              </label>
              <label class="field">
                <span>思考挡位</span>
                <input v-model="selectedSub.model.reasoningEffort" type="text" placeholder="medium" />
              </label>
            </template>

            <label class="field">
              <span>最大轮数 <HintTip>一次任务最多试几轮（1–{{ maxRounds }}），默认 {{ defaultMaxRounds }}</HintTip></span>
              <input
                v-model.number="selectedSub.maxRounds"
                type="number"
                min="1"
                :max="maxRounds"
                data-test="prompts-agent-rounds"
              />
            </label>

            <label class="field prompt-box">
              <span>
                提示词
                <span v-if="selectedSub.prompt.trim() !== ''" class="badge" data-test="prompts-agent-modified">已修改</span>
              </span>
              <textarea
                v-model="selectedSub.prompt"
                rows="12"
                :placeholder="selectedSubView?.defaultPrompt ?? ''"
                data-test="prompts-agent-textarea"
              />
            </label>

            <div class="row">
              <button
                class="ghost"
                :disabled="selectedSub.prompt.trim() === ''"
                data-test="prompts-agent-reset"
                @click="resetSubPrompt"
              >
                恢复默认
              </button>
              <span
                class="muted"
                :class="{ error: selectedSubTokens > limit }"
                data-test="prompts-agent-tokens"
              >
                约 {{ selectedSubTokens }} token / 上限 {{ limit }}
              </span>
            </div>

            <button
              class="ghost preview-toggle"
              :aria-expanded="previewOpen === selectedSub.key"
              data-test="prompts-agent-preview-toggle"
              @click="previewOpen = previewOpen === selectedSub.key ? null : selectedSub.key"
            >
              {{ previewOpen === selectedSub.key ? '收起' : '展开' }}模型实际看到的提示词
            </button>
            <pre
              v-if="previewOpen === selectedSub.key"
              class="effective"
              data-test="prompts-agent-preview"
            >{{ selectedSubView?.effectivePrompt }}</pre>

            <div v-if="!isBuiltin(selectedSub)" class="row">
              <button class="ghost danger" data-test="prompts-agent-delete" @click="removeSelected">删除</button>
            </div>
          </template>
        </section>
      </div>
    </div>

    <div class="toolbar footer-actions">
      <button
        class="primary"
        :disabled="!dirty || overLimit"
        data-test="prompts-save"
        @click="save"
      >
        保存
      </button>
      <button class="ghost" :disabled="!dirty" data-test="prompts-discard" @click="discard">放弃修改</button>
      <span v-if="overLimit" class="error" data-test="prompts-over-limit">提示词超出上限，先缩短再保存</span>
      <span v-if="error" class="error" data-test="error">{{ error }}</span>
      <ul v-if="errors.length > 0" class="save-errors" data-test="prompts-errors">
        <li v-for="(entry, index) in errors" :key="index">{{ entry }}</li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.scroll {
  flex: 1;
  min-block-size: 0;
  overflow-y: auto;
  padding: var(--space-3);
  display: grid;
  gap: var(--space-3);
  align-content: start;
}

/* The server's warnings: a tint rather than a border, so they read as a banner over the page rather than as
   one more card in the form. */
.problems {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--warn) 10%, transparent);
  color: var(--warn);
  font-size: var(--fs-meta);
  line-height: var(--lh-meta);
}

/* Two columns: the list of agents, and the one being edited. The list is narrow because a name and a switch
   are all it holds; the prompt editor takes whatever is left. */
.split {
  display: grid;
  grid-template-columns: minmax(14rem, 20rem) minmax(0, 1fr);
  gap: var(--space-3);
  align-items: start;
}

.list-column {
  display: grid;
  gap: var(--space-2);
  align-content: start;
}

.agents {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 2px;
}

.agents > li {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2);
  border-radius: var(--radius-sm);
  cursor: pointer;
  min-inline-size: 0;
}

.agents > li:hover {
  background: var(--surface-2);
}

/* Selection is a thin accent bar plus a light fill, the same as the rail (ui.md §4). */
.agents > li.active {
  background: var(--selected);
  box-shadow: inset 2px 0 0 var(--accent-pressed);
}

.agent-name {
  font-size: var(--fs-ui);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.badge {
  flex: none;
  padding: 0 var(--space-1);
  border: 1px solid var(--line-strong);
  border-radius: var(--radius-sm);
  font-size: var(--fs-meta);
  line-height: 1.5;
  color: var(--text-2);
}

.scenario {
  margin-inline-start: auto;
  flex: none;
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.unavailable {
  flex: none;
  font-size: var(--fs-meta);
  color: var(--warn);
}

.add {
  justify-self: start;
}

.add-form {
  display: grid;
  gap: var(--space-2);
  padding: var(--space-2);
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.detail {
  display: grid;
  gap: var(--space-2);
  padding: var(--space-3);
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  min-inline-size: 0;
}

.detail-title {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin: 0;
  font-size: var(--fs-title);
}

.detail-title input {
  font: inherit;
  font-weight: 600;
}

/* The prompt box is the page's main control, so it gets its own row rather than the label/control split the
   global `.field` gives numeric inputs. */
.prompt-box {
  grid-template-columns: minmax(0, 1fr);
}

.prompt-box textarea {
  inline-size: 100%;
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
  resize: vertical;
}

.scenario-text {
  font-size: var(--fs-ui);
}

.preview-toggle {
  justify-self: start;
}

/* Read-only, and shown as the document it is: monospace, preserved whitespace, its own scroll so a long
   prompt does not push the Save button off the page. */
.effective {
  margin: 0;
  padding: var(--space-2);
  max-block-size: 24rem;
  overflow: auto;
  border-radius: var(--radius-sm);
  background: var(--surface-0);
  font-family: var(--font-mono);
  font-size: var(--fs-meta);
  line-height: var(--lh-code);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.footer-actions {
  border-block-start: 1px solid var(--line);
  border-block-end: none;
  flex-wrap: wrap;
}

.save-errors {
  margin: 0;
  padding-inline-start: var(--space-4);
  color: var(--error);
  font-size: var(--fs-meta);
  flex-basis: 100%;
}

/* Narrow: the list goes above the editor. A 14rem list beside a form on a 600px window would leave the form
   narrower than the prompt it edits. */
@container body (max-width: 52rem) {
  .split {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
