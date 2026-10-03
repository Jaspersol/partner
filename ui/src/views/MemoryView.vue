<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { MemoryRevision, PartnerApiSurface } from '../api'
import { memoryRowFromHit, memoryRowFromItem, MEMORY_TIERS, tierLabel, type MemoryRow } from '../memoryRow'
import { useOverlayMode } from '../narrow'
import Inspector from '../components/Inspector.vue'
import Icon from '../components/Icon.vue'
import MemoryList from '../components/MemoryList.vue'

const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string]; changed: [] }>()

const rows = ref<MemoryRow[]>([])

/**
 * Whether the list has ever come back. See SessionView's copy of this — the two states ("no memories" and
 * "has not asked yet") were sharing one message, and the second of those was being reported as the first.
 */
const loaded = ref(false)
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
/** Closed on arrival, like the session view's: the list is what a reader opens this page for. */
const inspectorCollapsed = ref(true)

/** See SessionView: the handle is only honest while the panel is a column rather than an overlay. */
const { isOverlay } = useOverlayMode()

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
    // The full list, retired records included: a memory view that hides what a later statement replaced cannot
    // answer "why does it now believe the opposite", which is the question a conflict raises.
    const all = await props.api.memoryAll()
    rows.value = all.map((item) => memoryRowFromItem(item))
    loaded.value = true
    recalling.value = false

    const live = rows.value.filter((row) => !row.retired).length
    summary.value = `${live} 条生效 · 共 ${rows.value.length} 条`
    emit('context', `记忆 · ${rows.value.length} 条`)
  } catch (failure) {
    error.value = String(failure)
  }
}

/**
 * Moves the selected record between tiers, then re-reads.
 *
 * <para>
 * A person's call, not the scorer's: "this is a fact I rely on" and "this is something I once guessed" are
 * exactly the distinction the demotion rule works off, and no score can tell them apart. The re-read is the
 * norm (ui.md): the row on screen is what the server kept, not what was sent.
 * </para>
 */
async function setTier(tier: string) {
  const row = selected.value
  if (!row) {
    error.value = '先选中一条记忆'
    return
  }

  error.value = ''
  try {
    await props.api.setMemoryTier(row.recordId, tier)
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

/** For Ctrl+K. See SessionView's copy of this — it is the same contract. */
const searchInput = ref<HTMLInputElement | null>(null)

function focusSearch() {
  query.value = ''
  searchInput.value?.focus()
}

function collapseInspector() {
  inspectorCollapsed.value = true
}

defineExpose({ refresh, focusSearch, collapseInspector })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="memory-refresh" @click="refresh">刷新</button>
      <label class="search-field">
        <Icon name="search" :size="14" class="search-glyph" />
        <input
          ref="searchInput"
          v-model="query"
          placeholder="检索记忆…"
          title="Ctrl+K"
          aria-label="检索记忆"
          data-test="memory-query"
          @keydown.enter="recall"
        />
      </label>
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
      <button
        class="ghost"
        data-test="memory-inspector-toggle"
        aria-controls="memory-inspector"
        :aria-expanded="!inspectorCollapsed"
        @click="inspectorCollapsed = !inspectorCollapsed"
      >
        {{ inspectorCollapsed ? '‹ 展开检视' : '› 收起检视' }}
      </button>
      <span class="spacer" />
      <span class="muted" data-test="memory-summary">{{ summary }}</span>
    </div>

    <div class="split" :class="{ 'is-inspector-collapsed': inspectorCollapsed }">
      <div v-if="!inspectorCollapsed" class="inspector-scrim" @click="inspectorCollapsed = true" />
      <div class="scroll">
        <MemoryList :rows="visible" :selected-id="selected?.recordId ?? null" @select="select" />
        <p v-if="!loaded" class="pending" data-test="memory-pending">正在读取记忆…</p>
        <p v-else-if="visible.length === 0" class="empty">（无记忆）</p>
      </div>

      <!--
        The tier is editable because the demotion rule protects what a person marked as established. That
        makes it a judgement rather than a score, and a judgement belongs to whoever reads the list.
      -->
      <div v-if="selected" class="tier-picker">
        <label for="memory-tier-picker">层级</label>
        <select
          id="memory-tier-picker"
          :value="selected.tier"
          data-test="memory-set-tier"
          @change="setTier(($event.target as HTMLSelectElement).value)"
        >
          <option v-for="option in MEMORY_TIERS" :key="option" :value="option">
            {{ tierLabel(option) }}
          </option>
        </select>
      </div>
      <Inspector
        id="memory-inspector"
        test-prefix="memory"
        title="编辑选中记忆（旧记录软失效并标记 supersededBy）"
        :target="selected?.recordId ?? '未选中'"
        :draft="draft"
        :revisions="revisionRows"
        :note="note"
        delete-label="删除"
        :resizable="!isOverlay"
        @save="save"
        @remove="remove"
      />
    </div>

    <p v-if="error" class="error notice" data-test="error">{{ error }}</p>
  </div>
</template>

<style scoped>
.split {
  flex: 1;
  min-block-size: 0;
}

.tier-picker {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-3);
  border-block-end: 1px solid var(--line);
  font-size: var(--fs-meta);
}

.tier-picker label {
  color: var(--text-3);
}

.tier-picker select {
  font-size: var(--fs-meta);
  min-inline-size: 0;
}
</style>
