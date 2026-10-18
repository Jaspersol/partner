<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import type {
  ConsumerState,
  DesktopState,
  PartnerApiSurface,
  PendingApproval,
  QueueItem,
  QueueSummary,
  SessionItem,
  SessionView,
} from '../api'
import { sourceLabel } from '../sessionKind'
import ApprovalBanner from '../components/ApprovalBanner.vue'
import Icon from '../components/Icon.vue'
import Composer from '../components/Composer.vue'
import EventList from '../components/EventList.vue'

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
const query = ref('')
const sourceFilter = ref('all')
const composer = ref<InstanceType<typeof Composer> | null>(null)

const conversation = computed(() => view.value?.conversation ?? null)
const withdrawn = computed(() => conversation.value?.withdrawn ?? null)

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

async function refresh() {
  try {
    view.value = await props.api.session()
    loaded.value = true
    if (selectedSeq.value !== null) {
      emit('context', contextFor(selectedSeq.value))
    }

    // Content just changed — a new event, or the reply growing. A reader at the bottom stays at the bottom.
    await followIfFollowing()
  } catch {
    // A failed read says nothing on screen any more. The last good list stays where it is and the poll tries
    // again a few hundred milliseconds later, which is the same thing the reader would do by hand.
  }
}

function contextFor(seq: number): string {
  const item = view.value?.items.find((candidate) => candidate.seq === seq)
  return item ? `会话 #${item.seq} · ${item.kind}` : '会话'
}

/**
 * Selecting a row marks it and names it in the top bar. There is no editor behind the selection any more: what
 * a row can do to a row — 复用, 删除, 撤回 — is on the row itself, one hover away.
 */
function select(seq: number) {
  selectedSeq.value = seq
  emit('context', contextFor(seq))
}

/**
 * Deletes the latest conversation as a whole.
 *
 * <para>
 * Deleting is physical: the lines leave the log and only a content-free `purge #a..#b` marker stays (see
 * README §历史). Nothing on the page announces it — the rows are simply gone, which is what was asked for.
 * </para>
 */
async function deleteLatest(seq: number) {
  // The reader's own message goes back in the box. The deleted conversation starts at its first message, so
  // that is the text worth keeping — the row pressed may be a later reply. Its text is about to leave the log
  // for good, and the only copy left is the one the composer is holding.
  const startSeq = conversation.value?.latestFromSeq ?? seq
  const first = view.value?.items.find((item) => item.seq === startSeq)
  const body = first !== undefined && restorableItem(first) ? first.body : undefined

  try {
    await props.api.deleteLatest(seq)
    if (body !== undefined) {
      composer.value?.restore(body)
    }

    clearSelection()
    await refresh()
    emit('changed')
  } catch {
    // Nothing to report and no place to report it: the list is re-read below either way, so a refusal means
    // the rows are still there.
    await refresh()
  }
}

/**
 * Withdraws the selected conversation and everything after it.
 *
 * <para>
 * Nothing is deleted: the range is hidden behind a retraction that stands until the reader sends a new
 * message. That is why this takes one click and needs no confirmation.
 * </para>
 */
async function withdrawFrom(seq: number) {
  try {
    await props.api.withdrawFrom(seq)
  } catch {
    // As above: a refused withdrawal leaves the list as it was, which the re-read shows.
  }

  // The withdrawn rows are no longer in the view, so nothing may stay selected.
  clearSelection()
  await refresh()
  emit('changed')
}

/**
 * Takes a pending withdrawal back, so the rows it hid come back.
 *
 * <para>
 * A withdrawal is the one history change with a way back, and it only holds until the next message is sent — so
 * the way back is offered, and it is offered where the missing rows were: directly under the newest message,
 * which is where the log now stops. Nothing else says a withdrawal happened; the button is both the report and
 * the remedy.
 * </para>
 */
async function cancelWithdrawal() {
  try {
    await props.api.cancelWithdrawal()
  } catch {
    // A refused cancel leaves the withdrawal standing. The re-read below is what shows that, either way.
  }

  clearSelection()
  await refresh()
  emit('changed')
}

/** The selection is a pointer into the list: a row that left the view must not stay selected. */
function clearSelection() {
  selectedSeq.value = null
}

/**
 * Whether a removed conversation's first message belongs back in the composer's box.
 *
 * Only the reader's own typed messages do. An assistant reply put back there would be sent as the human's
 * words on the next turn, and a scheduled task's text is not theirs to resend.
 */
function restorableItem(item: SessionItem): boolean {
  return item.kind === 'User' && item.source.toLowerCase() === 'text'
}

/** The row's own 复用 button: fill the box, change nothing on the server. */
function restoreIntoComposer(seq: number) {
  const body = view.value?.items.find((item) => item.seq === seq)?.body
  if (body === undefined) {
    return
  }

  composer.value?.restore(body)
}

/** The composer stopped the running turn; the queue behind the badge is stale until someone re-reads it. */
function onCancel() {
  emit('changed')
  void refresh()
}

async function onSent() {
  // The delivery lands in the queue, so the session is unchanged until a consumer runs — and the queue above
  // the composer is where the reader sees it.
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

defineExpose({ refresh, focusSearch })
</script>

<template>
  <div class="view">
    <div class="toolbar">
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
          {{ option === 'all' ? '全部' : sourceLabel(option) }}
        </option>
      </select>
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
    </div>

    <div class="split">
      <div ref="scroller" class="scroll" @scroll="measureFollow">
        <EventList
          :items="visible"
          :selected-seq="selectedSeq"
          :latest-from-seq="conversation?.latestFromSeq ?? null"
          :withdrawn="withdrawn !== null"
          :busy="view?.busy === true"
          @select="select"
          @remove="deleteLatest"
          @withdraw="withdrawFrom"
          @restore="restoreIntoComposer"
        />
        <!--
          Where the log stops. A pending withdrawal hid a range of rows, so the one thing to say about it sits
          exactly where they were — under the newest message — rather than in a banner far from the gap it
          describes. Shown only while a withdrawal is actually pending.
        -->
        <button
          v-if="withdrawn !== null"
          class="ghost withdrawn-undo"
          data-test="session-withdrawn-cancel"
          title="把撤回隐藏的对话放回来（发送新消息后就无法恢复了）"
          @click="cancelWithdrawal"
        >
          取消撤回
        </button>

        <div v-if="!loaded" class="skeleton-stack" aria-hidden="true">
          <div class="skeleton-line" />
          <div class="skeleton-line" />
          <div class="skeleton-line" />
        </div>
      </div>
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
      @cancel="onCancel"
    />
  </div>
</template>

<style scoped>
/* One column, explicitly. `.split` is a two-track grid globally because the memory view puts a panel in the
   second one; this view has no panel any more, and inheriting that rule left an empty track on the right — a
   band of nothing as wide as the panel used to be. */
.split {
  flex: 1;
  min-block-size: 0;
  grid-template-columns: minmax(0, 1fr);
}

/* The way back from a withdrawal. It stands where the hidden rows stood — just under the newest message — at the
   same left gutter the rows are padded to, so it reads as the end of the log rather than as a control bolted
   under it. Quiet by default: it is an offer, not another event. */
.withdrawn-undo {
  margin: var(--space-2) var(--space-4) var(--space-4);
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
