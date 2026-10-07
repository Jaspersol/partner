<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import type {
  ConsumerState,
  DesktopState,
  PartnerApiSurface,
  PendingApproval,
  QueueItem,
  QueueSummary,
  SessionRevision,
  SessionView,
} from '../api'
import { useOverlayMode } from '../narrow'
import ApprovalBanner from '../components/ApprovalBanner.vue'
import Icon from '../components/Icon.vue'
import Composer from '../components/Composer.vue'
import EventList from '../components/EventList.vue'
import Inspector from '../components/Inspector.vue'

const props = defineProps<{
  api: PartnerApiSurface
  summary?: QueueSummary | undefined
  consumer?: ConsumerState | undefined
  approval?: string | null | undefined
  approvals?: PendingApproval[] | undefined
  desktop?: DesktopState | null | undefined
  /** The gear menu and its default, passed straight through to the composer. */
  gears?: readonly string[] | undefined
  defaultGear?: string | undefined
  /** The queue behind the badge, forwarded so the composer's list is the same answer the badge came from. */
  queueItems?: readonly QueueItem[] | undefined
}>()
const emit = defineEmits<{
  context: [value: string]
  changed: []
  approval: [gear: string]
  /** The human's answer to a parked tool call; the shell owns the call itself. */
  answer: [approvalId: string, approved: boolean]
  /** A running turn was stopped, so the queue needs re-reading. */
  cancel: [itemId: string]
}>()

const view = ref<SessionView | null>(null)

/**
 * Whether anything has ever come back.
 *
 * <para>
 * Without it, the view's first render has no events and says 「（无匹配事件）」 — a confident wrong answer, said
 * before the request that would have contradicted it even went out. A reader who arrives during those few
 * hundred milliseconds is told the conversation is empty.
 * </para>
 *
 * <para>
 * Stays true for the life of the view once set, including across a failed poll: a refresh that fails should
 * leave the last good list on screen with an error beside it, not replace a conversation with a spinner.
 * </para>
 */
const loaded = ref(false)
const selectedSeq = ref<number | null>(null)
const draft = ref('')
const revisions = ref<SessionRevision[]>([])
const query = ref('')
const sourceFilter = ref('all')
const error = ref('')
const notice = ref('')
const composer = ref<InstanceType<typeof Composer> | null>(null)
/**
 * Closed on arrival.
 *
 * <para>
 * The inspector is the narrow column, and it competes with the log for the width that the log's prose needs.
 * Open by default, a 1400px window gives the conversation roughly 1000px and the panel 380 — and a reader
 * opening a session is reading the conversation. The panel is two clicks away and its own state is announced
 * in the toolbar button, so nothing is hidden that cannot be reached.
 * </para>
 *
 * <para>
 * This also matches the old plan's intent ("switching views starts with the inspector open") without its
 * letter: a fresh instance starts with the *state it should show a reader first*, which for a log is the log.
 * </para>
 */
const inspectorCollapsed = ref(true)

/**
 * Results only. A view preference, not a filter on the data: the events are still fetched and still selectable
 * by search, so turning it off restores every row without a re-read.
 */
const resultsOnly = ref(false)



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
    loaded.value = true
    if (selectedSeq.value !== null) {
      emit('context', contextFor(selectedSeq.value))
    }


    // Content just changed — a new event, or the reply growing. A reader at the bottom stays at the bottom.
    await followIfFollowing()
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

/**
 * Removes one event, or one event and everything after it, and says so plainly.
 *
 * <para>
 * Both are physical: the lines leave the log and only a content-free `purge #a..#b` marker stays (see
 * README §历史). That is why the view reports the range it removed rather than a generic "done" — a reader
 * who pressed "delete from here" needs to know how much went with it.
 * </para>
 */
async function removeFrom(seq: number, through: boolean) {
  error.value = ''
  notice.value = ''

  // The reader's own message goes back in the box. Its text is about to leave the log for good, and the only
  // copy left is the one the composer is holding.
  const body = through ? undefined : view.value?.items.find((item) => item.seq === seq)?.body

  try {
    if (through) {
      await props.api.retractFrom(seq)
      notice.value = `已撤回 #${seq} 及之后的事件（已从日志移除）`
    } else {
      await props.api.deleteEvent(seq)
      notice.value = `已删除 #${seq}（已从日志移除）`
      if (body !== undefined && restorableBody(view.value, seq)) {
        composer.value?.restore(body)
      }
    }

    // The selection is gone from the view, so the panel must not keep pointing at it.
    selectedSeq.value = null
    draft.value = ''
    revisions.value = []
    await refresh()
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

async function removeEvent() {
  if (selectedSeq.value === null) {
    error.value = '先选中一条事件'
    return
  }

  await removeFrom(selectedSeq.value, false)
}

async function retractFromSelected() {
  if (selectedSeq.value === null) {
    error.value = '先选中一条事件'
    return
  }

  await removeFrom(selectedSeq.value, true)
}

/**
 * Whether a removed event's text belongs back in the composer's box.
 *
 * Only the reader's own typed messages do. An assistant reply put back there would be sent as the human's
 * words on the next turn, and a scheduled task's text is not theirs to resend.
 */
function restorableBody(current: SessionView | null, seq: number): boolean {
  const item = current?.items.find((candidate) => candidate.seq === seq)
  return item !== undefined && item.kind === 'User' && item.source.toLowerCase() === 'text'
}

/** The row's own 撤回 button: fill the box, change nothing on the server. */
function restoreIntoComposer(seq: number) {
  const body = view.value?.items.find((item) => item.seq === seq)?.body
  if (body === undefined) {
    return
  }

  composer.value?.restore(body)
  notice.value = `已把 #${seq} 的原文放回输入框（可修改后重发）`
}

/** The composer stopped the running turn; the queue behind the badge is stale until someone re-reads it. */
function onCancel() {
  emit('changed')
  void refresh()
}

async function onSent(result: { itemId: string; deduplicated: boolean }) {
  // The queue is strictly FIFO, so a message always waits its turn. Saying otherwise here would be a promise
  // the runtime does not make: the interject option below is a preference about *this keystroke*, and the
  // wording here must keep describing what actually happened.
  notice.value = result.deduplicated
    ? `已入队 ${result.itemId}（重复提交，未新增）`
    : `已入队 ${result.itemId} · 等待执行`
  // The delivery lands in the queue, so the session is unchanged until a consumer runs.
  await refresh()

  // Sending is an explicit "show me what happens", so the view starts following the reply even if the reader
  // had scrolled back — unlike a reply that merely arrives while they are reading something older.
  following.value = true
  await followIfFollowing()

  emit('changed')
}

/**
 * How the view keeps up. A session being written to must show it while it happens, so a turn in flight is
 * polled quickly; an idle one is polled slowly, because the only thing that can change it then is work
 * started elsewhere (a scheduled task, another window) — and that still has to be caught early enough to show
 * its reply while it is being written, not after it finished.
 */
const BUSY_POLL_MS = 400
const IDLE_POLL_MS = 2000

let poller: ReturnType<typeof setInterval> | undefined
let lastPoll = 0

/**
 * True while a read is in flight.
 *
 * <para>
 * The poll must never overlap itself. `busy` deliberately has no throttle — a turn in flight has to be
 * watched closely — so with a slow read the 400ms timer fires again before the first request has come back,
 * and they stack. On a long session `GET /api/session` re-reads and serialises the whole log and can easily
 * exceed the interval; the stack then grows without bound, saturates the server, and the UI stops responding
 * to anything at all — including the cancel button, which only appears while `busy` is true, which is exactly
 * when the pile-up is worst. A poll that is already running is a poll that has nothing to add.
 * </para>
 */
let pollInFlight = false

async function poll() {
  if (pollInFlight) {
    return
  }

  const now = Date.now()
  if (view.value?.busy !== true && now - lastPoll < IDLE_POLL_MS) {
    return
  }

  lastPoll = now
  pollInFlight = true
  try {
    await refresh()
  } catch {
    // A failed poll is already surfaced by refresh() itself; the timer keeps going.
  } finally {
    pollInFlight = false
  }
}

/**
 * Whether the list follows new content. Decided by measuring, never assumed: opening a long history is not
 * "at the bottom", and a reply arriving must not pull a reader away from what they scrolled back to read.
 */
const scroller = ref<HTMLElement | null>(null)
const following = ref(false)

/**
 * Whether the inspector is a floating overlay rather than a column.
 *
 * <para>
 * A panel covering the list has nothing beside it to take width from, so the resize handle is removed rather
 * than left there doing nothing. The threshold itself lives in `narrow.ts`, next to the only copy of it.
 * </para>
 */
const { isOverlay } = useOverlayMode()

/** How close to the bottom still counts as the bottom — sub-pixel layout and the caret flip it otherwise. */
const FOLLOW_SLACK_PX = 32

/** Set while the view is moving itself, so the scroll event it causes is not read as the reader's intent. */
let pinning = false

function measureFollow() {
  if (pinning) {
    return
  }

  const el = scroller.value
  if (!el) {
    return
  }

  following.value = el.scrollHeight - el.scrollTop - el.clientHeight <= FOLLOW_SLACK_PX
}

/** After content changes, pin the bottom — but only for a reader who was already there. */
async function followIfFollowing() {
  await nextTick()

  const el = scroller.value
  if (el === null || !following.value) {
    return
  }

  pinning = true
  el.scrollTop = el.scrollHeight

  // A browser fires a scroll event for that assignment, and it lands before the next frame. Reading it would
  // measure a layout that is still settling and could turn following off right after turning it on — which is
  // how the view used to end up parked 36px short of the bottom, permanently.
  requestAnimationFrame(() => {
    pinning = false
  })
}

onMounted(async () => {
  await refresh()

  // Open on the newest event. A session is a conversation and its interesting end is the bottom, so this is
  // the one scroll the view does on its own. From here on it follows the reader's position instead: growing
  // text keeps the bottom pinned only while they are still there.
  await nextTick()
  following.value = true
  await followIfFollowing()

  poller = setInterval(() => {
    void poll()
  }, BUSY_POLL_MS)
})

onUnmounted(() => {
  if (poller !== undefined) {
    clearInterval(poller)
  }
})


/** Forwarded rather than decided here: the shell owns the API call and the queue. */
function answer(approvalId: string, approved: boolean) {
  emit('answer', approvalId, approved)
}

/**
 * Puts the caret in this view's search box, for Ctrl+K.
 *
 * <para>
 * The shell asks because only it knows which view is showing — and only two of them have a search box, so the
 * others simply do not expose this and the shortcut does nothing there rather than pretending otherwise.
 * </para>
 *
 * <para>
 * The query is cleared first: someone reaching for a filter expects to be shown the whole list and then narrow
 * it, not to inherit a filter they forgot was there and be confused by the empty result.
 * </para>
 */
const searchInput = ref<HTMLInputElement | null>(null)

function focusSearch() {
  query.value = ''
  searchInput.value?.focus()
}

/** For Escape. Closes a panel the reader may not have meant to open. */
function collapseInspector() {
  inspectorCollapsed.value = true
}

defineExpose({ refresh, focusSearch, collapseInspector })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="session-refresh" @click="refresh">刷新</button>
      <label class="search-field">
        <Icon name="search" :size="14" class="search-glyph" />
        <input
          ref="searchInput"
          v-model="query"
          placeholder="检索正文…"
          title="Ctrl+K"
          aria-label="检索事件正文"
          data-test="session-query"
        />
      </label>
      <select v-model="sourceFilter" data-test="session-source-filter" aria-label="来源">
        <option v-for="option in sources" :key="option" :value="option">
          {{ option === 'all' ? '来源：全部' : option }}
        </option>
      </select>
      <!-- `aria-controls` names what this toggles, and `aria-expanded` says which way it currently is — the
           label alone ("› 收起检视") is the state, so a screen reader reading it out of context gets a command
           instead of a fact. -->
      <button
        class="ghost"
        data-test="session-inspector-toggle"
        aria-controls="session-inspector"
        :aria-expanded="!inspectorCollapsed"
        @click="inspectorCollapsed = !inspectorCollapsed"
      >
        {{ inspectorCollapsed ? "‹ 展开检视" : "› 收起检视" }}
      </button>
      <button
        class="ghost"
        :aria-pressed="resultsOnly"
        data-test="session-results-only"
        title="只保留用户消息与最终回复，工具调用行全部隐藏"
        @click="resultsOnly = !resultsOnly"
      >
        <Icon :name="resultsOnly ? 'check' : 'square'" :size="14" />
        只看结果
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

    <!--
      The outcome line sits directly under the toolbar, not at the foot of the column.

      <para>
      It used to come after the composer, which put it at the very bottom of the window — so a delete that
      reported what it had just removed, or an error from a failed save, was only on screen if you happened to
      be looking at the composer. In a session a few hundred events long, the one line saying what just happened
      was the one line that scrolled out of sight first.
      </para>

      <para>
      Here the toolbar does not scroll and neither does this, so both are always visible: the toolbar carries
      the counts, this carries the consequence. `.notice`'s top border is what separates them, which is why the
      rule still draws it even though this is no longer the last thing in the column.
      </para>
    -->
    <p v-if="notice" class="muted notice" data-test="session-notice">{{ notice }}</p>
    <p v-if="error" class="error notice" data-test="error">{{ error }}</p>

    <div class="split" :class="{ 'is-inspector-collapsed': inspectorCollapsed }">
      <div v-if="!inspectorCollapsed" class="inspector-scrim" @click="inspectorCollapsed = true" />
      <div ref="scroller" class="scroll" @scroll="measureFollow">
        <EventList
          :items="visible"
          :selected-seq="selectedSeq"
          :results-only="resultsOnly"
          @select="select"
          @remove="removeFrom($event, false)"
          @retract="removeFrom($event, true)"
          @restore="restoreIntoComposer"
        />
        <p v-if="!loaded" class="pending" data-test="session-pending">正在读取会话…</p>
        <p v-else-if="visible.length === 0" class="empty">（无匹配事件）</p>
      </div>

      <Inspector
        id="session-inspector"
        test-prefix="session"
        title="编辑选中事件（追加修订，原文保留）"
        :target="selected ? `#${selected.seq} · ${selected.kind} · ${selected.source} · 修订 ${selected.revisionCount} 次` : '未选中'"
        :draft="draft"
        :revisions="revisions"
        :note="revisionNote"
        :resizable="!isOverlay"
        @save="save"
        @delete="removeEvent"
        @retract="retractFromSelected"
      />
    </div>

    <ApprovalBanner
      :pending="approvals ?? []"
      :desktop="desktop ?? null"
      @answer="answer"
    />

    <Composer
      ref="composer"
      :api="api"
      :summary="summary"
      :consumer="consumer"
      :approval="approval"
      :gears="gears"
      :default-gear="defaultGear"
      :queue-items="queueItems"
      @sent="onSent"
      @approval="emit('approval', $event)"
      @failed="error = $event"
      @cancel="onCancel"
    />
  </div>
</template>

<style scoped>
.split {
  flex: 1;
  min-block-size: 0;
}

/* The meter turns loud only when the window is nearly full — that is when it matters. */
.context-meter {
  white-space: nowrap;
}

.context-tight {
  color: var(--warn);
  font-weight: 600;
}
</style>
