<script setup lang="ts">
import { locale, t } from '../i18n'
import { computed, onMounted, ref } from 'vue'
import type {
  PartnerApiSurface,
  ServiceListItem,
  ServiceSettingsView,
  ServiceState,
  ServiceWrite,
} from '../api'
import { showToast } from '../toast'
import Dialog from '../components/Dialog.vue'
import Icon from '../components/Icon.vue'
import SaveBar from '../components/SaveBar.vue'
import Segmented from '../components/Segmented.vue'
import SettingRow from '../components/SettingRow.vue'

/**
 * The external memory services: one reverse-proxy port each, transparently forwarding to an upstream model
 * while injecting recalled memories and writing one-line summaries back into the main store.
 *
 * Editing is a per-service write (create/update/delete endpoints), not one settings document. The SaveBar
 * commits every changed card in one pass; the state line and the one-off actions (flush / rotate / delete)
 * act on a single service immediately and re-read just that one, so a draft on another card is never lost.
 */
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ changed: []; navigate: [key: string] }>()

interface Card {
  /** Empty for a card that has not been created yet. */
  id: string
  draft: ServiceWrite
  state: ServiceState
  tokenSet: boolean
  apiKeySet: boolean
  isNew: boolean
  /** The draft as last stored, so "changed" is a comparison and not a flag a handler remembered. */
  savedDraft: string
  open: boolean
}

const items = ref<Card[]>([])
const error = ref('')
const notice = ref('')
const attempted = ref(false)

/** A plaintext token, shown once after create/rotate and nowhere else. */
const revealed = ref<{ name: string; token: string } | null>(null)

/** The delete dialog's subject and its purge checkbox (unchecked by default). */
const deleteTarget = ref<Card | null>(null)
const purgeMemories = ref(false)

const PROTOCOLS = [
  { value: 'openai', label: 'OpenAI' },
  { value: 'anthropic', label: 'Anthropic' },
]

/** The view shape never carries secret bytes; the form starts them empty, which means "keep the stored one". */
function toWrite(s: ServiceSettingsView): ServiceWrite {
  return {
    id: s.id,
    name: s.name,
    enabled: s.enabled,
    port: s.port,
    protocol: s.protocol,
    model: s.model,
    contextTokens: s.contextTokens,
    prompt: s.prompt,
    recall: { ...s.recall },
    summary: { ...s.summary },
    upstream: {
      inherit: s.upstream.inherit,
      baseUrl: s.upstream.baseUrl,
      model: s.upstream.model,
      apiKey: '',
      maxTokens: s.upstream.maxTokens,
      timeoutSeconds: s.upstream.timeoutSeconds,
    },
    token: '',
  }
}

function blankWrite(): ServiceWrite {
  return {
    id: '',
    name: '',
    enabled: true,
    port: 8080,
    protocol: 'openai',
    model: '',
    contextTokens: 8192,
    prompt: '',
    recall: { enabled: true, limit: 8, tokenLimit: 2000 },
    summary: { enabled: true, intervalMinutes: 10, maxWaitMinutes: 60 },
    upstream: { inherit: true, baseUrl: null, model: null, apiKey: '', maxTokens: null, timeoutSeconds: null },
    token: '',
  }
}

function blankState(): ServiceState {
  return {
    running: false,
    error: null,
    port: 0,
    requests: 0,
    lastActivityAt: null,
    pendingRounds: 0,
    lastFlushAt: null,
    lastMemoryId: null,
  }
}

function toCard(item: ServiceListItem): Card {
  const draft = toWrite(item.settings)
  return {
    id: item.settings.id,
    draft,
    state: { ...item.state },
    tokenSet: item.settings.tokenSet,
    apiKeySet: item.settings.upstream.apiKeySet,
    isNew: false,
    savedDraft: JSON.stringify(draft),
    open: false,
  }
}

const dirty = computed(() =>
  items.value.some((card) => card.isNew || JSON.stringify(card.draft) !== card.savedDraft),
)

function changed(card: Card): boolean {
  return card.isNew || JSON.stringify(card.draft) !== card.savedDraft
}

async function refresh() {
  error.value = ''
  try {
    const list = await props.api.listServices()
    items.value = Array.isArray(list) ? list.map(toCard) : []
  } catch (failure) {
    error.value = String(failure)
  }
}

function addService() {
  items.value = [
    ...items.value,
    {
      id: '',
      draft: blankWrite(),
      state: blankState(),
      tokenSet: false,
      apiKeySet: false,
      isNew: true,
      savedDraft: '',
      open: true,
    },
  ]
}

function toggleOpen(card: Card) {
  card.open = !card.open
}

function setProtocol(card: Card, value: string) {
  card.draft.protocol = value === 'anthropic' ? 'anthropic' : 'openai'
}

function missing(card: Card, field: 'name' | 'model'): boolean {
  return attempted.value && card.draft[field].trim() === ''
}

/**
 * Recreated from the form's port, not the settings: the address a client actually calls, and the one a
 * just-added (still unsaved) card can already show.
 */
function baseUrl(card: Card): string {
  return `http://127.0.0.1:${card.draft.port}/v1`
}

/** A copyable curl example, generated per protocol. The token is a placeholder: it is never sent back. */
function curl(card: Card): string {
  const model = card.draft.model || 'MODEL'
  const anthropic = card.draft.protocol === 'anthropic'
  const path = anthropic ? 'messages' : 'chat/completions'
  const lines = [`curl ${baseUrl(card)}/${path}`]
  if (anthropic) {
    lines.push('-H "x-api-key: <token>"', '-H "anthropic-version: 2023-06-01"')
  } else {
    lines.push('-H "Authorization: Bearer <token>"')
  }
  lines.push('-H "Content-Type: application/json"')
  const body = anthropic
    ? `{"model":"${model}","max_tokens":1024,"messages":[{"role":"user","content":"你好"}]}`
    : `{"model":"${model}","messages":[{"role":"user","content":"你好"}]}`
  lines.push(`-d '${body}'`)
  return lines.join(' \\\n')
}

function fmt(iso: string | null): string {
  if (!iso) return '—'
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString(locale.value, { hour12: false })
}

async function copy(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    showToast(t('已复制'))
  } catch {
    showToast(t('复制失败'), 'error')
  }
}

async function save() {
  const invalid = items.value.findIndex(
    (card) =>
      card.draft.name.trim() === '' ||
      card.draft.model.trim() === '' ||
      !Number.isFinite(card.draft.port) ||
      card.draft.port < 1024 ||
      card.draft.port > 65535,
  )
  if (invalid >= 0) {
    attempted.value = true
    items.value[invalid]!.open = true
    error.value = t('每个服务都要有名称、模型名和有效端口')
    return
  }

  error.value = ''
  notice.value = ''
  try {
    for (const card of items.value) {
      if (card.isNew) {
        const created = await props.api.createService(card.draft)
        revealed.value = { name: card.draft.name, token: created.token }
      } else if (changed(card)) {
        await props.api.updateService(card.id, card.draft)
      }
    }
    await refresh()
    attempted.value = false
    notice.value = t('已保存')
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

/** Summarizes now, then re-reads only this service's live state so other drafts survive. */
async function flush(card: Card) {
  if (card.isNew) return
  error.value = ''
  try {
    await props.api.flushService(card.id)
    const fresh = await props.api.getService(card.id)
    card.state = { ...fresh.state }
    showToast(t('已总结'))
  } catch (failure) {
    error.value = String(failure)
  }
}

async function rotate(card: Card) {
  if (card.isNew) return
  error.value = ''
  try {
    const answer = await props.api.rotateServiceToken(card.id)
    card.tokenSet = true
    revealed.value = { name: card.draft.name || card.id, token: answer.token }
  } catch (failure) {
    error.value = String(failure)
  }
}

function requestDelete(card: Card) {
  if (card.isNew) {
    items.value = items.value.filter((entry) => entry !== card)
    return
  }
  deleteTarget.value = card
  purgeMemories.value = false
}

async function confirmDelete() {
  const card = deleteTarget.value
  if (!card) return
  error.value = ''
  try {
    await props.api.deleteService(card.id, purgeMemories.value)
    deleteTarget.value = null
    items.value = items.value.filter((entry) => entry !== card)
    showToast(t('已删除'))
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
    deleteTarget.value = null
  }
}

onMounted(refresh)

/** Asked by the shell before it switches away: the views are `v-if`, so leaving discards every edit. */
function hasUnsavedChanges(): boolean {
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="scroll services-scroll">
      <div class="g-page">
        <!-- A token is only ever returned in plaintext once, so it lives in a banner the reader copies now or loses. -->
        <div v-if="revealed" class="svc-reveal g-glass" data-test="services-token">
          <span class="svc-reveal-text">
            {{ t('token 只显示一次，请立即复制') }} · <strong>{{ revealed.name }}</strong>
            <code class="break svc-token">{{ revealed.token }}</code>
          </span>
          <button class="ghost" data-test="services-token-copy" @click="copy(revealed.token)">
            <Icon name="copy" :size="14" /> {{ t('复制') }}
          </button>
          <button class="ghost" :aria-label="t('关闭')" @click="revealed = null">
            <Icon name="x" :size="14" />
          </button>
        </div>

        <p class="summary-line">
          <span data-test="services-summary">{{ items.length }} {{ t('对外服务') }}</span>
          <button class="ghost refresh" :aria-label="t('刷新')" :title="t('刷新')" data-test="services-refresh" @click="refresh">
            <Icon name="refresh" :size="14" />
          </button>
        </p>

        <div class="svc-list">
          <p v-if="items.length === 0" class="empty" data-test="services-empty">
            {{ t('还没有对外服务，添加一个来对外提供记忆能力。') }}
          </p>

          <section v-for="(card, index) in items" :key="card.isNew ? `new-${index}` : card.id" class="svc-card g-glass" data-test="services-card">
            <header class="svc-head">
              <button class="svc-toggle" :aria-expanded="card.open" @click="toggleOpen(card)">
                <Icon name="chevron-down" :size="14" class="svc-chevron" :class="{ 'is-open': card.open }" />
                <span class="svc-name">{{ card.draft.name || t('（未命名）') }}</span>
                <span class="svc-sub break">{{ baseUrl(card) }}</span>
              </button>
              <span class="g-chip" :class="{ 'is-accent': card.state.running }" data-test="services-running">
                {{ card.state.running ? t('运行中') : t('已停止') }}
              </span>
            </header>

            <div v-show="card.open" class="svc-body">
              <SettingRow :label="t('名称')" :hint="t('只用来区分不同的服务。写回记忆的摘要会以「[对外服务:名称]」开头，方便看出是哪个服务写的。')">
                <input
                  v-model="card.draft.name"
                  data-test="services-name"
                  :aria-invalid="missing(card, 'name') || undefined"
                />
              </SettingRow>
              <p v-if="missing(card, 'name')" class="field-error">{{ t('请填写名称') }}</p>

              <SettingRow :label="t('启用')" :hint="t('关闭后停止监听这个端口，停止前会先把还没总结的内容写进记忆。')">
                <input v-model="card.draft.enabled" type="checkbox" class="g-switch" role="switch" data-test="services-enabled" />
              </SettingRow>

              <SettingRow :label="t('端口')" :hint="t('只在本机 127.0.0.1 上监听，范围 1024–65535。不能和其他服务、主程序或本地语音引擎的端口重复。')">
                <input v-model.number="card.draft.port" type="number" min="1024" max="65535" data-test="services-port" />
              </SettingRow>

              <SettingRow :label="t('协议')" :hint="t('调用方使用的接口格式。必须和上游模型的格式一致：这里只转发请求，不做格式转换。')">
                <Segmented
                  :model-value="card.draft.protocol"
                  :options="PROTOCOLS"
                  :label="t('协议')"
                  data-test="services-protocol"
                  @update:modelValue="(value: string) => setProtocol(card, value)"
                />
              </SettingRow>

              <SettingRow :label="t('模型名')" :hint="t('对外显示的模型名。调用方请求里的 model 必须填这个名字，转发时会换成上游的真实模型名。')">
                <input v-model="card.draft.model" data-test="services-model" :aria-invalid="missing(card, 'model') || undefined" />
              </SettingRow>
              <p v-if="missing(card, 'model')" class="field-error">{{ t('请填写模型名') }}</p>

              <SettingRow :label="t('上下文长度')" unit="token" :hint="t('上游模型能接收的最大长度，用来计算还能塞进多少记忆。请求本身太长时，这一次就不加记忆。新建时默认和主模型相同。')">
                <input v-model.number="card.draft.contextTokens" type="number" min="1" data-test="services-context" />
              </SettingRow>

              <SettingRow :label="t('自定义提示词')" :hint="t('加在调用方自己的系统提示词前面，每次请求都会带上。留空则完全不改动请求。')" stack>
                <textarea
                  v-model="card.draft.prompt"
                  rows="3"
                  :placeholder="t('留空则不修改请求')"
                  data-test="services-prompt"
                />
              </SettingRow>

              <SettingRow :label="t('召回')" :hint="t('开启后，按调用方最后一条消息从主记忆里查相关内容，作为背景资料加在这条消息前面。')">
                <input v-model="card.draft.recall.enabled" type="checkbox" class="g-switch" role="switch" data-test="services-recall" />
              </SettingRow>
              <SettingRow :label="t('条数')" :unit="t('条')" :hint="t('每次最多加几条记忆。默认 8 条。')">
                <input v-model.number="card.draft.recall.limit" type="number" min="1" data-test="services-recall-limit" />
              </SettingRow>
              <SettingRow :label="t('token 上限')" unit="token" :hint="t('加进去的记忆最多占多长。超出的部分不加，不会截断某一条的中间。默认 2000。')">
                <input v-model.number="card.draft.recall.tokenLimit" type="number" min="0" data-test="services-recall-token" />
              </SettingRow>

              <SettingRow :label="t('总结')" :hint="t('开启后，把调用方的要求和服务的回复定时总结成一句话，写进主记忆，主对话以后也能想起来。关闭则不记录任何交互。')">
                <input v-model="card.draft.summary.enabled" type="checkbox" class="g-switch" role="switch" data-test="services-summary-enabled" />
              </SettingRow>
              <SettingRow :label="t('间隔（分钟）')" :hint="t('最后一次请求之后，空闲这么久就总结一次。默认 10 分钟。')">
                <input v-model.number="card.draft.summary.intervalMinutes" type="number" min="1" data-test="services-interval" />
              </SettingRow>
              <SettingRow :label="t('最长等待（分钟）')" :hint="t('请求一直不断时，最早一条等了这么久也会总结，免得一直不写。不能小于间隔。默认 60 分钟。')">
                <input v-model.number="card.draft.summary.maxWaitMinutes" type="number" min="1" data-test="services-maxwait" />
              </SettingRow>

              <SettingRow :label="t('上游')" :hint="t('请求转发给哪个模型。继承主模型时，地址、密钥、超时都跟主模型设置走；关闭后可以单独填写。')">
                <label class="svc-inherit">
                  <input v-model="card.draft.upstream.inherit" type="checkbox" class="g-switch" role="switch" data-test="services-inherit" />
                  {{ t('继承主模型') }}
                </label>
              </SettingRow>
              <template v-if="!card.draft.upstream.inherit">
                <SettingRow :label="t('接口地址')" :hint="t('模型服务的网址，一般以 /v1 结尾。')">
                  <input v-model="card.draft.upstream.baseUrl" data-test="services-upstream-url" />
                </SettingRow>
                <SettingRow :label="t('模型')" :hint="t('上游服务里真实的模型名，转发时会替换请求里的 model。')">
                  <input v-model="card.draft.upstream.model" data-test="services-upstream-model" />
                </SettingRow>
                <SettingRow label="API Key" :hint="t('保存后不会再显示出来。留空表示不改；本地模型一般不需要。')">
                  <input v-model="card.draft.upstream.apiKey" type="password" autocomplete="off" data-test="services-upstream-key" />
                </SettingRow>
                <!-- No protocol picker: pass-through never converts formats, so a custom upstream must speak the
                     service's own protocol. Offering a choice here would only produce a 400 from the model. -->
                <SettingRow :label="t('最大输出 tokens')" :hint="t('Anthropic 协议要求每个请求都带 max_tokens，调用方没带时用这个值补上。留空表示沿用主模型。')">
                  <input v-model.number="card.draft.upstream.maxTokens" type="number" min="1" data-test="services-upstream-max-tokens" />
                </SettingRow>
                <SettingRow :label="t('上游超时（秒）')" :hint="t('等上游开始回复的最长时间，超时返回 504。开始回复之后不再限时。留空表示沿用主模型。')">
                  <input v-model.number="card.draft.upstream.timeoutSeconds" type="number" min="1" data-test="services-upstream-timeout" />
                </SettingRow>
              </template>

              <!-- The live facts of an instance: address, health, counters and the last summary it wrote. -->
              <div class="svc-status">
                <div class="svc-kv"><span>{{ t('服务地址') }}</span><code class="break">{{ baseUrl(card) }}</code></div>
                <div class="svc-kv"><span>{{ t('错误') }}</span><span class="svc-error-text">{{ card.state.error || '—' }}</span></div>
                <div class="svc-kv"><span>{{ t('请求数') }}</span><span>{{ card.state.requests }}</span></div>
                <div class="svc-kv"><span>{{ t('待总结轮数') }}</span><span>{{ card.state.pendingRounds }}</span></div>
                <div class="svc-kv"><span>{{ t('上次总结') }}</span><span>{{ fmt(card.state.lastFlushAt) }}</span></div>
                <div class="svc-kv">
                  <span>{{ t('最近写入的记忆') }}</span>
                  <button v-if="card.state.lastMemoryId" class="svc-link" data-test="services-last-memory" @click="emit('navigate', 'memory')">
                    {{ card.state.lastMemoryId }}
                  </button>
                  <span v-else>—</span>
                </div>
              </div>

              <div class="svc-actions" v-if="!card.isNew">
                <button class="ghost" data-test="services-flush" @click="flush(card)">
                  <Icon name="sparkle" :size="14" /> {{ t('立即总结') }}
                </button>
                <button class="ghost" data-test="services-rotate" @click="rotate(card)">
                  <Icon name="refresh" :size="14" /> {{ t('轮换 token') }}
                </button>
                <button class="ghost danger" data-test="services-delete" @click="requestDelete(card)">
                  <Icon name="trash" :size="14" /> {{ t('删除') }}
                </button>
              </div>
              <div class="svc-actions" v-else>
                <button class="ghost danger" data-test="services-delete" @click="requestDelete(card)">
                  <Icon name="trash" :size="14" /> {{ t('删除') }}
                </button>
              </div>

              <div class="svc-curl">
                <div class="svc-curl-head">
                  <span>{{ t('curl 示例') }}</span>
                  <button class="ghost" :aria-label="t('复制')" data-test="services-curl-copy" @click="copy(curl(card))">
                    <Icon name="copy" :size="14" /> {{ t('复制') }}
                  </button>
                </div>
                <pre class="break"><code>{{ curl(card) }}</code></pre>
              </div>
            </div>
          </section>

          <button class="ghost add" data-test="services-add" @click="addService">
            <Icon name="plus" :size="14" /> {{ t('添加服务') }}
          </button>
        </div>
      </div>
    </div>

    <!-- Deleting can optionally purge the memories this service wrote; the checkbox starts unchecked. -->
    <Dialog :open="deleteTarget !== null" :title="t('确认删除')" @close="deleteTarget = null">
      <p>{{ t('删除后无法恢复。') }}</p>
      <label class="svc-purge">
        <input v-model="purgeMemories" type="checkbox" data-test="services-purge" />
        {{ t('同时删除它写入的记忆') }}
      </label>
      <template #footer>
        <button class="ghost" data-test="services-delete-cancel" @click="deleteTarget = null">{{ t('取消') }}</button>
        <button class="primary" data-test="services-delete-confirm" @click="confirmDelete">{{ t('确认删除') }}</button>
      </template>
    </Dialog>

    <SaveBar
      prefix="services"
      status-test="services-notice"
      :dirty="dirty"
      :status="notice"
      :error="error"
      can-discard
      @save="save"
      @discard="refresh"
    />
  </div>
</template>

<style scoped>
.services-scroll {
  flex: 1;
}

.svc-list {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
}

.svc-card {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
  padding: var(--space-4) var(--space-5);
}

.svc-head {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.svc-toggle {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-inline-size: 0;
  flex: 1;
  background: none;
  border: 0;
  padding: 0;
  cursor: pointer;
  color: var(--text);
  text-align: start;
}

.svc-chevron {
  transition: transform var(--motion-fast) var(--ease);
}
.svc-chevron.is-open {
  transform: rotate(180deg);
}

.svc-name {
  font-weight: 600;
}

.svc-sub {
  color: var(--text-3);
  font-size: var(--fs-meta);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.svc-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
}

.svc-inherit {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}

.svc-status {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr));
  gap: var(--space-2) var(--space-4);
  padding: var(--space-3);
  border: 1px solid var(--glass-edge);
  border-radius: var(--radius-md);
  font-size: var(--fs-meta);
}

.svc-kv {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 2px;
  min-inline-size: 0;
}
.svc-kv > span:first-child {
  color: var(--text-3);
}

.svc-error-text {
  color: var(--danger, #c0392b);
}

.svc-link {
  justify-self: start;
  background: none;
  border: 0;
  padding: 0;
  color: var(--accent);
  cursor: pointer;
  text-decoration: underline;
}

.svc-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
}

.svc-curl {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
}
.svc-curl-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: var(--text-3);
  font-size: var(--fs-meta);
}
.svc-curl pre {
  margin: 0;
  padding: var(--space-3);
  border: 1px solid var(--glass-edge);
  border-radius: var(--radius-md);
  background: var(--glass-2);
  font-size: var(--fs-meta);
  white-space: pre-wrap;
  overflow-x: auto;
}

.svc-reveal {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-3) var(--space-4);
  margin-bottom: var(--space-3);
}
.svc-reveal-text {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--space-2);
  min-inline-size: 0;
  flex: 1;
}
.svc-token {
  font-weight: 600;
}

.svc-purge {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  cursor: pointer;
}
</style>
