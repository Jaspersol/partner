<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue'
import {
  ApiError,
  PartnerApi,
  summarizeQueue,
  type PartnerApiSurface,
  type ConsumerState,
  type QueueSummary,
  type RebuildReport,
  type PendingApproval,
  type DesktopState,
} from './api'
import { resolveConnection } from './config'
import { applyTheme, nextTheme, readTheme, type Theme } from './theme'
import SideBar, { type NavItem } from './components/SideBar.vue'
import StatusBar from './components/StatusBar.vue'
import ApprovalBanner from './components/ApprovalBanner.vue'
import TopBar from './components/TopBar.vue'
import MemoryView from './views/MemoryView.vue'
import QueueView from './views/QueueView.vue'
import SessionView from './views/SessionView.vue'
import SettingsView from './views/SettingsView.vue'
import { version } from '../package.json'

/**
 * The shell (ui.md §2): top bar, body (sidebar + content), status bar. It owns exactly what the regions
 * share — the connection, the queue summary, the active view — and nothing else. Each view reads its own
 * data; the shell never caches a view's contents.
 */

const NAV: NavItem[] = [
  { key: 'session', label: '会话', icon: '◈' },
  { key: 'queue', label: '队列', icon: '☰' },
  { key: 'memory', label: '记忆', icon: '⌗' },
  { key: 'settings', label: '设置', icon: '⚙' },
]

/** How often the badge re-reads the queue: blocked work should not need a click to appear (ui.md §10 G6). */
const QUEUE_POLL_MS = 5000

/**
 * A tool call waiting for a human, shown here rather than inside a view: the answer is part of a turn that is
 * already parked, and a prompt that only exists on the session tab is a prompt nobody sees while they are
 * elsewhere — which is how two approvals once timed out with nobody watching.
 */
const APPROVAL_POLL_MS = 500

const approvals = ref<PendingApproval[]>([])
const desktop = ref<DesktopState | null>(null)

const active = ref('session')
const sidebarCollapsed = ref(false)
const inspectorCollapsed = ref(false)
const summary = ref<QueueSummary | null>(null)
const consumer = ref<ConsumerState | null>(null)
const rebuildReport = ref<RebuildReport | null>(null)
const context = ref('')
const connectionError = ref('')
const endpoint = ref('')
const theme = ref<Theme>(readTheme())
const api = shallowRef<PartnerApiSurface | null>(null)
const sessionView = ref<InstanceType<typeof SessionView> | null>(null)

const connected = computed(() => api.value !== null)
const viewLabel = computed(() => NAV.find((item) => item.key === active.value)?.label ?? '')

let poller: ReturnType<typeof setInterval> | undefined
let approvalPoller: ReturnType<typeof setInterval> | undefined

async function refreshSummary() {
  if (!api.value) {
    return
  }

  try {
    summary.value = summarizeQueue(await api.value.queue())
    consumer.value = await api.value.consumer()
    approvals.value = (await api.value.approvals()).pending

    // Only fetched while something is waiting: it is what turns an entityId into a window a human recognises.
    desktop.value = approvals.value.length > 0 ? await api.value.desktop() : null
  } catch (failure) {
    // A stale badge is worse than none: it would claim the queue is empty when we simply do not know.
    summary.value = null
    consumer.value = null
    if (failure instanceof ApiError && failure.status === 401) {
      connectionError.value = 'token 失效（401）——请重启壳以获取新令牌'
    }
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

async function rebuildProjections() {
  if (!api.value) {
    return
  }

  try {
    rebuildReport.value = await api.value.rebuild()
    await refreshSummary()
  } catch (failure) {
    connectionError.value = String(failure)
  }
}

function select(key: string) {
  active.value = key
  context.value = ''
  // The inspector belongs to master-detail views; switching views starts with it open.
  inspectorCollapsed.value = false
}

function startNewQuery() {
  select('session')
  sessionView.value?.focusComposer()
}

function cycleTheme() {
  theme.value = nextTheme(theme.value)
  applyTheme(theme.value)
}

onMounted(async () => {
  applyTheme(theme.value)

  const resolved = resolveConnection()
  if (!resolved.ok) {
    connectionError.value = resolved.reason
    return
  }

  endpoint.value = `http://127.0.0.1:${resolved.connection.port}`
  api.value = new PartnerApi(resolved.connection)

  await refreshSummary()
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
      @update:collapsed="sidebarCollapsed = $event"
      @cycle-theme="cycleTheme"
    />

    <ApprovalBanner
      :pending="approvals"
      :desktop="desktop"
      @answer="answerApproval"
    />

    <div class="body" :class="{ 'is-sidebar-collapsed': sidebarCollapsed }">
      <SideBar
        :items="NAV"
        :active="active"
        :collapsed="sidebarCollapsed"
        :rebuild="rebuildReport"
        @select="select"
        @new-query="startNewQuery"
        @rebuild="rebuildProjections"
      />

      <main class="content">
        <template v-if="api">
          <SessionView
            v-if="active === 'session'"
            ref="sessionView"
            :api="api"
            :summary="summary ?? undefined"
            :consumer="consumer ?? undefined"
            @context="context = $event"
            @changed="refreshSummary"
          />
          <QueueView
            v-else-if="active === 'queue'"
            :api="api"
            @context="context = $event"
            @changed="refreshSummary"
          />
          <MemoryView
            v-else-if="active === 'memory'"
            :api="api"
            @context="context = $event"
            @changed="refreshSummary"
          />
          <SettingsView v-else :api="api" @context="context = $event" />
        </template>

        <p v-else class="error" data-test="connection-error" style="padding: var(--space-4)">
          {{ connectionError }}
        </p>
      </main>
    </div>

    <StatusBar
      :connected="connected"
      :connection-error="connectionError"
      :endpoint="endpoint"
      :summary="summary"
      :consumer="consumer"
      :version="`v${version}`"
    />
  </div>
</template>
