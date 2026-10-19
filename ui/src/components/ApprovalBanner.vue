<script setup lang="ts">
import { computed } from 'vue'
import type { DesktopState, PendingApproval } from '../api'
import HintTip from './HintTip.vue'
import Icon from './Icon.vue'

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

/**
 * Grant's size comes from the server — it is the server's semantics, not the banner's. A null desktop
 * state (no desktop observation yet) falls back to what the store does by default.
 */
const grantSteps = computed(() => props.desktop?.stepsPerGrant ?? 10)
</script>

<template>
  <div v-if="current" class="approval g-float" data-test="approval-banner" role="alert">
    <div class="approval-body">
      <div class="approval-head">
        <Icon name="shield" :size="16" class="approval-icon" />
        <span class="title">模型请求执行动作</span>
        <!--
          Whose question this is. A sub-agent's approval arrives while the person may be looking at something
          else entirely; without the origin it reads as the main agent acting, which is a different thing to allow.
        -->
        <span v-if="current.origin" class="origin" data-test="approval-origin">来自 {{ current.origin }}</span>
      </div>
      <div class="approval-what">
        <strong data-test="approval-tool">{{ current.toolName }}</strong>
        <span v-if="kind" class="kind">{{ kind }}</span>
        <span v-if="target" class="target" data-test="approval-target">目标 {{ target }}</span>
      </div>
      <code class="break args">{{ current.arguments }}</code>
    </div>
    <div class="approval-actions">
      <HintTip align="end" data-test="approval-hint">
        无人应答即拒绝。允许后，助手可以在这个窗口上继续操作 {{ grantSteps }} 步；危险类的动作仍然逐次询问。
      </HintTip>
      <button class="ghost" data-test="approval-deny" @click="emit('answer', current.approvalId, false)">拒绝</button>
      <button class="primary" data-test="approval-allow" @click="emit('answer', current.approvalId, true)">允许</button>
    </div>
  </div>
</template>

<style scoped>
/* A warning is a glass card with an amber edge, not a full-width stripe: it has to be unmissable and it has to
   sit inside the same column as the dock it interrupts. */
.approval {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-3) var(--space-4) var(--space-3) var(--space-5);
  border-color: color-mix(in srgb, var(--warn) 55%, transparent);
}

.approval-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-1);
  min-inline-size: 0;
}

.approval-head,
.approval-what {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--space-2);
}

.approval-icon {
  align-self: center;
  color: var(--warn);
}

.title {
  color: var(--warn);
  font-weight: 600;
}

/* The asker, named before the tool: "who is asking" changes the answer more than "which tool" does. */
.origin {
  font-weight: 600;
  color: var(--text);
}

.kind {
  font-family: var(--font-mono);
  font-size: var(--fs-meta);
  color: var(--text-2);
}

.target {
  color: var(--text);
}

.args {
  color: var(--text-3);
  font-size: var(--fs-meta);
  max-block-size: 3rem;
  overflow: hidden;
}

.approval-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex: none;
}
</style>
