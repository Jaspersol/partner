<script setup lang="ts">
import { t, tr } from '../i18n'
import { computed, inject, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { TOPBAR_TOOLS } from '../topbarTools'
import type {
  AgentJobView,
  ConsumerState,
  DesktopState,
  PartnerApiSurface,
  PendingApproval,
  QueueItem,
  QueueSummary,
  SessionItem,
  SessionView,
  SessionWindow,
} from '../api'
import { sourceLabel } from '../sessionKind'
import ApprovalBanner from '../components/ApprovalBanner.vue'
import Icon from '../components/Icon.vue'
import Segmented from '../components/Segmented.vue'
import Composer from '../components/Composer.vue'
import EventList from '../components/EventList.vue'
import DayPicker from '../components/DayPicker.vue'
import SubAgentCards from '../components/SubAgentCards.vue'

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
  changed: []
  approval: [gear: string]
  /** The human's answer to a parked tool call; the shell owns the call itself. */
  answer: [approvalId: string, approved: boolean]
  /** A running turn was stopped, so the queue needs re-reading. */
  cancel: [itemId: string]
}>()

/** Where the search goes: the top bar's slot when there is one, otherwise right here. */
const topbarTools = inject(TOPBAR_TOOLS, ref<HTMLElement | null>(null))

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
  return t('上下文 {format} / {format2}（{percent}%）', { format: format(usage.usedTokens), format2: format(usage.budgetTokens), percent: percent })
})

const contextHint = computed(() => {
  const usage = view.value?.context
  if (!usage) {
    return ''
  }

  const folded = usage.foldedItems > 0 ? t(' · 已折叠 {foldedItems} 条', { foldedItems: usage.foldedItems }) : ''
  return t('窗口 {contextTokens} token（预留输出 {maxTokens}）· 保留 {keptItems} 条{folded}', { contextTokens: usage.contextTokens, maxTokens: usage.maxTokens, keptItems: usage.keptItems, folded: folded })
})

function format(tokens: number): string {
  return tokens >= 1000 ? `${(tokens / 1000).toFixed(1)}k` : String(tokens)
}

//
// The session is one log; the days are only a way of looking at it. Nothing below changes what is stored or
// where a message goes — a message sent while looking at an old day still lands in the log's tail.

/** The day the reader chose, or null for "the newest" (which then follows along). */
const selectedDay = ref<string | null>(null)

/**
 * The day shown as "newest" while work is running across midnight.
 *
 * <para>
 * A turn that started yesterday and is still writing at 00:05 is saved under today, but the reader is in the
 * middle of it: the screen must not jump to a new, nearly empty day under them. So while anything is running or
 * queued the newest day stays what it was (the window is *open*, so the new events still show up here), and
 * the switch happens on the first idle read.
 * </para>
 */
const heldDay = ref<string | null>(null)

/** Running, or something in the queue that will run. A blocked queue (a failure) is not going to run. */
const working = computed(() => {
  const summary = props.summary
  const queued = summary !== undefined && summary.failed === 0 && summary.pending > 0
  const running = summary !== undefined && (summary.runningItemId ?? '') !== ''
  return view.value?.busy === true || queued || running
})

function windowRequest(): SessionWindow {
  const offsetMinutes = -new Date().getTimezoneOffset()
  if (selectedDay.value !== null) {
    return { day: selectedDay.value, open: false, offsetMinutes }
  }

  return { day: working.value && heldDay.value !== null ? heldDay.value : 'latest', open: true, offsetMinutes }
}

/** The days on offer: while the newest day is held back, the days after it are not listed yet. */
const days = computed(() => {
  const all = view.value?.days ?? []
  const limit = selectedDay.value === null ? heldDay.value : null
  return limit !== null && working.value ? all.filter((entry) => entry.day <= limit) : all
})

/** What the picker shows as chosen. */
const shownDay = computed(() => selectedDay.value ?? (working.value ? heldDay.value : null) ?? view.value?.day ?? '')

function chooseDay(day: string) {
  const newest = days.value[days.value.length - 1]?.day
  selectedDay.value = day === newest ? null : day
  // Landing on the newest day means "follow it". If work is running that day is the held one, so it stays held.
  if (selectedDay.value === null) heldDay.value = newest ?? null
  following.value = selectedDay.value === null
  clearSelection()
  void refresh().then(async () => {
    await nextTick()
    const el = scroller.value
    if (el === null) return
    pinning = true
    el.scrollTop = selectedDay.value === null ? el.scrollHeight : 0
    requestAnimationFrame(() => {
      pinning = false
    })
  })
}

async function refresh() {
  try {
    const next = await props.api.session(undefined, undefined, windowRequest())
    // A turn that ends has usually just handed work to a sub-agent. Read the jobs before showing it as over, so
    // the screen goes straight from "running" to "delegating" and never through "finished".
    if (view.value?.busy === true && next.busy !== true) await refreshJobs()
    view.value = next
    // The chosen day no longer exists (its conversation was deleted): the server answered with the newest.
    if (selectedDay.value !== null && next.day !== selectedDay.value) selectedDay.value = null
    if (selectedDay.value === null && next.day !== undefined && next.day !== null && !(working.value && heldDay.value !== null)) {
      heldDay.value = next.day
    }

    loaded.value = true
    // Content just changed — a new event, or the reply growing. A reader at the bottom stays at the bottom.
    await followIfFollowing()
  } catch {
    // A failed read says nothing on screen any more. The last good list stays where it is and the poll tries
    // again a few hundred milliseconds later, which is the same thing the reader would do by hand.
  }
}

/**
 * Selecting a row (from the keyboard) marks it; nothing is shown for it. There is no editor behind the selection any more: what
 * a row can do to a row — 复用, 删除, 撤回 — is on the row itself, one hover away.
 */
function select(seq: number) {
  selectedSeq.value = seq
}

/**
 * Deletes the latest conversation as a whole.
 *
 * <para>
 * Deleting is physical: the lines leave the log and only a content-free `purge #a..#b` marker stays. Nothing on
 * the page announces it — the rows are simply gone, which is what was asked for.
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
  void refreshJobs()
}

async function onSent() {
  // Sending writes to the end of the log, so show the end: leave an older day for the newest one.
  if (selectedDay.value !== null) {
    selectedDay.value = null
    heldDay.value = null
  }

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


/** The delegated jobs, newest read. Only the running ones are drawn (see SubAgentCards). */
const jobs = ref<AgentJobView[]>([])
const runningJobs = computed(() => jobs.value.filter((job) => job.status === 'running'))

/** A job's round moves at the speed of a model call, so while one runs (or a turn that may start one) it is read every second. */
const JOBS_ACTIVE_MS = 1000
const JOBS_IDLE_MS = 3000
let lastJobsPoll = 0
let jobsRequest: Promise<void> | null = null

/** Shared while a read is in flight, so a caller that needs the answer waits for that one instead of skipping it. */
function refreshJobs(): Promise<void> {
  if (jobsRequest !== null) {
    return jobsRequest
  }

  lastJobsPoll = Date.now()
  jobsRequest = (async () => {
    try {
      jobs.value = await props.api.agentJobs()
    } catch {
      // An older build has no jobs endpoint, and a failed read keeps the cards that were there: a card that blinks
      // out on a hiccup and comes back is worse than one a second out of date.
    } finally {
      jobsRequest = null
    }
  })()

  return jobsRequest
}

/**
 * How long a finished sub-agent still counts as work in progress.
 *
 * <para>
 * A job is marked finished first and its report is queued a moment later, so for a beat nothing is running and
 * nothing is queued, although the main agent is about to answer it. Without the grace the turn would fold, then
 * unfold when the answer starts.
 * </para>
 */
const JOBS_SETTLE_MS = 3000
const settling = ref(false)
let settleTimer: ReturnType<typeof setTimeout> | undefined

watch(
  () => runningJobs.value.length,
  (now, before) => {
    clearTimeout(settleTimer)
    if (now > 0) {
      settling.value = false
    } else if (before > 0) {
      settling.value = true
      settleTimer = setTimeout(() => {
        settling.value = false
      }, JOBS_SETTLE_MS)
    }
  },
)

/** A sub-agent is working, or only just stopped. */
const delegating = computed(() => runningJobs.value.length > 0 || settling.value)

/**
 * The newest turn is not over: the model is running, a message is queued, or a sub-agent is working and the
 * main agent still has to answer its report. The fold and the composer's stop button both wait for this.
 */
const inProgress = computed(() => working.value || delegating.value)

function pollJobs() {
  const active = runningJobs.value.length > 0 || view.value?.busy === true
  if (Date.now() - lastJobsPoll >= (active ? JOBS_ACTIVE_MS : JOBS_IDLE_MS)) {
    void refreshJobs()
  }
}

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
  pollJobs()

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
  // measure a layout that is still settling and could turn following off right after turning it on, leaving
  // the view parked short of the bottom.
  requestAnimationFrame(() => {
    pinning = false
  })
}

onMounted(async () => {
  void refreshJobs()
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
  clearTimeout(settleTimer)
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

/**
 * The search is a single icon until it is wanted. It stays open while it is doing something (a query, a source
 * filter) so a filtered list never has an invisible filter on it, and folds back on Esc.
 */
const searching = ref(false)

function focusSearch() {
  query.value = ''
  searching.value = true
  void nextTick(() => searchInput.value?.focus())
}

function closeSearch() {
  query.value = ''
  sourceFilter.value = 'all'
  searching.value = false
}

const searchOpen = computed(() => searching.value || query.value !== '' || sourceFilter.value !== 'all')

const sourceOptions = computed(() =>
  sources.value.map((option) => ({ value: option, label: option === 'all' ? t('全部') : sourceLabel(option) })),
)

/** What the composer's ring and its tooltip say. Absent until the API has said it. */
const contextRing = computed(() => {
  const usage = view.value?.context
  return usage ? { fraction: usage.fraction, label: contextLabel.value, hint: contextHint.value } : undefined
})

/** Three things to say to an empty conversation. They only fill the box; sending is still the reader's call. */
const STARTERS = [tr('介绍一下你能做什么'), tr('帮我记住：我喜欢简洁的回答'), tr('明天早上九点提醒我开会')]

function starter(text: string) {
  composer.value?.restore(text)
}

defineExpose({ refresh, focusSearch })
</script>

<template>
  <div class="view">
    <!--
      The conversation scrolls under the top bar and under the dock; the padding in `.g-page` is what keeps its
      first and last lines clear of them at rest. No toolbar: search is an icon (below), and the context meter
      lives in the composer, where the thing it measures is being written.
    -->
    <div ref="scroller" class="scroll session-scroll" @scroll="measureFollow">
      <div class="g-page chat">
        <EventList
          :items="visible"
          :selected-seq="selectedSeq"
          :latest-from-seq="conversation?.latestFromSeq ?? null"
          :withdrawn="withdrawn !== null"
          :busy="view?.busy === true"
          :active="inProgress"
          @select="select"
          @remove="deleteLatest"
          @withdraw="withdrawFrom"
          @restore="restoreIntoComposer"
        />

        <!--
          Where the log stops. A pending withdrawal hid a range of rows, so the one thing to say about it sits
          exactly where they were — under the newest message — and is both the report and the remedy.
        -->
        <div v-if="withdrawn !== null" class="withdrawn" data-test="session-withdrawn">
          <span>{{ t('已撤回一部分对话') }}</span>
          <button
            class="ghost"
            data-test="session-withdrawn-cancel"
            :title="t('把撤回隐藏的对话放回来。发送新消息后就无法恢复了。')"
            @click="cancelWithdrawal"
          >
            {{ t('取消撤回') }}
          </button>
        </div>

        <div v-if="!loaded" class="skeleton-stack" aria-hidden="true">
          <div class="skeleton-line" />
          <div class="skeleton-line" />
          <div class="skeleton-line" />
        </div>

        <!-- An empty conversation gets an invitation, not a blank page. Only once the first read has come back:
             before that, "empty" would be a confident wrong answer. -->
        <div v-else-if="(view?.items.length ?? 0) === 0" class="empty-state" data-test="session-empty">
          <p class="empty-title">{{ t('说点什么吧') }}</p>
          <div class="starters">
            <button v-for="text in STARTERS" :key="text" class="starter" @click="starter(text)">{{ t(text) }}</button>
          </div>
        </div>

        <p v-else-if="visible.length === 0" class="empty" data-test="session-no-match">{{ t('没有匹配的对话') }}</p>
      </div>
    </div>

    <!-- Search: one icon until it is wanted (Ctrl+K), then a glass field with the source filter under it. -->
    <Teleport :to="topbarTools ?? 'body'" :disabled="topbarTools === null">
    <DayPicker v-if="days.length > 0" :days="days" :model-value="shownDay" @update:model-value="chooseDay" />
    <div class="searchbar" :class="{ 'is-open': searchOpen }">
      <button
        v-if="!searchOpen"
        class="ghost search-open"
        :aria-label="t('搜索对话')"
        :title="t('搜索对话（Ctrl+K）')"
        data-test="session-search-open"
        @click="focusSearch"
      >
        <Icon name="search" :size="16" />
      </button>
      <div v-else class="search-panel g-float" @keydown.escape="closeSearch">
        <label class="search-field">
          <Icon name="search" :size="14" class="search-glyph" />
          <input
            ref="searchInput"
            v-model="query"
            :placeholder="t('搜索对话')"
            title="Ctrl+K"
            :aria-label="t('搜索对话')"
            data-test="session-query"
          />
        </label>
        <Segmented
          v-if="sources.length > 2"
          v-model="sourceFilter"
          :options="sourceOptions"
          :label="t('来源')"
          data-test="session-source-filter"
        />
        <button class="ghost" :aria-label="t('关闭搜索')" data-test="session-search-close" @click="closeSearch">
          <Icon name="x" :size="14" />
        </button>
      </div>
    </div>
    </Teleport>

    <div class="dock-stack">
      <ApprovalBanner
        :pending="approvals ?? []"
        :desktop="desktop ?? null"
        @answer="answer"
      />

      <SubAgentCards :jobs="jobs" />

      <Composer
        ref="composer"
        :api="api"
        :summary="summary"
        :consumer="consumer"
        :approval="approval"
        :gears="gears"
        :default-gear="defaultGear"
        :queue-items="queueItems"
        :delegating="delegating"
        :jobs="runningJobs"
        :context="contextRing"
        @sent="onSent"
        @approval="emit('approval', $event)"
        @cancel="onCancel"
      />
    </div>
  </div>
</template>

<style scoped>
.session-scroll {
  flex: 1;
  min-block-size: 0;
}

/* The page's bottom padding clears the dock at rest (a two-line box, one row of tools); the dock grows over the
   list when the box does, which is the glass doing its job. */
.chat {
  padding-block-end: 11rem;
}

/* Floating things: the dock stack is anchored to the bottom and centred on the reading column. Its container
   ignores the pointer so the empty space around the dock still scrolls the list underneath. */
.dock-stack {
  position: absolute;
  inset-inline: 0;
  inset-block-end: 0;
  z-index: 15;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
  justify-items: center;
  padding: 0 var(--space-5) var(--space-3);
  pointer-events: none;
}

.dock-stack > * {
  inline-size: min(var(--column), 100%);
  pointer-events: auto;
}

/* Lives in the top bar (teleported), so it is the anchor for the panel that drops from it. */
.searchbar {
  position: relative;
  display: inline-flex;
}

.search-panel {
  position: absolute;
  inset-block-start: calc(100% + var(--space-2));
  inset-inline-end: 0;
  z-index: 30;
}

.search-open {
  inline-size: 2.25rem;
  padding: 0;
  border-radius: var(--radius-sm);
  background: var(--glass-2);
  border: 1px solid var(--glass-edge);
  box-shadow: var(--glass-hi), var(--shadow-1);
}

.search-panel {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2);
  border-radius: var(--radius-pill);
}

.search-panel input {
  inline-size: 12rem;
  border-radius: var(--radius-pill);
}

.withdrawn {
  justify-self: center;
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-1) var(--space-1) var(--space-4);
  border-radius: var(--radius-pill);
  background: var(--surface-2);
  color: var(--text-2);
  font-size: var(--fs-meta);
}

.withdrawn button {
  min-block-size: 1.75rem;
  border-radius: var(--radius-pill);
  font-size: var(--fs-meta);
}

.empty-state {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  justify-items: center;
  gap: var(--space-4);
  padding-block: var(--space-7);
}

.empty-title {
  margin: 0;
  font-size: var(--fs-title);
  font-weight: 600;
}

.starters {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: var(--space-2);
}

.starter {
  border-radius: var(--radius-pill);
  color: var(--text-2);
}
</style>
