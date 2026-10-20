<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { MemoryRevision, PartnerApiSurface } from '../api'
import {
  memoryRowFromHit,
  memoryRowFromItem,
  MEMORY_TIERS,
  tierHint,
  tierLabel,
  tierShort,
  type MemoryRow,
} from '../memoryRow'
import { showToast } from '../toast'
import Dialog from '../components/Dialog.vue'
import Drawer from '../components/Drawer.vue'
import HintTip from '../components/HintTip.vue'
import Icon from '../components/Icon.vue'
import MemoryList from '../components/MemoryList.vue'
import Segmented from '../components/Segmented.vue'

const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ changed: [] }>()

const rows = ref<MemoryRow[]>([])

/**
 * Whether the list has ever come back. See SessionView's copy of this — the two states ("no memories" and
 * "has not asked yet") were sharing one message, and the second of those was being reported as the first.
 */
const loaded = ref(false)
const selected = ref<MemoryRow | null>(null)
const draft = ref('')
const revisions = ref<MemoryRevision[]>([])

/** The detail panel. Separate from the selection: closing the panel keeps the row marked, deleting clears both. */
const drawerOpen = ref(false)

const query = ref('')
const tier = ref('all')
/** 「只看已固定」: the retention filter is a yes/no with one exception, not a rank. */
const pinnedOnly = ref(false)
/** Records a later statement replaced are kept but are not part of the day-to-day picture, so they start hidden. */
const showRetired = ref(false)
const error = ref('')

/** True while the list shows recall hits rather than the stored records. */
const recalling = ref(false)

/** The query whose hits are on screen. Part of the state, not part of the sentence: see `summary`. */
const recalled = ref('')

/**
 * What the summary line says, derived from the list rather than remembered alongside it.
 *
 * <para>
 * A number next to a list must be computed from that list: a separately stored count drifts, so a list of
 * three can sit under 「10 条命中」. As a computed, there is no path by which the sentence and the rows
 * disagree.
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
 * "This tier is empty" and "this tier does not exist" are different facts: building the list from the tiers the
 * loaded rows happen to have makes an empty tier unselectable and shows a bare enum name for the rest. A reader
 * asking what is remembered about procedures should get an answer rather than an absent option.
 * </para>
 */
const tierOptions = computed(() => [
  { value: 'all', label: '全部' },
  ...MEMORY_TIERS.map((option) => ({
    value: option,
    label: `${tierShort(option)} ${rows.value.filter((row) => row.tier === option).length}`,
    title: tierLabel(option),
  })),
])

const visible = computed(() =>
  rows.value.filter(
    (row) =>
      (tier.value === 'all' || row.tier === tier.value) &&
      (!pinnedOnly.value || row.scope === 'Retain') &&
      (showRetired.value || !row.retired),
  ),
)

/** The retired ones the switch is hiding, so the switch can say how many it would show. */
const hiddenRetired = computed(() => rows.value.filter((row) => row.retired).length)

/** The inspector shows one body per revision; for a memory record that is the text after the edit. */
const revisionRows = computed(() =>
  revisions.value.map((revision) => ({ at: revision.at, actor: revision.actor, body: revision.after })),
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
    // answer "why does it now believe the opposite", which is the question a conflict raises. They are filtered
    // on screen, not left out of the read.
    const all = await props.api.memoryAll()
    rows.value = all.map((item) => memoryRowFromItem(item))
    loaded.value = true
    recalling.value = false
    recalled.value = ''
  } catch (failure) {
    error.value = String(failure)
  }
}

/** Re-reads, then re-selects the same record so the drawer shows what the server kept, not what was sent. */
async function reselect(recordId: string) {
  await refresh()
  const updated = rows.value.find((candidate) => candidate.recordId === recordId)
  if (updated) {
    await select(updated)
  }
}

/**
 * Moves the selected record between tiers, then re-reads.
 *
 * <para>
 * A person's call, not the scorer's: "this is a fact I rely on" and "this is something I once guessed" are
 * exactly the distinction the demotion rule works off, and no score can tell them apart. The re-read keeps the
 * row on screen equal to what the server kept, not what was sent.
 * </para>
 */
async function setTier(next: string) {
  const row = selected.value
  if (!row) {
    error.value = '先选一条记忆'
    return
  }

  error.value = ''
  try {
    await props.api.setMemoryTier(row.recordId, next)
    await reselect(row.recordId)
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

/**
 * Pins the selected record against forgetting, or releases it.
 *
 * <para>
 * A person's call for the same reason the tier is: the 90-day demotion clock cannot tell a fact you rely on from
 * one you happened to hear once. Re-reads rather than assuming, like every other edit here.
 * </para>
 */
async function setScope(next: string) {
  const row = selected.value
  if (!row) {
    error.value = '先选一条记忆'
    return
  }

  error.value = ''
  try {
    await props.api.setMemoryScope(row.recordId, next)
    await reselect(row.recordId)
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
    rows.value = hits.map((hit) => memoryRowFromHit(hit))
    recalling.value = true
    recalled.value = text
  } catch (failure) {
    error.value = String(failure)
  }
}

/** The × in the search box: empties it, and puts the full list back if the list was showing hits. */
async function clearSearch() {
  query.value = ''
  if (recalling.value) {
    await refresh()
  }
}

async function select(row: MemoryRow) {
  selected.value = row
  draft.value = row.body
  confirmingDelete.value = false
  drawerOpen.value = true

  try {
    revisions.value = await props.api.memoryRevisions(row.recordId)
  } catch (failure) {
    error.value = String(failure)
  }
}

function closeDrawer() {
  drawerOpen.value = false
  confirmingDelete.value = false
}

async function save() {
  const row = selected.value
  if (!row) {
    error.value = '先选一条记忆'
    return
  }

  const body = draft.value.trim()
  if (body === '') {
    error.value = '内容不能为空'
    return
  }

  error.value = ''
  try {
    await props.api.editMemory(row.recordId, body)
    await reselect(row.recordId)
    showToast('已保存')
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

/**
 * Deleting a memory cannot be taken back, and the old button deleted on the first press. It arms now: the first
 * press turns the button into 「确认删除」 and the second does it.
 */
const confirmingDelete = ref(false)

async function remove() {
  const row = selected.value
  if (!row) {
    error.value = '先选一条记忆'
    return
  }

  if (!confirmingDelete.value) {
    confirmingDelete.value = true
    return
  }

  error.value = ''
  confirmingDelete.value = false
  try {
    await props.api.deleteMemory(row.recordId)
    selected.value = null
    draft.value = ''
    revisions.value = []
    drawerOpen.value = false
    await refresh()
    showToast('已删除')
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

/**
 * Writes a memory the distiller would not have written.
 *
 * <para>
 * The store only keeps what the distiller recognised, so a fact the person wants remembered must also be
 * writable by hand. The tier and scope are offered here rather than assumed, because both are judgements — but
 * the server decides the defaults if they are left alone, so the common case is one textarea.
 * </para>
 */
const composing = ref(false)
const newBody = ref('')
const newTier = ref<string>('Semantic')
const newPinned = ref(false)

function openComposer() {
  error.value = ''
  newBody.value = ''
  newTier.value = 'Semantic'
  newPinned.value = false
  composing.value = true
}

async function create() {
  const body = newBody.value.trim()

  // Only the empty case is refused here. The length rule belongs to the server — it is the distiller's
  // `MinBodyLength`, and a second copy of it here is a number that will drift: raise the rule there and this
  // one keeps rejecting text the store would have accepted. The server's refusal is already surfaced below,
  // with its own wording.
  if (body === '') {
    error.value = '内容不能为空'
    return
  }

  error.value = ''
  try {
    const recordId = await props.api.createMemory({ body, tier: newTier.value, scope: newPinned.value ? 'Retain' : 'Recall' })
    composing.value = false
    await reselect(recordId)
    drawerOpen.value = false
    showToast('记住了')
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
  closeDrawer()
}

defineExpose({ refresh, focusSearch, collapseInspector })
</script>

<template>
  <div class="view">
    <div class="scroll memory-scroll">
      <div class="g-page memory-page">
        <header class="page-head">
          <label class="search-field memory-search">
            <Icon name="search" :size="14" class="search-glyph" />
            <input
              ref="searchInput"
              v-model="query"
              placeholder="搜一搜助手记得的…"
              title="Ctrl+K"
              aria-label="检索记忆"
              data-test="memory-query"
              @keydown.enter="recall"
            />
            <button
              v-if="query !== '' || recalling"
              class="ghost clear"
              aria-label="清除搜索"
              data-test="memory-back-to-all"
              @click="clearSearch"
            >
              <Icon name="x" :size="14" />
            </button>
          </label>
          <button class="primary" data-test="memory-new" @click="openComposer">
            <Icon name="plus" :size="14" :stroke-width="2.2" /> 记住一件事
          </button>
        </header>

        <div class="filters">
          <Segmented v-model="tier" :options="tierOptions" label="按类型筛选" data-test="memory-tier" />
          <span class="spacer" />
          <label class="toggle">
            <input v-model="pinnedOnly" type="checkbox" class="g-switch" role="switch" data-test="memory-pinned-only" />
            只看已固定
          </label>
          <label v-if="hiddenRetired > 0 || showRetired" class="toggle">
            <input v-model="showRetired" type="checkbox" class="g-switch" role="switch" data-test="memory-show-retired" />
            含已失效 {{ hiddenRetired }}
            <HintTip align="end">被新记录取代的旧记忆。平时不用看，想知道「它为什么现在这么认为」时再打开。</HintTip>
          </label>
        </div>

        <p class="summary-line">
          <span data-test="memory-summary">{{ summary }}</span>
          <button class="ghost refresh" aria-label="刷新" title="刷新" data-test="memory-refresh" @click="refresh">
            <Icon name="refresh" :size="14" />
          </button>
        </p>

        <p v-if="error && !composing" class="error" data-test="error">{{ error }}</p>

        <MemoryList :rows="visible" :selected-id="selected?.recordId ?? null" @select="select" />
        <div v-if="!loaded" class="skeleton-stack" aria-hidden="true">
          <div class="skeleton-line" />
          <div class="skeleton-line" />
          <div class="skeleton-line" />
        </div>
        <p v-if="!loaded" class="pending" data-test="memory-pending">正在读取记忆…</p>
        <!--
          Two different empties. 「没有相关的记忆」 in a search result says the store is empty when it has just reported
          hits, and the other way round says the same thing about the whole store.
        -->
        <p v-else-if="visible.length === 0" class="empty" data-test="memory-empty">
          {{ recalling ? '没有找到相关的记忆' : '助手还没记住任何事' }}
        </p>
      </div>
    </div>

    <!--
      The detail panel. The tier is editable because the demotion rule protects what a person marked as
      established — a judgement, which belongs to whoever reads the list. Retention is a switch and not a second
      picker on purpose: it is a promise about time, not a rank, and the two are independent (a pinned 「刚记下」 is a
      legal combination), so there is nothing to lock.
    -->
    <Drawer id="memory-inspector" :open="drawerOpen" title="编辑记忆" @close="closeDrawer">
      <div class="target">
        <span class="meta break" data-test="memory-target">{{ selected?.recordId ?? '未选中' }}</span>
        <span v-if="note" class="g-chip is-warn" data-test="memory-note">{{ note }}</span>
        <HintTip align="end">保存后会生成一条新记录，旧的那条被标记为「已失效」，但不会丢。</HintTip>
      </div>

      <textarea v-model="draft" rows="6" aria-label="记忆内容" data-test="memory-draft" />

      <div v-if="selected" class="field-row">
        <span class="field-name">类型 <HintTip>{{ tierHint(selected.tier) }}</HintTip></span>
        <Segmented
          :model-value="selected.tier"
          :options="MEMORY_TIERS.map((option) => ({ value: option, label: tierShort(option), title: tierLabel(option) }))"
          label="记忆类型"
          data-test="memory-set-tier"
          @update:model-value="setTier"
        />
      </div>
      <label v-if="selected" class="field-row">
        <span class="field-name">
          固定
          <HintTip data-test="memory-retain-hint">固定后不会被自动遗忘；但同类的新记录仍然可以纠正它。</HintTip>
        </span>
        <input
          type="checkbox"
          class="g-switch"
          role="switch"
          data-test="memory-retain"
          :checked="selected.scope === 'Retain'"
          @change="setScope(($event.target as HTMLInputElement).checked ? 'Retain' : 'Recall')"
        />
      </label>

      <details class="history">
        <summary>修改记录（{{ revisionRows.length }}）</summary>
        <!-- Undo needs no special mechanism: restoring appends another edit pointing at the old text. -->
        <ul class="revisions" data-test="memory-revisions">
          <li v-for="(revision, index) in revisionRows" :key="index">
            <div class="meta">
              {{ revision.at.slice(0, 16).replace('T', ' ') }}<span v-if="revision.actor"> · {{ revision.actor }}</span>
            </div>
            <div class="break">{{ revision.body }}</div>
            <button class="ghost" data-test="memory-revision-restore" @click="draft = revision.body">恢复这一版</button>
          </li>
        </ul>
        <p v-if="revisionRows.length === 0" class="muted">还没有修改过。</p>
      </details>

      <p v-if="error && !composing" class="error" data-test="drawer-error">{{ error }}</p>

      <template #footer>
        <button
          v-if="selected"
          class="ghost"
          :class="{ danger: confirmingDelete }"
          data-test="memory-delete"
          @click="remove"
        >
          {{ confirmingDelete ? '确认删除' : '删除' }}
        </button>
        <button v-if="confirmingDelete" class="ghost" @click="confirmingDelete = false">取消</button>
        <span class="spacer" />
        <button class="primary" data-test="memory-save" @click="save">保存</button>
      </template>
    </Drawer>

    <!-- Not a page: it is one textarea and two judgements, and a bottom-of-page form hides the list it adds to. -->
    <Dialog :open="composing" title="记住一件事" @close="composing = false">
      <div data-test="memory-composer" class="composer-body">
        <textarea
          v-model="newBody"
          rows="4"
          placeholder="要记住的一句话…"
          aria-label="新记忆内容"
          data-test="memory-new-body"
        />
        <div class="field-row">
          <span class="field-name">类型 <HintTip>手写的记忆默认算「事实」，不需要再被印证。</HintTip></span>
          <Segmented
            v-model="newTier"
            :options="MEMORY_TIERS.map((option) => ({ value: option, label: tierShort(option), title: tierLabel(option) }))"
            label="新记忆的类型"
            data-test="memory-new-tier"
          />
        </div>
        <label class="field-row">
          <span class="field-name">
            固定
            <HintTip>固定后不会被自动遗忘；但同类的新记录仍然可以纠正它。</HintTip>
          </span>
          <input v-model="newPinned" type="checkbox" class="g-switch" role="switch" data-test="memory-new-pin" />
        </label>
        <!--
          The one thing this form does not do, said while the reader is here rather than when they are surprised:
          deleting a conversation (or sending a message after a withdrawal) takes the memories distilled from it
          with it, and this one is not one of those. It belongs to the person.
        -->
        <p class="cascade">
          这条记忆不属于任何一次对话
          <HintTip data-test="memory-new-cascade-hint">这条记忆不属于任何一次对话，删掉对话不会删掉它。</HintTip>
        </p>
        <p v-if="error" class="error" data-test="error">{{ error }}</p>
      </div>
      <template #footer>
        <button class="ghost" data-test="memory-new-cancel" @click="composing = false">取消</button>
        <button class="primary" data-test="memory-new-submit" @click="create">保存</button>
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.memory-scroll {
  flex: 1;
  min-block-size: 0;
}

.page-head {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.memory-search {
  flex: 1;
  min-inline-size: 0;
}

.memory-search input {
  inline-size: 100%;
  min-block-size: 2.75rem;
  border-radius: var(--radius-pill);
}

.memory-search .clear {
  position: absolute;
  inset-inline-end: var(--space-2);
  min-block-size: 1.75rem;
  inline-size: 1.75rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--space-3);
}

.toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  color: var(--text-2);
  font-size: var(--fs-ui);
  cursor: pointer;
}

.summary-line {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin: calc(var(--space-2) * -1) 0 0;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.refresh {
  min-block-size: 1.75rem;
  inline-size: 1.75rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.target {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.target .meta {
  flex: 1;
  min-inline-size: 0;
  font-family: var(--font-mono);
}

.field-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
}

.field-name {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}

.history summary {
  cursor: pointer;
  color: var(--text-2);
  font-size: var(--fs-ui);
}

.revisions {
  list-style: none;
  margin: var(--space-3) 0 0;
  padding: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
}

.revisions li {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-1);
  padding: var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.revisions button {
  justify-self: start;
  min-block-size: 1.75rem;
  font-size: var(--fs-meta);
}

.composer-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-4);
}

.cascade {
  margin: 0;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: var(--text-3);
  font-size: var(--fs-meta);
}
</style>
