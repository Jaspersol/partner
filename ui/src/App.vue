<script setup lang="ts">
import { computed, onMounted, onUnmounted, provide, ref, shallowRef } from 'vue'
import { TOPBAR_TOOLS } from './topbarTools'
import {
  ApiError,
  PartnerApi,
  summarizeQueue,
  type PartnerApiSurface,
  type ConsumerState,
  type QueueItem,
  type QueueSummary,
  type DesktopState,
  type PendingApproval,
  type AgentJobView,
} from './api'
import { resolveConnection } from './config'
import { useRailOnlyMode } from './narrow'
import { applyReducedTransparency, applyTheme, nextTheme, readReducedTransparency, readTheme, type Theme } from './theme'
import SideBar, { type NavItem } from './components/SideBar.vue'
import { setGearLabels } from './gears'
import StatusBar from './components/StatusBar.vue'
import ToastHost from './components/ToastHost.vue'
import TopBar from './components/TopBar.vue'
import MemoryView from './views/MemoryView.vue'
import SessionView from './views/SessionView.vue'
import McpView from './views/McpView.vue'
import SchedulesView from './views/SchedulesView.vue'
import SkillsView from './views/SkillsView.vue'
import PromptsView from './views/PromptsView.vue'
import SettingsView from './views/SettingsView.vue'
import { version } from '../package.json'

/**
 * The shell: top bar, body (sidebar + content), status bar. It owns exactly what the regions share — the
 * connection, the queue summary, the active view — and nothing else. Each view reads its own data; the shell
 * never caches a view's contents.
 */

const NAV: NavItem[] = [
  { key: 'session', label: '会话', icon: 'chat' },
  // Right under the conversation: a timed task's output lands there.
  { key: 'schedules', label: '定时', icon: 'clock' },
  { key: 'memory', label: '记忆', icon: 'memory' },

  // Two capability pages of their own rather than cards inside settings: both are lists a person reads and
  // changes, and need the room a settings card does not give.
  { key: 'mcp', label: 'MCP', icon: 'mcp' },
  { key: 'skills', label: '技能', icon: 'skill' },

  // What the agents are told, and who they may hand work to.
  { key: 'prompts', label: 'agent', icon: 'prompt' },
  { key: 'settings', label: '设置', icon: 'settings' },
]

/** How often the badge re-reads the queue: blocked work should not need a click to appear. */
const QUEUE_POLL_MS = 5000

/**
 * A tool call waiting for a human, shown here rather than inside a view: the answer is part of a turn that is
 * already parked, and a prompt that only exists on the session tab is a prompt nobody sees while they are
 * elsewhere.
 */
const APPROVAL_POLL_MS = 500

/** The top bar's slot for the page's own tools; the bar fills it, the page teleports into it. */
const topbarTools = ref<HTMLElement | null>(null)
provide(TOPBAR_TOOLS, topbarTools)

const approvals = ref<PendingApproval[]>([])
const desktop = ref<DesktopState | null>(null)
/**
 * The delegated job on screen, if any.
 *
 * <para>
 * Polled with the queue because it answers the same question — "is something working, and can I stop it". Kept
 * as the first running one rather than a list: the status bar has room for one line.
 * </para>
 */
const runningJob = ref<AgentJobView | null>(null)


/**
 * The gear menu and its default, for the composer's picker. Read from settings rather than fetched with the
 * summary: they only change when settings are saved.
 */
const gears = ref<string[]>([])
const defaultGear = ref('')
const active = ref('session')
/**
 * What the reader chose, not what is true.
 *
 * <para>
 * Kept apart from the measured `railOnly`: the effective state is derived from both, and the toggle button is
 * not offered at all when the window is too narrow for it to mean anything.
 * </para>
 */
const userWantsSidebar = ref(false)
const { railOnly } = useRailOnlyMode()
const sidebarCollapsed = computed(() => userWantsSidebar.value || railOnly.value)
const summary = ref<QueueSummary | null>(null)
/**
 * The queue behind the badge, and what the composer's list is drawn from.
 *
 * <para>
 * One read, two readings — the badge's counts and the composer's rows — so they cannot disagree.
 * </para>
 */
const queueItems = ref<QueueItem[]>([])
const consumer = ref<ConsumerState | null>(null)

/** The line the top bar shows in the middle. Only the session view sets it, to name the selected row. */
const context = ref('')
const connectionError = ref('')
const endpoint = ref('')
const theme = ref<Theme>(readTheme())
const api = shallowRef<PartnerApiSurface | null>(null)

/**
 * The view currently on screen.
 *
 * <para>
 * One ref bound to whichever branch renders. The shell needs it for the shortcuts: Ctrl+K has to reach
 * <em>this</em> view's search box, and only the view knows whether it has one.
 * </para>
 */
interface ActiveView {
  refresh?: () => Promise<void>
  focusSearch?: () => void
  collapseInspector?: () => void
  /** True when the view holds edits that only its own Save button can commit. */
  hasUnsavedChanges?: () => boolean
}
const activeView = ref<ActiveView | null>(null)

/**
 * Switching views, with one thing asked first.
 *
 * <para>
 * The views are `v-if`, so leaving one destroys it. Saving is explicit, so switching tabs would silently throw
 * away a page of edits; the view is asked, and only asked when it has unsaved changes.
 * </para>
 */
function select(key: string) {
  const leaving = activeView.value?.hasUnsavedChanges?.() === true
  if (leaving && !window.confirm('当前页面有未保存的修改，切换后将丢失。继续？')) {
    return
  }

  active.value = key
  context.value = ''
}

const connected = computed(() => api.value !== null)
const viewLabel = computed(() => NAV.find((item) => item.key === active.value)?.label ?? '')

let poller: ReturnType<typeof setInterval> | undefined
let approvalPoller: ReturnType<typeof setInterval> | undefined

/**
 * True once a refresh has failed and nothing has succeeded since.
 *
 * A failed read must change the answer without erasing it: the badge can say "unknown", but the stop button
 * must not disappear while the turn it controls is still running.
 */
const stale = ref(false)

async function refreshSummary() {
  if (!api.value) {
    return
  }

  try {
    const queue = await api.value.queue()
    summary.value = summarizeQueue(queue)
    queueItems.value = queue.items
    consumer.value = await api.value.consumer()
    approvals.value = (await api.value.approvals()).pending ?? []

    // Always fetched: it carries the approval gear the composer offers and the entities that turn an
    // approval's entityId into a window a human recognises. Both are read-only and in-memory.
    desktop.value = await api.value.desktop()
    stale.value = false
  } catch (failure) {
    // A count we no longer trust must not be shown: `summary = null` makes the badge read 队列 —. But an action
    // must not disappear because a read failed: the composer's interrupt button and queue list are derived from
    // `queueItems` / `consumer`, so those are kept. A control that disappears while the thing it controls is
    // still running is worse than one showing something slightly old. Pressing interrupt on a stale read is
    // harmless — `cancel` answers 404 and the composer reports 「已完成，无法取消」.
    if (failure instanceof ApiError && failure.status === 401) {
      connectionError.value = 'token 失效（401）——请重启壳以获取新令牌'
    }

    stale.value = summary.value !== null
  }

  // A separate read, deliberately: an older API answers 404 here, and that must not mark the queue stale or
  // clear the badge. A failed job read keeps the last known job.
  await refreshJobs()
}

/**
 * The delegated job, on the same five-second cadence as the queue.
 *
 * Kept apart from `refreshSummary` because it is a different surface with a different failure: a build without
 * the jobs endpoint should leave the status bar quiet rather than flag the whole connection as stale.
 */
async function refreshJobs() {
  if (!api.value) {
    return
  }

  try {
    runningJob.value = (await api.value.agentJobs()).find((job) => job.status === 'running') ?? null
  } catch {
    // Keep what we last knew: the stop button is derived from it.
  }
}

/** Stops the running job. A 404 means it finished between two polls — nothing to stop, not an error. */
async function cancelJob(jobId: string) {
  if (!api.value) {
    return
  }

  try {
    await api.value.cancelAgentJob(jobId)
  } catch (failure) {
    connectionError.value = String(failure)
  }

  await refreshJobs()
}

/** Answers a parked tool call. A failed answer is left on screen rather than swallowed: it is still waiting. */
async function answerApproval(approvalId: string, approved: boolean) {
  if (!api.value) {
    return
  }

  try {
    await api.value.answerApproval(approvalId, approved)
  } catch (failure) {
    connectionError.value = String(failure)
  }

  await refreshSummary()
}

/** The composer's gear choice, applied where the setting lives. */
async function setApprovalGear(gear: string) {
  if (!api.value) {
    return
  }

  try {
    await api.value.setApprovalGear(gear)
  } catch (failure) {
    connectionError.value = String(failure)
  }

  await refreshSummary()
}

/**
 * Re-reads the gear menu after a settings save, so the composer offers what was just stored. A failed read
 * keeps the last known list: emptying the picker would silently drop a gear the user can no longer select.
 */
async function refreshGears() {
  if (!api.value) {
    return
  }

  try {
    const settings = await api.value.settings()
    gears.value = settings.model.reasoningGears ?? []
    setGearLabels(settings.model.reasoningLabels)
    defaultGear.value = settings.model.reasoningEffort ?? ''
  } catch (failure) {
    connectionError.value = String(failure)
  }
}

/**
 * Keyboard shortcuts.
 *
 * <para>
 * Ctrl+K to search, `1`–`7` to switch view, Ctrl+B for the rail.
 * </para>
 *
 * <para>
 * The digits are inert while the caret is in a field: a shortcut that swallows half the characters of a message
 * is worse than no shortcut. Ctrl chords that are not ours are left alone, so Ctrl+Tab and Ctrl+W still belong
 * to the browser.
 * </para>
 */
const VIEW_KEYS = ['1', '2', '3', '4', '5', '6', '7'] as const

/** True when the keystroke is going into a field rather than into the app. */
function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable) return true
  const tag = target.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
}

function onShortcut(event: KeyboardEvent) {
  if (event.ctrlKey || event.metaKey) {
    const key = event.key.toLowerCase()

    if (key === 'k') {
      // Only when there is something to focus: the chord is left alone unless we act on it, which is also what
      // keeps Ctrl+Tab and Ctrl+W the browser's.
      if (typeof activeView.value?.focusSearch !== 'function') return
      // Deliberately not gated on `isTyping`: taking the reader away from whatever they were typing *is* what
      // "search" means.
      event.preventDefault()
      activeView.value.focusSearch()
      return
    }

    if (key === 'b') {
      event.preventDefault()
      userWantsSidebar.value = !userWantsSidebar.value
      return
    }

    // Any other chord belongs to the browser or the OS.
    return
  }

  if (event.altKey || isTyping(event.target)) return

  if (event.key === 'Escape') {
    // Views without an inspector (the queue, settings) are left alone rather than having the key eaten.
    if (typeof activeView.value?.collapseInspector !== 'function') return
    activeView.value.collapseInspector()
    return
  }

  const index = VIEW_KEYS.indexOf(event.key as (typeof VIEW_KEYS)[number])
  const item = index >= 0 ? NAV[index] : undefined
  if (item === undefined) return

  event.preventDefault()
  select(item.key)
}

function cycleTheme() {
  setTheme(nextTheme(theme.value))
}

function setTheme(next: Theme) {
  theme.value = next
  applyTheme(next)
}

/**
 * Waits until the API answers. The shell shows the window before the sidecar has finished starting, so the
 * first reads can race a port that is not listening yet. 'unauthorized' is terminal — the token is rejected
 * and retrying cannot help; 'unavailable' retries until the deadline.
 */
async function waitForApi(client: PartnerApi): Promise<'ok' | 'unauthorized' | 'unavailable'> {
  const deadline = Date.now() + 15_000
  for (;;) {
    const state = await client.health()
    if (state !== 'unavailable') {
      return state
    }
    if (Date.now() > deadline) {
      return 'unavailable'
    }
    await new Promise((resolve) => setTimeout(resolve, 250))
  }
}

onMounted(async () => {
  applyTheme(theme.value)
  applyReducedTransparency(readReducedTransparency())

  // On the window, not on the shell: a shortcut has to work wherever the caret is, including inside the
  // composer. `isTyping` keeps the digit keys out of a message.
  window.addEventListener('keydown', onShortcut)

  const resolved = resolveConnection()
  if (!resolved.ok) {
    connectionError.value = resolved.reason
    return
  }

  endpoint.value = `http://127.0.0.1:${resolved.connection.port}`
  const client = new PartnerApi(resolved.connection)

  // The window can appear before the API is listening. Show a connecting state, then hand the client to the
  // views only once it answers — a 401 is terminal, a still-down port retries until the deadline.
  connectionError.value = '连接中…'
  const up = await waitForApi(client)
  if (up !== 'ok') {
    connectionError.value =
      up === 'unauthorized'
        ? 'token 失效（401）——请重启壳以获取新令牌'
        : '无法连接到本地服务——请确认 Partner API 已启动'
    return
  }

  connectionError.value = ''
  api.value = client

  await Promise.all([refreshSummary(), refreshGears()])
  // Two cadences: the badge can wait five seconds, a parked turn cannot.
  poller = setInterval(() => {
    void refreshSummary()
  }, QUEUE_POLL_MS)

  approvalPoller = setInterval(() => {
    if (approvals.value.length > 0) {
      void refreshSummary()
    }
  }, APPROVAL_POLL_MS)
})

onUnmounted(() => {
  window.removeEventListener('keydown', onShortcut)

  if (poller !== undefined) {
    clearInterval(poller)
  }

  if (approvalPoller !== undefined) {
    clearInterval(approvalPoller)
  }
})
</script>

<template>
  <div class="app">
    <div class="body" :class="{ 'is-sidebar-collapsed': sidebarCollapsed }">
      <SideBar
        :items="NAV"
        :active="active"
        :collapsed="sidebarCollapsed"
        :toggle-hidden="railOnly"
        @select="select"
        @update:collapsed="userWantsSidebar = $event"
      />

      <div class="stage">
      <main class="content">
        <TopBar
          :view-label="viewLabel"
          :context="context"
          :summary="summary"
          :connected="connected"
          :connection-error="connectionError"
          :theme="theme"
          @cycle-theme="cycleTheme"
        />

        <template v-if="api">
          <SessionView
            v-if="active === 'session'"
            ref="activeView"
            :api="api"
            :summary="summary ?? undefined"
            :consumer="consumer ?? undefined"
            :approval="desktop?.enabled === true ? desktop.approval : null"
            :approvals="approvals"
            :desktop="desktop"
            :gears="gears"
            :default-gear="defaultGear"
            :queue-items="queueItems"
            @approval="setApprovalGear"
            @answer="answerApproval"
            @changed="refreshSummary"
          />
          <MemoryView
            v-else-if="active === 'memory'"
            ref="activeView"
            :api="api"
            @changed="refreshSummary"
          />
          <SchedulesView
            v-else-if="active === 'schedules'"
            ref="activeView"
            :api="api"
            @changed="refreshSummary"
          />
          <McpView
            v-else-if="active === 'mcp'"
            ref="activeView"
            :api="api"
            @changed="refreshSummary"
          />
          <SkillsView
            v-else-if="active === 'skills'"
            ref="activeView"
            :api="api"
            @changed="refreshSummary"
          />
          <PromptsView
            v-else-if="active === 'prompts'"
            ref="activeView"
            :api="api"
            @changed="refreshSummary"
            @navigate="select"
          />
          <SettingsView
            v-else
            ref="activeView"
            :api="api"
            :theme="theme"
            @set-theme="setTheme"
            @saved="refreshGears"
            @navigate="select"
          />
        </template>

        <p v-else class="error empty g-connect" data-test="connection-error">{{ connectionError }}</p>
      </main>

      <StatusBar
        :connected="connected"
        :connection-error="connectionError"
        :endpoint="endpoint"
        :consumer="consumer"
        :stale="stale"
        :version="`v${version}`"
        :job="runningJob"
        @cancel-job="cancelJob"
      />
      </div>
    </div>
    <ToastHost />
  </div>
</template>
