<script setup lang="ts">
import type { QueueSummary } from '../api'
import { themeLabel, type Theme } from '../theme'

defineProps<{
  viewLabel: string
  context: string
  summary: QueueSummary | null
  collapsed: boolean
  connected: boolean
  connectionError: string
  theme: Theme
}>()

const emit = defineEmits<{
  'update:collapsed': [value: boolean]
  'cycle-theme': []
}>()
</script>

<template>
  <header class="topbar">
    <div class="row">
      <button
        class="ghost"
        data-test="sidebar-toggle"
        :title="collapsed ? '展开侧栏' : '收起侧栏'"
        @click="emit('update:collapsed', !collapsed)"
      >
        ◧
      </button>
      <strong>Partner</strong>
      <span class="muted brand-scope">· {{ viewLabel }}</span>
    </div>

    <div class="row breadcrumb">
      <span class="muted break">{{ context }}</span>
    </div>

    <div class="row">
      <button class="ghost" data-test="theme-toggle" :title="`主题：${themeLabel(theme)}`" @click="emit('cycle-theme')">
        {{ theme === 'dark' ? '☾' : theme === 'light' ? '☀' : '◐' }}
      </button>

      <!-- Blocked work is visible without opening the queue view (ui.md §1, §3). -->
      <span v-if="summary" class="badge" :class="{ 'badge-error': summary.failed > 0 }" data-test="blocked">
        队列 {{ summary.pending }} · 失败 {{ summary.failed
        }}<template v-if="summary.runningItemId"> · 执行中 {{ summary.runningItemId }}</template>
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
  min-block-size: 2.75rem;
  border-block-end: 1px solid var(--line);
  background: var(--pane);
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
}
</style>
