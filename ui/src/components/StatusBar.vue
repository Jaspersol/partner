<script setup lang="ts">
import { computed } from 'vue'
import type { ConsumerState } from '../api'

const props = defineProps<{
  connected: boolean
  connectionError: string
  endpoint: string
  consumer: ConsumerState | null
  version: string
  /** A refresh failed and nothing has succeeded since: what is on screen is the last good read. */
  stale?: boolean | undefined
}>()

/**
 * What the screen reader is told about the connection, and nothing else.
 *
 * <para>
 * Only the dead case speaks. While connected this is empty, so a re-render — and there are many, the shell
 * refreshes the consumer state on every poll — has nothing to announce. Losing the endpoint is the failure that
 * makes every other control on the screen a lie, which is why it is worth the interruption; the queue badge in
 * the top bar already covers the other status worth speaking about.
 * </para>
 */
const announcement = computed(() => (props.connected ? '' : `连接已断开：${props.connectionError}`))
</script>

<template>
  <footer class="statusbar">
    <span data-test="status" :class="{ error: !connected }">
      <!-- 401 is worth naming: it means the token, not the network (ui.md §7). -->
      {{ connected ? `ok · ${endpoint}` : connectionError }}
    </span>
    <!-- The status above is visible but not announced: it is a quiet line of text at the foot of the window,
         and re-rendering it on every poll would make a screen reader talk over whatever is being read. This
         carries the same fact only when it changes into the state that matters. -->
    <span class="sr-only" role="status" aria-live="polite" data-test="status-announcer">{{ announcement }}</span>
    <span class="consumer" data-test="status-consumer">
      <template v-if="consumer?.running">消费中 · {{ consumer.model ?? "模型" }}</template>
      <template v-else-if="consumer">未执行 · {{ consumer.reason }}</template>
    </span>

    <!--
      The status bar is where connection state belongs, so this is where "what you are reading is the last
      thing we managed to read" goes. Everything above it — the badge, the queue list, the interrupt button —
      keeps working on that last good read rather than being wiped, because a blocked-work indicator that
      blinks out on one failed poll is not a blocked-work indicator.
    -->
    <span v-if="stale" class="stale" data-test="status-stale">读取失败 · 以下是上次读到的</span>

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
  background: var(--surface-1);
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.consumer {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.stale {
  color: var(--warn);
  white-space: nowrap;
}
</style>
