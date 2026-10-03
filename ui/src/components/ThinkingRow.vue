<script setup lang="ts">
import { computed } from 'vue'
import { useSharedTick } from '../clock'
import Icon from './Icon.vue'
import MarkdownBody from './MarkdownBody.vue'

/**
 * One stretch of the model's reasoning, folded to a line.
 *
 * <para>
 * A reasoning model can think for minutes before it says anything. Shown as prose, that reads as a hang;
 * shown as nothing at all, it is one. So the row is always there, always says how long it has been, and is
 * open by itself while the model is still thinking — a reader who wants the detail watches it arrive, and one
 * who does not sees a line that is visibly alive.
 * </para>
 *
 * <para>
 * It stops open. When the answer lands the log holds a finished <code>Thinking</code> event, and this renders
 * it collapsed: the reasoning is there to be consulted, not to be re-read on every later scrollback.
 * </para>
 */
const props = defineProps<{
  body: string
  /** True while the model is still thinking: the row is open and the clock runs. */
  running?: boolean | undefined
  /** ISO instant the current run of thinking started, when the server knows one. */
  since?: string | null | undefined
}>()

/**
 * How long the current run has been.
 *
 * <para>
 * Derived from a known start instant rather than held: this is a display value the client recomputes, the
 * same allowance the queue's running timer already has. It shows nothing at all once the model has stopped —
 * a clock left running on a finished row would be showing a duration nobody cares about.
 * </para>
 *
 * <para>
 * This row used to own a one-second interval, started and stopped by its own `running` flag. It could not: a
 * session view re-reads itself every few hundred milliseconds and hands back a *new* row each time, so a
 * remount would leave the clock frozen at whatever it first computed — and a turn that thinks in three
 * stretches ran three timers to display the same number three times. One shared clock instead
 * (`ui/src/clock.ts`); the flag now only decides whether the row shows a number.
 * </para>
 */
// 只在真正思考时订阅：已完成的思考段永远留在日志里，按生命周期订阅会让时钟一直跑。
const now = useSharedTick(() => props.running === true)
const startedAt = computed(() => (props.since === null || props.since === undefined ? null : Date.parse(props.since)))

const elapsed = computed(() => {
  if (props.running !== true || startedAt.value === null || Number.isNaN(startedAt.value)) {
    return ''
  }

  const seconds = Math.max(0, Math.floor((now.value - startedAt.value) / 1000))
  return `已思考 ${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
})

/** What the row says when it is collapsed. */
const summary = computed(() => {
  const firstLine = props.body
    .split('\n')
    .map((line) => line.trim())
    .find((line) => line.length > 0)

  if (firstLine === undefined) {
    return ''
  }

  return firstLine.length > 72 ? firstLine.slice(0, 72) + '…' : firstLine
})
</script>

<template>
  <details
    class="thinking"
    :class="{ 'is-running': running === true }"
    :open="running === true"
    data-test="session-thinking"
  >
    <summary>
      <span class="mark">
        <Icon name="sparkle" :size="14" />
      </span>
      <span class="thinking-label">思考</span>
      <span v-if="running === true" class="summary-line" data-test="session-thinking-live">{{ summary }}</span>
      <span v-else class="summary-line muted">{{ summary }}</span>
      <span v-if="elapsed" class="elapsed" data-test="session-thinking-elapsed">{{ elapsed }}</span>
    </summary>

    <div class="thinking-body">
      <MarkdownBody :body="body" />
    </div>
  </details>
</template>

<style scoped>
.thinking {
  /* Same treatment as the tool card: an inset block whose surface step does the work, with the stripe left
     because it says "receded" — a finished thought is in the log but is not part of the conversation. */
  border-inline-start: 3px solid var(--text-3);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  padding: var(--space-1) var(--space-2);
  min-inline-size: 0;
}

/* While the model is thinking this is the only thing moving on screen, so it has to look alive. */
.thinking.is-running {
  border-inline-start-color: var(--accent);
}

summary {
  cursor: pointer;
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
  min-inline-size: 0;
}

.mark {
  flex: none;
  color: var(--text-3);
  display: inline-flex;
  align-items: center;
  animation: none;
}

/* The parent already carries `.is-running`, so the state is read from there rather than set twice. */
.thinking.is-running .mark {
  color: var(--accent);
  animation: pulse 1.6s ease-in-out infinite;
}

.thinking-label {
  flex: none;
  font-size: var(--fs-meta);
  color: var(--text-3);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.summary-line {
  flex: 1;
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text);
}

.elapsed {
  flex: none;
  font-size: var(--fs-meta);
  color: var(--accent);
  font-variant-numeric: tabular-nums;
}

/* No scroll container here, deliberately.

   This used to be `max-block-size: 18rem; overflow-y: auto`, on the reasoning that a long chain of thought
   must not push everything after it off the screen. But the list it lives in already scrolls: growing here
   pushes the rest of the log *down the log*, which is what a log is, and nothing leaves the screen that the
   reader could not scroll to anyway.

   What the inner scroller actually cost was a bug, not a design: a classic scrollbar takes width out of the
   box it belongs to, so the moment a thought was long enough to need one, every line of it ended ~17px short
   of the line end — a wrap that moved as the text scrolled. Prose that reflows while you read it is worse
   than prose that is simply further down. Code blocks keep their scrollers, where a fixed width is what you
   want; prose does not.

   Named `thinking-body`, not `body`: styles.css has a global `.body` that lays the shell out as sidebar +
   content, and a scoped rule that only sets colour and font-size does not stop that from applying. It did,
   and every line of a long thought wrapped at the sidebar's width — which is why this element is not the
   obvious name. */
.thinking-body {
  margin-block-start: var(--space-2);
  color: var(--text-2);
  font-size: var(--fs-ui);
}

/* Motion is handled in one place — the app-wide `prefers-reduced-motion` block in styles.css. This used to
   carry its own guard for `.mark`; two blocks means the next animation somebody adds is not covered. */
</style>
