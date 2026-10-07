<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue'
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
} from './api'
import { resolveConnection } from './config'
import { useRailOnlyMode } from './narrow'
import { applyTheme, nextTheme, readTheme, type Theme } from './theme'
import SideBar, { type NavItem } from './components/SideBar.vue'
import StatusBar from './components/StatusBar.vue'
import TopBar from './components/TopBar.vue'
import MemoryView from './views/MemoryView.vue'
import SessionView from './views/SessionView.vue'
import McpView from './views/McpView.vue'
import SkillsView from './views/SkillsView.vue'
import SettingsView from './views/SettingsView.vue'
import { version } from '../package.json'

/**
 * The shell (ui.md §2): top bar, body (sidebar + content), status bar. It owns exactly what the regions
 * share — the connection, the queue summary, the active view — and nothing else. Each view reads its own
 * data; the shell never caches a view's contents.
 */

const NAV: NavItem[] = [
  { key: 'session', label: '会话', icon: 'chat' },
  { key: 'memory', label: '记忆', icon: 'memory' },

  // Two capability pages of their own rather than cards inside settings: both are lists a person reads and
  // changes, not two more switches. A server row or a skill row deserves the room a card in a settings form
  // does not give it.
  { key: 'mcp', label: 'MCP', icon: 'mcp' },
  { key: 'skills', label: '技能', icon: 'skill' },
  { key: 'settings', label: '设置', icon: 'settings' },
]

/** How often the badge re-reads the queue: blocked work should not need a click to appear. */
const QUEUE_POLL_MS = 5000

/**
 * A tool call waiting for a human, shown here rather than inside a view: the answer is part of a turn that is
 * already parked, and a prompt that only exists on the session tab is a prompt nobody sees while they are
 * elsewhere — which is how two approvals once timed out with nobody watching.
 */
const APPROVAL_POLL_MS = 500

const approvals = ref<PendingApproval[]>([])
const desktop = ref<DesktopState | null>(null)


/**
 * The gear menu and its default, for the composer's picker. Read from settings rather than fetched with the
 * summary: they only change when settings are saved, and a poll that re-read them would be asking the
 * server the same question every few seconds.
 */
const gears = ref<string[]>([])
const defaultGear = ref('')
const active = ref('session')
/**
 * What the reader chose, not what is true.
 *
 * <para>
 * These are kept apart because the difference is the whole bug. For years the CSS collapsed the sidebar on a
 * narrow window while this flag stayed `false`, so the toggle button went on reading 「收起侧栏」 and clicking it
 * changed nothing that could be seen: the state it reported and the state on screen had quietly diverged.
 * Now `railOnly` comes from a measurement, the effective state is derived from both, and the button is not
 * offered at all when the window is too narrow for it to mean anything.
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
 * One read, two readings — the counts the badge shows and the rows the composer's expandable list lists —
 * so they cannot disagree. There is no queue page any more: the conversation is where a reader meets the
 * queue, in the composer's own strip.
 * </para>
 */
const queueItems = ref<QueueItem[]>([])
const consumer = ref<ConsumerState | null>(null)
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
 * The views are \`v-if\`, so leaving one destroys it. That was invisible while settings saved themselves on every
 * toggle; now that saving is explicit (P4 #20), switching tabs would silently throw away a page of edits. A
 * reader who did not expect to be asked is better served by a question than by losing the work — so the view is
 * asked, and only asked when the answer is yes.
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
 * <para>
 * This exists because a failed read has to change the answer without erasing it, and those are different
 * demands that were being served by one line.
 * </para>
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

    // Always fetched: it carries the approval gear the composer offers, and the entities that turn an
    // approval's entityId into a window a human recognises. Both are read-only and in-memory.
    desktop.value = await api.value.desktop()
    stale.value = false
  } catch (failure) {
    // Two different things, and they used to be handled by the same statement:
    //
    //   - **A count we no longer trust must not be shown.** `summary = null` makes the badge read 队列 —,
    //     which is honest: we do not know. Clearing it here is right, and the comment that used to sit on
    //     this line is kept for the half of it that still holds.
    //   - **An action must not disappear because a read failed.** The composer's interrupt button and its queue
    //     list are derived from `queueItems` / `consumer`, so clearing those made the *stop* button vanish
    //     mid-turn — on any transient hiccup, and with no way to get it back but waiting for the next poll.
    //     A control that disappears while the thing it controls is still running is worse than one showing
    //     something slightly old.
    //
    // So: the last known queue and consumer state are kept, and `stale` says so on the surface that is about
    // connection state. Pressing interrupt on a stale read is harmless — `cancel` answers 404 and the composer
    // reports 「已完成，无法取消」.
    if (failure instanceof ApiError && failure.status === 401) {
      connectionError.value = 'token 失效（401）——请重启壳以获取新令牌'
    }

    stale.value = summary.value !== null
  }
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
 * Re-reads the gear menu after a settings save, so the composer offers what was just stored. A failed
 * read keeps the last known list: emptying the picker would silently drop a gear the user can no longer
 * select, which is worse than showing a slightly stale one.
 */
async function refreshGears() {
  if (!api.value) {
    return
  }

  try {
    const settings = await api.value.settings()
    gears.value = settings.model.reasoningGears ?? []
    defaultGear.value = settings.model.reasoningEffort ?? ''
  } catch (failure) {
    connectionError.value = String(failure)
  }
}

/**
 * Keyboard shortcuts (P4 #19).
 *
 * <para>
 * Three, and each one has to be understood as *the* way to do something rather than a faster way: Ctrl+K to
 * search, `1`–`6` to switch view, Ctrl+B for the rail. The arrow keys already proved this app can be driven
 * from the keyboard; these are the three that were missing to finish the set.
 * </para>
 *
 * <para>
 * The rule that matters is what they must <b>not</b> steal. `1`–`6` are the digits a person types into the
 * composer, so they are inert while the caret is in a field — a shortcut that swallows half the characters of
 * a message is worse than no shortcut at all. Ctrl chords are left alone entirely unless they are one of ours,
 * so Ctrl+Tab and Ctrl+W still belong to the browser.
 * </para>
 */
const VIEW_KEYS = ['1', '2', '3', '4', '5', '6'] as const

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
      // Only when there is something to focus. Preventing the default for a chord we then do nothing with
      // would be the same lie as a button that does nothing: the chord is left alone unless we act on it,
      // which is also what keeps Ctrl+Tab and Ctrl+W the browser's.
      if (typeof activeView.value?.focusSearch !== 'function') return
      // Deliberately not gated on `isTyping`: taking the reader away from whatever they were typing *is* what
      // "search" means, and it is how every application that offers Ctrl+K behaves.
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
  theme.value = nextTheme(theme.value)
  applyTheme(theme.value)
}

onMounted(async () => {
  applyTheme(theme.value)

  // On the window, not on the shell: a shortcut has to work wherever the caret is, including inside the
  // composer. `isTyping` is what keeps the digit keys out of a message.
  window.addEventListener('keydown', onShortcut)

  const resolved = resolveConnection()
  if (!resolved.ok) {
    connectionError.value = resolved.reason
    return
  }

  endpoint.value = `http://127.0.0.1:${resolved.connection.port}`
  api.value = new PartnerApi(resolved.connection)

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
    <TopBar
      :view-label="viewLabel"
      :context="context"
      :summary="summary"
      :collapsed="sidebarCollapsed"
      :connected="connected"
      :connection-error="connectionError"
      :theme="theme"
      :toggle-hidden="railOnly"
      @update:collapsed="userWantsSidebar = $event"
      @cycle-theme="cycleTheme"
    />

    <div class="body" :class="{ 'is-sidebar-collapsed': sidebarCollapsed }">
      <SideBar
        :items="NAV"
        :active="active"
        :collapsed="sidebarCollapsed"
        @select="select"
      />

      <main class="content">
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
            @context="context = $event"
            @approval="setApprovalGear"
            @answer="answerApproval"
            @changed="refreshSummary"
          />
          <MemoryView
            v-else-if="active === 'memory'"
            ref="activeView"
            :api="api"
            @context="context = $event"
            @changed="refreshSummary"
          />
          <McpView
            v-else-if="active === 'mcp'"
            ref="activeView"
            :api="api"
            @context="context = $event"
            @changed="refreshSummary"
          />
          <SkillsView
            v-else-if="active === 'skills'"
            ref="activeView"
            :api="api"
            @context="context = $event"
            @changed="refreshSummary"
          />
          <SettingsView v-else ref="activeView" :api="api" @context="context = $event" @saved="refreshGears" />
        </template>

        <p v-else class="error empty" data-test="connection-error">{{ connectionError }}</p>
      </main>
    </div>

    <StatusBar
      :connected="connected"
      :connection-error="connectionError"
      :endpoint="endpoint"
      :consumer="consumer"
      :stale="stale"
      :version="`v${version}`"
    />
  </div>
</template>
