<script setup lang="ts">
import { computed } from 'vue'
import type { AgentJobView, ConsumerState } from '../api'

const props = defineProps<{
  connected: boolean
  connectionError: string
  endpoint: string
  consumer: ConsumerState | null
  version: string
  /** A refresh failed and nothing has succeeded since: what is on screen is the last good read. */
  stale?: boolean | undefined
  /** The delegated job in flight, or null when nothing is delegated. */
  job?: AgentJobView | null | undefined
}>()

const emit = defineEmits<{ 'cancel-job': [jobId: string] }>()

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
      A delegated job is work happening that the main turn may be waiting on. It is not the queue, so it does
      not live in the badge, and it is not the consumer, so it does not replace the model line. One line, and
      a stop button: a job a reader cannot see is a job they cannot cancel. Nothing renders when idle.
    -->
    <span v-if="job" class="job" data-test="status-job">
      子任务进行中 · {{ job.agentName }} · 第 {{ job.round }}/{{ job.maxRounds }} 轮
      <button class="ghost" data-test="status-job-cancel" @click="emit('cancel-job', job.id)">停止</button>
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

/* The job line is live work, so it is not muted: it has to be findable at the foot of the window. */
.job {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  color: var(--accent-pressed);
  white-space: nowrap;
}

.job button {
  font-size: var(--fs-meta);
  padding: 0 var(--space-1);
}
</style>
