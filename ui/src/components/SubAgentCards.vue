<script setup lang="ts">
import { computed } from 'vue'
import type { AgentJobView } from '../api'

/**
 * The sub-agents working right now, one small card each, above the box the reader types in.
 *
 * <para>
 * A card is the job's name and, on its right, how far it has got (round / most rounds). It exists only while the
 * job runs: the list is the running jobs and nothing else, so a finished job's card is gone on the next read and
 * the ones behind it close up into the space. Cards flow left to right and wrap, oldest first, so a card does not
 * jump to the other end when an earlier one leaves.
 * </para>
 */
const props = defineProps<{
  jobs: readonly AgentJobView[]
}>()

const running = computed(() =>
  props.jobs
    .filter((job) => job.status === 'running')
    .sort((a, b) => a.startedAt.localeCompare(b.startedAt) || a.id.localeCompare(b.id)),
)

function fraction(job: AgentJobView): number {
  return job.maxRounds > 0 ? Math.min(1, Math.max(0, job.round / job.maxRounds)) : 0
}
</script>

<template>
  <TransitionGroup v-if="running.length > 0" tag="ul" name="sa" class="sa-cards" aria-label="子 agent 进度" data-test="subagent-cards">
    <li
      v-for="job in running"
      :key="job.id"
      class="sa-card"
      :title="job.goal"
      :data-job="job.id"
      data-test="subagent-card"
    >
      <span class="sa-name">{{ job.agentName }}</span>
      <span class="sa-progress" data-test="subagent-progress">{{ job.round }}/{{ job.maxRounds }}</span>
      <span class="sa-bar" aria-hidden="true"><span class="sa-fill" :style="{ inlineSize: `${fraction(job) * 100}%` }" /></span>
    </li>
  </TransitionGroup>
</template>

<style scoped>
.sa-cards {
  position: relative;
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
}

.sa-card {
  position: relative;
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  min-inline-size: 9rem;
  max-inline-size: 14rem;
  min-block-size: 2rem;
  padding: 0 var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--glass-2);
  border: 1px solid var(--glass-edge);
  box-shadow: var(--glass-hi), var(--shadow-1);
  font-size: var(--fs-meta);
}

.sa-name {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text);
}

.sa-progress {
  flex: none;
  color: var(--accent-pressed);
  font-variant-numeric: tabular-nums;
}

.sa-bar {
  position: absolute;
  inset-inline: 0;
  inset-block-end: 0;
  block-size: 2px;
  background: transparent;
}

.sa-fill {
  display: block;
  block-size: 100%;
  background: var(--accent);
  transition: inline-size var(--motion-fast) var(--ease);
}

/* Closing: fade out of the flow, so the cards behind slide into the space instead of snapping. */
.sa-enter-active,
.sa-leave-active,
.sa-move {
  transition:
    opacity var(--motion-fast) var(--ease),
    transform var(--motion-fast) var(--ease);
}

.sa-enter-from,
.sa-leave-to {
  opacity: 0;
  transform: translateY(4px);
}

.sa-leave-active {
  position: absolute;
}

@media (prefers-reduced-motion: reduce) {
  .sa-enter-active,
  .sa-leave-active,
  .sa-move,
  .sa-fill {
    transition: none;
  }
}
</style>
