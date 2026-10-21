<script setup lang="ts">
import { t } from '../i18n'
import { computed } from 'vue'
import type { DesktopState, PendingApproval } from '../api'
import HintTip from './HintTip.vue'
import Icon from './Icon.vue'

/** A tool call waiting for a human. Lives in the app shell so the prompt stays visible on every view. */
const props = defineProps<{
  pending: PendingApproval[]
  desktop: DesktopState | null
}>()

const emit = defineEmits<{ answer: [approvalId: string, approved: boolean] }>()

const current = computed(() => props.pending[0] ?? null)

const target = computed(() => {
  const args = current.value?.arguments ?? ''
  const id = /"entityId"\s*:\s*"([^"]+)"/.exec(args)?.[1]
  if (id === undefined) {
    return ''
  }

  const entity = props.desktop?.entities.find((candidate) => candidate.entityId === id)
  return entity ? t('『{title}』{state}', { title: entity.title, state: entity.foreground ? t('（当前前台）') : '' }) : `#${id}`
})

const schedule = computed(() => {
  const name = current.value?.toolName ?? ''
  if (name !== 'schedule_create' && name !== 'schedule_delete') {
    return null
  }

  let args: Record<string, unknown> = {}
  try {
    args = JSON.parse(current.value?.arguments ?? '{}') as Record<string, unknown>
  } catch {
    // Unreadable arguments are still shown and still asked about.
  }

  if (name === 'schedule_delete') {
    return { verb: 'delete' as const, rule: '', message: '', id: String(args.id ?? '') }
  }

  const minutes = (value: unknown) => {
    const count = Number(value)
    if (!Number.isFinite(count) || count < 1) return t('？')
    if (count % 1440 === 0) return t('{v} 天', { v: count / 1440 })
    if (count % 60 === 0) return t('{v} 小时', { v: count / 60 })
    return t('{count} 分钟', { count: count })
  }

  const rule =
    args.kind === 'cron'
      ? t('按时刻 {cron}', { cron: String(args.cron ?? '？') })
      : args.kind === 'interval'
        ? t('每 {minutes}', { minutes: minutes(args.intervalMinutes) })
        : args.delayMinutes !== undefined
          ? t('{minutes}后，只一次', { minutes: minutes(args.delayMinutes) })
          : t('{at}，只一次', { at: String(args.at ?? '？') })

  return { verb: 'create' as const, rule, message: String(args.message ?? ''), id: '' }
})

const kind = computed(() =>
  schedule.value ? '' : (/"kind"\s*:\s*"([^"]+)"/.exec(current.value?.arguments ?? '')?.[1] ?? ''),
)

/** Grant size is the server's semantics; 10 matches the store default when no desktop state exists yet. */
const grantSteps = computed(() => props.desktop?.stepsPerGrant ?? 10)
</script>

<template>
  <div v-if="current" class="approval g-float" data-test="approval-banner" role="alert">
    <div class="approval-body">
      <div class="approval-head">
        <Icon name="shield" :size="16" class="approval-icon" />
        <span class="title">{{ schedule ? (schedule.verb === 'create' ? t('模型请求创建定时任务') : t('模型请求删除定时任务')) : t('模型请求执行动作') }}</span>
        <span v-if="current.origin" class="origin" data-test="approval-origin">{{ t('来自 {origin}', { origin: current.origin }) }}</span>
      </div>
      <div class="approval-what">
        <strong data-test="approval-tool">{{ current.toolName }}</strong>
        <span v-if="kind" class="kind">{{ kind }}</span>
        <span v-if="target" class="target" data-test="approval-target">{{ t('目标 {target}', { target: target }) }}</span>
      </div>
      <p v-if="schedule?.verb === 'create'" class="schedule-what break" data-test="approval-schedule">
        <span class="schedule-rule">{{ schedule.rule }}</span>
        {{ t('到点会把这句话当作你发的消息交给助手：') }}<q>{{ schedule.message }}</q>
      </p>
      <p v-else-if="schedule" class="schedule-what break" data-test="approval-schedule">{{ t('任务 {id}，删除后不可恢复。', { id: schedule.id }) }}</p>
      <code v-else class="break args">{{ current.arguments }}</code>
    </div>
    <div class="approval-actions">
      <HintTip v-if="schedule" align="end" data-test="approval-hint">
        {{ t('无人应答即拒绝。定时任务到点后，助手会照这句话去做，可以随时在「定时」页删除。') }}
      </HintTip>
      <HintTip v-else align="end" data-test="approval-hint">
        {{ t('无人应答即拒绝。允许后，助手可以在这个窗口上继续操作 {grantSteps} 步；危险类的动作仍然逐次询问。', { grantSteps: grantSteps }) }}
      </HintTip>
      <button class="ghost" data-test="approval-deny" @click="emit('answer', current.approvalId, false)">{{ t('拒绝') }}</button>
      <button class="primary" data-test="approval-allow" @click="emit('answer', current.approvalId, true)">{{ t('允许') }}</button>
    </div>
  </div>
</template>

<style scoped>
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

.schedule-what {
  margin: 0;
  font-size: var(--fs-ui);
}

.schedule-rule {
  font-weight: 600;
  margin-inline-end: var(--space-2);
}

.approval-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex: none;
}
</style>
