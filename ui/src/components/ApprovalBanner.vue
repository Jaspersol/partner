<script setup lang="ts">
import { computed } from 'vue'
import type { DesktopState, PendingApproval } from '../api'

/**
 * A tool call waiting for a human. It lives in the app shell rather than inside a view because the turn is
 * already parked: a prompt that only exists on one tab is a prompt nobody sees while they are elsewhere.
 */
const props = defineProps<{
  pending: PendingApproval[]
  desktop: DesktopState | null
}>()

const emit = defineEmits<{ answer: [approvalId: string, approved: boolean] }>()

const current = computed(() => props.pending[0] ?? null)

/** The window an approval is about, so the question is "allow this, on that window" rather than a raw id. */
const target = computed(() => {
  const args = current.value?.arguments ?? ''
  const id = /"entityId"\s*:\s*"([^"]+)"/.exec(args)?.[1]
  if (id === undefined) {
    return ''
  }

  const entity = props.desktop?.entities.find((candidate) => candidate.entityId === id)
  return entity ? `『${entity.title}』${entity.foreground ? '（当前前台）' : ''}` : `#${id}`
})

const kind = computed(() => /"kind"\s*:\s*"([^"]+)"/.exec(current.value?.arguments ?? '')?.[1] ?? '')
</script>

<template>
  <div v-if="current" class="approval" data-test="approval-banner">
    <span class="title">模型请求执行动作</span>
    <strong data-test="approval-tool">{{ current.toolName }}</strong>
    <span v-if="kind" class="kind">{{ kind }}</span>
    <span v-if="target" class="target" data-test="approval-target">目标 {{ target }}</span>
    <code class="break args">{{ current.arguments }}</code>
    <span class="spacer" />
    <span class="muted" data-test="approval-hint">无人应答即拒绝</span>
    <button class="primary" data-test="approval-allow" @click="emit('answer', current.approvalId, true)">
      允许
    </button>
    <button class="ghost" data-test="approval-deny" @click="emit('answer', current.approvalId, false)">
      拒绝
    </button>
  </div>
</template>

<style scoped>
.approval {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-3);
  border-block-end: 1px solid var(--warn);
  background: var(--surface-1);
}

.title {
  color: var(--warn);
  font-weight: 600;
}

.kind {
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
}

.target {
  color: var(--text);
}

.args {
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.spacer {
  flex: 1;
}
</style>
