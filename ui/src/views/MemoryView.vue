<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { MemoryRevision, PartnerApiSurface } from '../api'
import { memoryRowFromHit, memoryRowFromItem, type MemoryRow } from '../memoryRow'
import Inspector from '../components/Inspector.vue'
import MemoryList from '../components/MemoryList.vue'

const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string]; changed: [] }>()

const rows = ref<MemoryRow[]>([])
const selected = ref<MemoryRow | null>(null)
const draft = ref('')
const revisions = ref<MemoryRevision[]>([])

/** The inspector shows one body per revision; for a memory record that is the text after the edit. */
const revisionRows = computed(() =>
  revisions.value.map((revision) => ({ at: revision.at, actor: revision.actor, body: revision.after })),
)
const query = ref('')
const tier = ref('all')
const summary = ref('')
const error = ref('')
const inspectorCollapsed = ref(false)

/** True while the list shows recall hits rather than the stored records. */
const recalling = ref(false)

const tiers = computed(() => {
  const seen = new Set(rows.value.map((row) => row.tier))
  return ['all', ...Array.from(seen).sort()]
})

const visible = computed(() =>
  tier.value === 'all' ? rows.value : rows.value.filter((row) => row.tier === tier.value),
)

const note = computed(() => {
  const row = selected.value
  if (!row || row.supersededBy === null) {
    return ''
  }

  return `已失效 → ${row.supersededBy}`
})

async function refresh() {
  error.value = ''
  try {
    rows.value = (await props.api.memory()).map(memoryRowFromItem)
    recalling.value = false
    summary.value = `${rows.value.length} 条有效记忆`
    emit('context', `记忆 · ${rows.value.length} 条`)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function recall() {
  error.value = ''
  const text = query.value.trim()

  if (text === '') {
    await refresh()
    return
  }

  try {
    const hits = await props.api.recall(text)
    rows.value = hits.map(memoryRowFromHit)
    recalling.value = true
    summary.value = `检索「${text}」→ ${hits.length} 条命中`
  } catch (failure) {
    error.value = String(failure)
  }
}

async function select(row: MemoryRow) {
  selected.value = row
  draft.value = row.body
  emit('context', `记忆 ${row.recordId}`)

  try {
    revisions.value = await props.api.memoryRevisions(row.recordId)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save(body: string) {
  const row = selected.value
  if (!row) {
    error.value = '先选中一条记忆'
    return
  }
  if (body === '') {
    error.value = '正文不能为空'
    return
  }

  error.value = ''
  try {
    await props.api.editMemory(row.recordId, body)
    await refresh()
    const updated = rows.value.find((candidate) => candidate.recordId === row.recordId)
    if (updated) {
      await select(updated)
    }
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function remove() {
  const row = selected.value
  if (!row) {
    error.value = '先选中一条记忆'
    return
  }

  error.value = ''
  try {
    await props.api.deleteMemory(row.recordId)
    selected.value = null
    draft.value = ''
    revisions.value = []
    await refresh()
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)
defineExpose({ refresh })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="memory-refresh" @click="refresh">刷新</button>
      <input v-model="query" placeholder="⌕ 检索…" data-test="memory-query" @keydown.enter="recall" />
      <button data-test="memory-recall" @click="recall">检索</button>
      <select v-model="tier" data-test="memory-tier" aria-label="层级">
        <option v-for="option in tiers" :key="option" :value="option">
          {{ option === 'all' ? 'tier：全部' : option }}
        </option>
      </select>
      <button
        v-if="recalling"
        class="ghost"
        data-test="memory-back-to-all"
        @click="refresh"
      >
        返回全量
      </button>
      <button class="ghost" data-test="memory-inspector-toggle" @click="inspectorCollapsed = !inspectorCollapsed">
        {{ inspectorCollapsed ? '‹ 展开检视' : '› 收起检视' }}
      </button>
      <span class="spacer" />
      <span class="muted" data-test="memory-summary">{{ summary }}</span>
    </div>

    <div class="split" :class="{ 'is-inspector-collapsed': inspectorCollapsed }">
      <div v-if="!inspectorCollapsed" class="inspector-scrim" @click="inspectorCollapsed = true" />
      <div class="scroll">
        <MemoryList :rows="visible" :selected-id="selected?.recordId ?? null" @select="select" />
        <p v-if="visible.length === 0" class="muted" style="padding: var(--space-3)">（无记忆）</p>
      </div>

      <Inspector
        test-prefix="memory"
        title="编辑选中记忆（旧记录软失效并标记 supersededBy）"
        :target="selected?.recordId ?? '未选中'"
        :draft="draft"
        :revisions="revisionRows"
        :note="note"
        delete-label="删除"
        @save="save"
        @remove="remove"
      />
    </div>

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

.notice {
  margin: 0;
  padding: var(--space-1) var(--space-3);
  border-block-start: 1px solid var(--line);
}
</style>
