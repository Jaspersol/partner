<script setup lang="ts">
import { computed, inject, onMounted, ref } from 'vue'
import { TOPBAR_TOOLS } from '../topbarTools'
import { hasWindowChrome, windowCommand } from '../windowChrome'
import Icon from './Icon.vue'
import type { IconName } from '../icons'
import type { QueueSummary } from '../api'
import { themeLabel, type Theme } from '../theme'

/** The theme button's three faces. Named here rather than picked at the call site so the mapping is one list. */
const THEME_ICON: Record<Theme, IconName> = { auto: 'contrast', light: 'sun', dark: 'moon' }

const props = defineProps<{
  viewLabel: string
  context: string
  summary: QueueSummary | null
  connected: boolean
  connectionError: string
  theme: Theme
}>()

const emit = defineEmits<{
  'cycle-theme': []
}>()

/** Hands the page its slot once there is an element to teleport into. */
const tools = ref<HTMLElement | null>(null)
const slot = inject(TOPBAR_TOOLS, null)
onMounted(() => {
  if (slot !== null) slot.value = tools.value
})

/** Something worth a pill: work waiting, running, or failed. Idle is not worth one. */
const busy = computed(() => {
  const summary = props.summary
  return summary !== null && (summary.pending > 0 || summary.failed > 0 || (summary.runningItemId ?? '') !== '')
})

/**
 * What the screen reader is told, and when.
 *
 * <para>
 * Only the facts that change what a person would do, and each on its own: blocked, executing, waiting, clear.
 * The queue depth is deliberately absent. It moves on every enqueue and dequeue, so a live region carrying it
 * would re-announce several times a minute over a message that never asked for anything — and once the text
 * changes at all the screen reader reads the whole line again, dragging the depth along with it.
 * </para>
 *
 * <para>
 * The failure count *is* carried, because more failures is a different situation rather than the same one
 * louder, and the branches are checked in order so the most urgent thing is what gets said.
 * </para>
 */
const announcement = computed(() => {
  const summary = props.summary
  if (summary === null) {
    return props.connected ? '' : '未连接'
  }

  if (summary.failed > 0) {
    return `队列阻塞：失败 ${summary.failed} 项`
  }

  if (summary.runningItemId !== null && summary.runningItemId !== '') {
    return `正在执行 ${summary.runningItemId}`
  }

  return summary.pending > 0 ? `队列待处理 ${summary.pending} 项` : '队列为空'
})
</script>

<template>
  <header class="topbar" data-tauri-drag-region>
    <div class="row topbar-left" data-tauri-drag-region>
      <span class="brand-scope" data-tauri-drag-region>{{ viewLabel }}</span>
    </div>

    <div class="row breadcrumb" data-tauri-drag-region>
      <span class="muted break" data-tauri-drag-region>{{ context }}</span>
    </div>

    <div class="row topbar-right">
      <div ref="tools" class="row topbar-tools" data-test="topbar-tools" />

      <!-- Blocked work is visible without opening anything (ui.md §1, §3).

           The queue poll runs every five seconds whether or not anything changed, so putting `aria-live` on the
           badge itself would interrupt whatever the screen reader was reading on nearly every tick. The live
           region below therefore speaks only when the *shape* of the queue changes — blocked, running, cleared
           — and says nothing while the counts merely drift. Blocking is the one thing this app must never let
           a reader miss, and it is the one thing the eye is not always on. -->
      <span class="sr-only" role="status" aria-live="polite" data-test="blocked-announcer">{{ announcement }}</span>

      <!--
        A readout, not a link: the queue is the composer's own list. And only present when there is something to
        read — a standing 「队列 0」 is a sentence about nothing. An unknown queue (the read failed) still says so,
        because "—" is honest and silence would not be.
      -->
      <span v-if="summary && busy" class="badge" :class="{ 'badge-error': summary.failed > 0 }" data-test="blocked">
        队列 {{ summary.pending }}<template v-if="summary.failed > 0"> · 失败 {{ summary.failed }}</template
        ><template v-if="summary.runningItemId"> · 执行中 {{ summary.runningItemId }}</template>
      </span>
      <span v-else-if="!summary && connected" class="badge" data-test="blocked">队列 —</span>
      <span v-if="!connected" class="badge badge-error" data-test="topbar-disconnected">未连接</span>

      <button
        class="ghost icon-btn"
        data-test="theme-toggle"
        :aria-label="`主题：${themeLabel(theme)}`"
        :title="`主题：${themeLabel(theme)}`"
        @click="emit('cycle-theme')"
      >
        <Icon :name="THEME_ICON[theme]" :size="16" />
      </button>

      <!-- The native title bar is off (src-tauri), so the three buttons it used to carry live here. They only
           exist inside the desktop shell: in a plain browser the browser has its own. -->
      <div v-if="hasWindowChrome()" class="row window-buttons" data-test="window-buttons">
        <button class="ghost icon-btn" aria-label="最小化" title="最小化" data-test="window-minimize" @click="windowCommand('minimize')">
          <Icon name="minus" :size="16" />
        </button>
        <button class="ghost icon-btn" aria-label="最大化或还原" title="最大化或还原" data-test="window-maximize" @click="windowCommand('toggle_maximize')">
          <Icon name="square" :size="14" />
        </button>
        <button class="ghost icon-btn is-close" aria-label="关闭" title="关闭" data-test="window-close" @click="windowCommand('close')">
          <Icon name="x" :size="16" />
        </button>
      </div>
    </div>
  </header>
</template>

<style scoped>
/* A glass bar the page scrolls under. It is transparent enough that the page reads through it and blurred
   enough that the text underneath does not fight the title. */
.topbar {
  position: absolute;
  inset-block-start: 0;
  inset-inline: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: 0 var(--space-5);
  block-size: var(--topbar-h);
  background: color-mix(in srgb, var(--surface-0) 62%, transparent);
  -webkit-backdrop-filter: var(--glass-blur);
  backdrop-filter: var(--glass-blur);
}

.topbar-left,
.topbar-right {
  min-inline-size: 0;
}

.brand-scope {
  font-size: 1rem;
  font-weight: 600;
  white-space: nowrap;
}

.breadcrumb {
  flex: 1;
  justify-content: center;
  min-inline-size: 0;
}

.icon-btn {
  min-block-size: 2.25rem;
  inline-size: 2.25rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.window-buttons {
  gap: 2px;
  margin-inline-start: var(--space-2);
}

.window-buttons .is-close:hover {
  background: var(--error);
  color: var(--on-accent);
}

.badge {
  padding: var(--space-1) var(--space-3);
  border-radius: var(--radius-pill);
  background: var(--surface-2);
  color: var(--text-2);
  font-size: var(--fs-meta);
  white-space: nowrap;
}

.badge-error {
  color: var(--error);
  background: color-mix(in srgb, var(--error) 10%, transparent);
}

@container shell (max-width: 40rem) {
  .breadcrumb {
    display: none;
  }
}
</style>
