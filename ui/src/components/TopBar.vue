<script setup lang="ts">
import { computed, inject, onMounted, ref } from 'vue'
import { TOPBAR_TOOLS } from '../topbarTools'
import { hasWindowChrome, windowCommand } from '../windowChrome'
import Icon from './Icon.vue'
import type { IconName } from '../icons'
import type { QueueSummary } from '../api'
import { t } from '../i18n'
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
    return props.connected ? '' : t('topbar.disconnected')
  }

  if (summary.failed > 0) {
    return t('topbar.queueBlocked', { count: summary.failed })
  }

  if (summary.runningItemId !== null && summary.runningItemId !== '') {
    return t('topbar.running', { id: summary.runningItemId })
  }

  return summary.pending > 0 ? t('topbar.queuePending', { count: summary.pending }) : t('topbar.queueEmpty')
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

      <!-- Blocked work is visible without opening anything.

           The queue poll runs every five seconds whether or not anything changed, so putting `aria-live` on the
           badge itself would interrupt whatever the screen reader was reading on nearly every tick. The live
           region below therefore speaks only when the *shape* of the queue changes — blocked, running, cleared
           — and says nothing while the counts merely drift. Blocking is the one thing this app must never let
           a reader miss, and it is the one thing the eye is not always on. -->
      <span class="sr-only" role="status" aria-live="polite" data-test="blocked-announcer">{{ announcement }}</span>

      <!--
        Silent unless work is stuck. The depth and the running item's id used to sit here permanently; the queue is
        the composer's own list, and a standing 「队列 1 · 执行中 in-…」 is an internal id nobody can act on. A
        failure is the exception: it blocks everything behind it, and the eye is not always on the composer.
      -->
      <span v-if="summary && summary.failed > 0" class="badge badge-error" data-test="blocked">{{ t('topbar.failed', { count: summary.failed }) }}</span>
      <span v-if="!connected" class="badge badge-error" data-test="topbar-disconnected">{{ t('topbar.disconnected') }}</span>

      <button
        class="ghost icon-btn"
        data-test="theme-toggle"
        :aria-label="t('topbar.theme', { name: themeLabel(theme) })"
        :title="t('topbar.theme', { name: themeLabel(theme) })"
        @click="emit('cycle-theme')"
      >
        <Icon :name="THEME_ICON[theme]" :size="16" />
      </button>

      <!-- The native title bar is off (src-tauri), so the three window buttons are rendered here. They only
           exist inside the desktop shell: in a plain browser the browser has its own. -->
      <div v-if="hasWindowChrome()" class="row window-buttons" data-test="window-buttons">
        <button class="ghost icon-btn" :aria-label="t('topbar.minimize')" :title="t('topbar.minimize')" data-test="window-minimize" @click="windowCommand('minimize')">
          <Icon name="minus" :size="16" />
        </button>
        <button class="ghost icon-btn" :aria-label="t('topbar.maximize')" :title="t('topbar.maximize')" data-test="window-maximize" @click="windowCommand('toggle_maximize')">
          <Icon name="square" :size="14" />
        </button>
        <button class="ghost icon-btn is-close" :aria-label="t('topbar.close')" :title="t('topbar.close')" data-test="window-close" @click="windowCommand('close')">
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
