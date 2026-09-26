<script setup lang="ts">
import type { ConsumerState, QueueSummary } from '../api'

defineProps<{
  connected: boolean
  connectionError: string
  endpoint: string
  summary: QueueSummary | null
  consumer: ConsumerState | null
  version: string
}>()
</script>

<template>
  <footer class="statusbar">
    <span data-test="status" :class="{ error: !connected }">
      <!-- 401 is worth naming: it means the token, not the network (ui.md §7). -->
      {{ connected ? `ok · ${endpoint}` : connectionError }}
    </span>
    <span class="consumer" data-test="status-consumer">
      <template v-if="consumer?.running">消费中 · {{ consumer.model ?? "模型" }}</template>
      <template v-else-if="consumer">未执行 · {{ consumer.reason }}</template>
    </span>
    <span v-if="summary" class="blocked" data-test="status-blocked">
      队列 {{ summary.pending }} · 失败 {{ summary.failed
      }}<template v-if="summary.runningItemId"> · 执行中 {{ summary.runningItemId }}</template>
    </span>
    <span class="muted">{{ version }}</span>
  </footer>
</template>

<style scoped>
.statusbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-1) var(--space-3);
  border-block-start: 1px solid var(--line);
  background: var(--pane);
  font-size: var(--fs-meta);
  color: var(--muted);
}

.blocked {
  font-weight: 600;
}

.consumer {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
