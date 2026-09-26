<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { ConsumerState, PartnerApiSurface, QueueSummary, SessionRevision, SessionView } from '../api'
import Composer from '../components/Composer.vue'
import EventList from '../components/EventList.vue'
import Inspector from '../components/Inspector.vue'

const props = defineProps<{
  api: PartnerApiSurface
  summary?: QueueSummary | undefined
  consumer?: ConsumerState | undefined
}>()
const emit = defineEmits<{ context: [value: string]; changed: [] }>()

const view = ref<SessionView | null>(null)
const selectedSeq = ref<number | null>(null)
const draft = ref('')
const revisions = ref<SessionRevision[]>([])
const query = ref('')
const sourceFilter = ref('all')
const error = ref('')
const notice = ref('')
const composer = ref<InstanceType<typeof Composer> | null>(null)
const inspectorCollapsed = ref(false)

const selected = computed(() => view.value?.items.find((item) => item.seq === selectedSeq.value) ?? null)

const sources = computed(() => {
  const seen = new Set((view.value?.items ?? []).map((item) => item.source))
  return ['all', ...Array.from(seen).sort()]
})

/** Filtering happens over the loaded window; the API has no full-text search for events. */
const visible = computed(() => {
  const text = query.value.trim().toLowerCase()
  return (view.value?.items ?? []).filter((item) => {
    const matchesSource = sourceFilter.value === 'all' || item.source === sourceFilter.value
    const matchesText = text === '' || item.body.toLowerCase().includes(text)
    return matchesSource && matchesText
  })
})

/** Context occupancy as a short label: what the history costs against the room the prompt has. */
const contextLabel = computed(() => {
  const usage = view.value?.context
  if (!usage) {
    return ''
  }

  const percent = Math.round(usage.fraction * 100)
  return `上下文 ${format(usage.usedTokens)} / ${format(usage.budgetTokens)}（${percent}%）`
})

const contextHint = computed(() => {
  const usage = view.value?.context
  if (!usage) {
    return ''
  }

  const folded = usage.foldedItems > 0 ? ` · 已折叠 ${usage.foldedItems} 条` : ''
  return `窗口 ${usage.contextTokens} token（预留输出 ${usage.maxTokens}）· 保留 ${usage.keptItems} 条${folded}`
})

function format(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : String(tokens)
}

const revisionNote = computed(() => {
  const item = selected.value
  if (!item || item.revisionCount === 0) {
    return ''
  }

  return `已编辑 ${item.revisionCount} 次 · 原文：${item.originalBody ?? ''}`
})

async function refresh() {
  error.value = ''
  try {
    view.value = await props.api.session()
    if (selectedSeq.value !== null) {
      emit('context', contextFor(selectedSeq.value))
    }
  } catch (failure) {
    error.value = String(failure)
  }
}

function contextFor(seq: number): string {
  const item = view.value?.items.find((candidate) => candidate.seq === seq)
  return item ? `会话 #${item.seq} · ${item.kind}` : '会话'
}

async function select(seq: number) {
  selectedSeq.value = seq
  draft.value = view.value?.items.find((item) => item.seq === seq)?.body ?? ''
  emit('context', contextFor(seq))

  try {
    revisions.value = await props.api.sessionRevisions(seq)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save(body: string) {
  const seq = selectedSeq.value
  if (seq === null) {
    error.value = '先选中一条事件'
    return
  }
  if (body === '') {
    error.value = '正文不能为空'
    return
  }

  error.value = ''
  notice.value = ''
  try {
    await props.api.editSession(seq, body)
    // No optimistic update: re-read, then re-select so the editor shows what the server kept.
    await refresh()
    await select(seq)
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function removeEvent() {
  const seq = selectedSeq.value
  if (seq === null) {
    error.value = '先选中一条事件'
    return
  }

  error.value = ''
  notice.value = ''
  try {
    await props.api.deleteEvent(seq)
    // The selection is gone from the view, so the panel must not keep pointing at it.
    selectedSeq.value = null
    draft.value = ''
    revisions.value = []
    await refresh()
    notice.value = `已删除 #${seq}（原始日志保留）`
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function retractFromSelected() {
  const seq = selectedSeq.value
  if (seq === null) {
    error.value = '先选中一条事件'
    return
  }

  error.value = ''
  notice.value = ''
  try {
    await props.api.retractFrom(seq)
    selectedSeq.value = null
    draft.value = ''
    revisions.value = []
    await refresh()
    notice.value = `已撤回 #${seq} 及之后的事件（原始日志保留）`
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function onSent(result: { itemId: string; deduplicated: boolean }) {
  notice.value = result.deduplicated
    ? `已入队 ${result.itemId}（重复提交，未新增）`
    : `已入队 ${result.itemId} · 等待执行`
  // The delivery lands in the queue, so the session is unchanged until a consumer runs.
  await refresh()
  emit('changed')
}

function focusComposer() {
  composer.value?.focus()
}

onMounted(refresh)
defineExpose({ refresh, focusComposer })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="session-refresh" @click="refresh">刷新</button>
      <input v-model="query" placeholder="⌕ 检索正文…" data-test="session-query" />
      <select v-model="sourceFilter" data-test="session-source-filter" aria-label="来源">
        <option v-for="option in sources" :key="option" :value="option">
          {{ option === 'all' ? '来源：全部' : option }}
        </option>
      </select>
      <button class="ghost" data-test="session-inspector-toggle" @click="inspectorCollapsed = !inspectorCollapsed">
        {{ inspectorCollapsed ? "‹ 展开检视" : "› 收起检视" }}
      </button>
      <span class="spacer" />
      <span
        v-if="contextLabel"
        class="muted context-meter"
        :class="{ 'context-tight': (view?.context.fraction ?? 0) >= 0.85 }"
        :title="contextHint"
        data-test="session-context"
      >
        {{ contextLabel }}
      </span>
      <span class="muted" data-test="session-summary">
        {{ visible.length }} / {{ view?.items.length ?? 0 }} 条事件 · 最后 seq={{ view?.lastSeq ?? 0 }}
      </span>
    </div>

    <div class="split" :class="{ 'is-inspector-collapsed': inspectorCollapsed }">
      <div v-if="!inspectorCollapsed" class="inspector-scrim" @click="inspectorCollapsed = true" />
      <div class="scroll">
        <EventList :items="visible" :selected-seq="selectedSeq" @select="select" />
        <p v-if="visible.length === 0" class="muted" style="padding: var(--space-3)">
          （无匹配事件）
        </p>
      </div>

      <Inspector
        test-prefix="session"
        title="编辑选中事件（追加修订，原文保留）"
        :target="selected ? `#${selected.seq} · ${selected.kind} · ${selected.source} · 修订 ${selected.revisionCount} 次` : '未选中'"
        :draft="draft"
        :revisions="revisions"
        :note="revisionNote"
        @save="save"
        @delete="removeEvent"
        @retract="retractFromSelected"
      />
    </div>

    <Composer
      ref="composer"
      :api="api"
      :summary="summary"
      :consumer="consumer"
      @sent="onSent"
      @failed="error = $event"
    />

    <p v-if="notice" class="muted notice" data-test="session-notice">{{ notice }}</p>
    <p v-if="error" class="error notice" data-test="error">{{ error }}</p>
  </div>
</template>

<style scoped>
.view {
  display: flex;
  flex-direction: column;
  min-block-size: 0;
  block-size: 100%;
}

.split {
  flex: 1;
  min-block-size: 0;
}

.spacer {
  flex: 1;
}

/* The meter turns loud only when the window is nearly full — that is when it matters. */
.context-meter {
  white-space: nowrap;
}

.context-tight {
  color: var(--warn);
  font-weight: 600;
}

.notice {
  margin: 0;
  padding: var(--space-1) var(--space-3);
  border-block-start: 1px solid var(--line);
}
</style>
