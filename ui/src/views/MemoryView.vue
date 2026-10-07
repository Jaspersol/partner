<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { MemoryRevision, PartnerApiSurface } from '../api'
import { memoryRowFromHit, memoryRowFromItem, MEMORY_SCOPES, MEMORY_TIERS, scopeLabel, tierLabel, type MemoryRow } from '../memoryRow'
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
const scope = ref('all')
const error = ref('')
/** Closed on arrival, like the session view's: the list is what a reader opens this page for. */
const inspectorCollapsed = ref(true)

/** See SessionView: the handle is only honest while the panel is a column rather than an overlay. */
const { isOverlay } = useOverlayMode()

/** True while the list shows recall hits rather than the stored records. */
const recalling = ref(false)

/** The query whose hits are on screen. Part of the state, not part of the sentence: see `summary`. */
const recalled = ref('')

/**
 * What the summary line says, derived from the list rather than remembered alongside it.
 *
 * <para>
 * It used to be a ref written by whichever call finished last, which made it drift: change a filter after a
 * search and the count stayed the pre-filter one, so a list of three sat under 「10 条命中」 — and a list of
 * nothing sat under it too. A number next to a list has to be computed from that list, so this is a computed:
 * there is no path by which the sentence and the rows disagree.
 * </para>
 */
const summary = computed(() => {
  if (!loaded.value) {
    return ''
  }

  if (!recalling.value) {
    const live = rows.value.filter((row) => !row.retired).length
    return `${live} 条生效 · 共 ${rows.value.length} 条`
  }

  const hits = `检索「${recalled.value}」→ ${rows.value.length} 条命中`
  // Only when the filters are actually hiding something: two numbers where one would do is its own noise.
  return visible.value.length === rows.value.length ? hits : `${hits} · 筛选后 ${visible.value.length} 条`
})

/**
 * All four tiers, always, with a count each.
 *
 * <para>
 * The list used to build this from the tiers the loaded rows happened to have, so a tier with no records could
 * not be selected and the ones that were showed a bare enum name. Both lose something: "this tier is empty" and
 * "this tier does not exist" are different facts, and a reader asking what is remembered about procedures should
 * get an answer rather than an absent option.
 * </para>
 */
const tierOptions = computed(() =>
  MEMORY_TIERS.map((option) => ({
    value: option,
    count: rows.value.filter((row) => row.tier === option).length,
  })),
)

const visible = computed(() =>
  rows.value.filter(
    (row) =>
      (tier.value === 'all' || row.tier === tier.value) && (scope.value === 'all' || row.scope === scope.value),
  ),
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
    recalled.value = ''

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

/**
 * Pins the selected record against forgetting, or releases it.
 *
 * <p>
 * A person's call for the same reason the tier is: the 90-day demotion clock cannot tell a fact you rely on from
 * one you happened to hear once. Re-reads rather than assuming, like every other edit here.
 * </p>
 */
async function setScope(next: string) {
  const row = selected.value
  if (!row) {
    error.value = '先选中一条记忆'
    return
  }

  error.value = ''
  try {
    await props.api.setMemoryScope(row.recordId, next)
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
    rows.value = hits.map(hit => memoryRowFromHit(hit))
    recalling.value = true
    recalled.value = text
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

/**
 * Writes a memory the distiller would not have written.
 *
 * <p>
 * Until this existed the only editable memories were the ones the store had happened to keep, so a fact the
 * person wanted remembered had to be phrased in a way the distiller recognised. The tier and scope are offered
 * here rather than assumed, because both are judgements — but the server decides the defaults if they are left
 * alone, so the common case is one textarea.
 * </p>
 */
const composing = ref(false)
const newBody = ref('')
const newTier = ref<string>('Semantic')
const newScope = ref<string>('Recall')

function openComposer() {
  error.value = ''
  newBody.value = ''
  newTier.value = 'Semantic'
  newScope.value = 'Recall'
  composing.value = true
}

async function create() {
  const body = newBody.value.trim()
  if (body.length < 4) {
    error.value = '正文至少 4 个字'
    return
  }

  error.value = ''
  try {
    const recordId = await props.api.createMemory({ body, tier: newTier.value, scope: newScope.value })
    composing.value = false
    await refresh()
    const updated = rows.value.find((candidate) => candidate.recordId === recordId)
    if (updated) {
      await select(updated)
    }
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
      <button data-test="memory-new" @click="openComposer">＋ 新建</button>
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
        <option value="all">层：全部</option>
        <option v-for="option in tierOptions" :key="option.value" :value="option.value">
          {{ tierLabel(option.value) }}（{{ option.count }}）
        </option>
      </select>
      <select v-model="scope" data-test="memory-scope" aria-label="保留">
        <option value="all">保留：全部</option>
        <option v-for="option in MEMORY_SCOPES" :key="option" :value="option">
          {{ scopeLabel(option) }}
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
        <!--
          Two different empties. 「无记忆」 in a search result says the store is empty when it has just reported
          hits, and 「无命中」 over a filtered list says the same thing about the whole store.
        -->
        <p v-else-if="visible.length === 0" class="empty" data-test="memory-empty">
          {{ recalling ? '（无命中）' : '（无记忆）' }}
        </p>
      </div>

      <!--
        The tier is editable because the demotion rule protects what a person marked as established. That
        makes it a judgement rather than a score, and a judgement belongs to whoever reads the list.

        Retention is a checkbox and not a third dropdown on purpose: it is a promise about time, not a rank, and
        the two are independent — a retained Working record is a legal combination, so there is nothing to lock.
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
        <label class="retain-toggle">
          <input
            type="checkbox"
            data-test="memory-retain"
            :checked="selected.scope === 'Retain'"
            @change="setScope(($event.target as HTMLInputElement).checked ? 'Retain' : 'Recall')"
          />
          保留
        </label>
        <span class="hint" data-test="memory-retain-hint">保留只挡遗忘，不挡纠正 —— 同层的新记录仍会覆盖它</span>
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

    <!-- Not a modal: it is one textarea and two judgements, and a dialog would hide the list it is adding to. -->
    <div v-if="composing" class="composer" data-test="memory-composer">
      <textarea
        v-model="newBody"
        rows="3"
        placeholder="要记住的一句话…"
        aria-label="新记忆正文"
        data-test="memory-new-body"
      />
      <div class="composer-meta">
        <label>
          层级
          <select v-model="newTier" data-test="memory-new-tier">
            <option v-for="option in MEMORY_TIERS" :key="option" :value="option">{{ tierLabel(option) }}</option>
          </select>
        </label>
        <label>
          保留
          <select v-model="newScope" data-test="memory-new-scope">
            <option v-for="option in MEMORY_SCOPES" :key="option" :value="option">{{ scopeLabel(option) }}</option>
          </select>
        </label>
        <span class="hint">默认是「确定 · 可以当事实用」—— 手写的不是待印证的东西</span>
        <span class="spacer" />
        <button class="ghost" data-test="memory-new-cancel" @click="composing = false">取消</button>
        <button data-test="memory-new-submit" @click="create">保存</button>
      </div>
    </div>
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

/* A checkbox, not a select: "will this be forgotten" is a yes/no with one exception, and a dropdown of two
   options makes the reader open it to find out which one means yes. */
.retain-toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  color: var(--text-2);
}

.hint {
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.composer {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-block-start: 1px solid var(--line);
}

.composer textarea {
  font: inherit;
  inline-size: 100%;
  box-sizing: border-box;
  background: var(--bg-1);
  color: var(--text-1);
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  padding: var(--space-2);
  resize: block;
}

.composer-meta {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  font-size: var(--fs-meta);
  color: var(--text-3);
}
</style>
