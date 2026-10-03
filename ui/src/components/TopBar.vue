<script setup lang="ts">
import { computed } from 'vue'
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
  collapsed: boolean
  connected: boolean
  connectionError: string
  theme: Theme
  /**
   * The window is too narrow for the sidebar to open at all, so the toggle is not offered.
   *
   * <para>
   * The alternative — showing it and doing nothing — is the defect this exists to end. A control that cannot
   * act is worse than an absent one: it promises a thing the window will not allow.
   * </para>
   */
  toggleHidden?: boolean | undefined
}>()

const emit = defineEmits<{
  'update:collapsed': [value: boolean]
  'cycle-theme': []
}>()

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
  <header class="topbar">
    <div class="row">
      <button
        v-if="toggleHidden !== true"
        class="ghost"
        data-test="sidebar-toggle"
        :aria-label="collapsed ? '展开侧栏' : '收起侧栏'"
        :title="`${collapsed ? '展开侧栏' : '收起侧栏'}（Ctrl+B）`"
        @click="emit('update:collapsed', !collapsed)"
      >
        <Icon name="panel-left" :size="16" />
      </button>
      <strong>Partner</strong>
      <span class="muted brand-scope">· {{ viewLabel }}</span>
    </div>

    <div class="row breadcrumb">
      <span class="muted break">{{ context }}</span>
    </div>

    <div class="row">
      <button
        class="ghost"
        data-test="theme-toggle"
        :aria-label="`主题：${themeLabel(theme)}`"
        :title="`主题：${themeLabel(theme)}`"
        @click="emit('cycle-theme')"
      >
        <Icon :name="THEME_ICON[theme]" :size="16" />
      </button>

      <!-- Blocked work is visible without opening the queue view (ui.md §1, §3).

           The queue poll runs every five seconds whether or not anything changed, so putting `aria-live` on the
           badge itself would interrupt whatever the screen reader was reading on nearly every tick. The live
           region below therefore speaks only when the *shape* of the queue changes — blocked, running, cleared
           — and says nothing while the counts merely drift. Blocking is the one thing this app must never let
           a reader miss, and it is the one thing the eye is not always on. -->
      <span class="sr-only" role="status" aria-live="polite" data-test="blocked-announcer">{{ announcement }}</span>

      <!--
        A readout, not a link. It was a button once, pointing at a queue page; that page is gone, because the
        queue is now the composer's own list — the place a reader is already standing when a turn starts.

        So it no longer pretends to lead anywhere, and the reasoning behind that is the same one that put a
        sidebar toggle behind a width threshold: a control that cannot act is worse than an absent one. What
        still has to be acted on is one click away in the strip below it, and the announced form still says
        out loud when work is blocked.
      -->
      <span v-if="summary" class="badge" :class="{ 'badge-error': summary.failed > 0 }" data-test="blocked">
        队列 {{ summary.pending }}<template v-if="summary.failed > 0"> · 失败 {{ summary.failed }}</template
        ><template v-if="summary.runningItemId"> · 执行中 {{ summary.runningItemId }}</template>
      </span>
      <span v-else-if="connected" class="badge" data-test="blocked">队列 —</span>
      <span v-if="!connected" class="badge badge-error" data-test="topbar-disconnected">未连接</span>
    </div>
  </header>
</template>

<style scoped>
.topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-2) var(--space-3);
  /* 48px, not 44: the rail below it is 56, and a 44px bar over a 56px rail reads as two unrelated
     heights. The bar is now the only row in the shell with a number rather than a content-derived size, so it
     is the one place worth spending a literal on. */
  min-block-size: 3rem;
  border-block-end: 1px solid var(--line);
  background: var(--surface-1);
}

.topbar > .row:first-child {
  min-inline-size: 0;
}

.breadcrumb {
  flex: 1;
  justify-content: center;
  min-inline-size: 0;
}

.badge {
  padding: var(--space-1) var(--space-2);
  border: 1px solid var(--line);
  border-radius: var(--radius-lg);
  font-size: var(--fs-meta);
  white-space: nowrap;
}

.badge-error {
  border-color: var(--error);
  color: var(--error);
  background: color-mix(in srgb, var(--error) 10%, transparent);
}
</style>
