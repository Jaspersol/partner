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
  <!--
    Silent while everything works. The endpoint, the model and the version used to sit here permanently; the
    model now lives in the composer and the rest was a sentence about nothing. What remains is what a reader has
    to act on or be warned about: no connection, a stale read, a delegated job they can stop.
  -->
  <footer v-if="!connected || stale || job" class="statusbar">
    <!-- 401 is worth naming: it means the token, not the network. -->
    <span v-if="!connected" class="error" data-test="status">{{ connectionError }}</span>
    <span class="sr-only" role="status" aria-live="polite" data-test="status-announcer">{{ announcement }}</span>

    <span v-if="job" class="job" data-test="status-job">
      子任务进行中 · {{ job.agentName }} · 第 {{ job.round }}/{{ job.maxRounds }} 轮
      <button class="ghost" data-test="status-job-cancel" @click="emit('cancel-job', job.id)">停止</button>
    </span>

    <span v-if="stale" class="stale" data-test="status-stale">读取失败 · 以下是上次读到的</span>
  </footer>
</template>

<style scoped>
/* Not a bar: a line of small print under the page. No fill, no rule — the connection, the model and a running job
   are things to glance at, and the page above them is the point. */
.statusbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-1) var(--space-5) var(--space-2);
  min-block-size: 1.75rem;
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.statusbar > span {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.consumer {
  flex: 1;
}

.stale {
  color: var(--warn);
  white-space: nowrap;
}

/* The job line is live work, so it is not muted: it has to be findable. A glass pill, because it is the one
   thing down here that asks for a click. */
.job {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  padding: 0 var(--space-1) 0 var(--space-3);
  border-radius: var(--radius-pill);
  background: var(--accent-soft);
  color: var(--accent-pressed);
  white-space: nowrap;
}

.job button {
  min-block-size: 1.5rem;
  font-size: var(--fs-meta);
  padding: 0 var(--space-2);
  border-radius: var(--radius-pill);
}
</style>
