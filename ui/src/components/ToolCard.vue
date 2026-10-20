<script setup lang="ts">
import { computed } from 'vue'
import { toolAppearance } from '../toolAppearance'
import Icon from './Icon.vue'
import MarkdownBody from './MarkdownBody.vue'

/**
 * One tool call and its result, as a single card.
 * <para>
 * A call and its result are two events in the log but one thing to a reader; two unrelated rows make a tool
 * call read as two fragments of noise. The summary line carries what happened; opening the card carries what
 * was asked and what came back.
 * </para>
 */
const props = defineProps<{
  name: string
  args: string
  status: 'running' | 'ok' | 'failed'
  result: string | null
}>()

const STATUS_LABEL: Record<string, string> = {
  running: '进行中',
  ok: '完成',
  failed: '失败',
}

const appearance = computed(() => toolAppearance(props.name))

/** JSON on one line, trimmed: the summary is a glance, not the payload. */
const argsSummary = computed(() => {
  const compact = props.args.replace(/\s+/g, ' ').trim()
  if (compact === '' || compact === '{}') {
    return '（无参数）'
  }

  return compact.length > 70 ? compact.slice(0, 70) + '…' : compact
})

/** Pretty-printed when it is JSON, verbatim when it is not: a malformed argument must still be readable. */
const argsPretty = computed(() => {
  try {
    return JSON.stringify(JSON.parse(props.args), null, 2)
  } catch {
    return props.args
  }
})
</script>

<template>
  <details
    class="tool"
    :data-tool="name"
    :data-tool-family="appearance.family"
    :data-status="status"
    :data-chat-anchor-key="`tool:${name}:${appearance.family}`"
  >
    <summary>
      <span class="icon" :title="appearance.label">
        <Icon :name="appearance.icon" :size="14" />
      </span>
      <span class="name" :title="appearance.label">{{ name }}</span>
      <span class="args">{{ argsSummary }}</span>
      <span class="status" :class="status">{{ STATUS_LABEL[status] }}</span>
    </summary>

    <div class="section">
      <span class="tool-section-label">参数</span>
      <pre>{{ argsPretty }}</pre>
    </div>

    <div v-if="result !== null" class="section">
      <span class="tool-section-label">结果</span>
      <MarkdownBody :body="result" />
    </div>
  </details>
</template>

<style scoped>
/* A capsule, not a card: one line that says which tool, on what, and how it went. Opening it unfolds the
   details into the same shape rather than into a second box. The family colour is carried by the icon alone —
   the left stripe it used to have was a third channel for something the icon and the name already said. */
.tool {
  max-inline-size: 100%;
  min-inline-size: 0;
  border: 1px solid var(--glass-edge);
  border-radius: var(--radius-md);
  background: var(--surface-2);
  padding: 0 var(--space-3);
  transition: background-color var(--motion-fast) var(--ease);
}

.tool[open] {
  padding-block-end: var(--space-3);
  background: var(--glass-1);
}

/* The colour says what kind of tool it is; the status says whether it worked. Two channels, two meanings —
   conflating them would leave a failed search indistinguishable from a successful terminal command. */
.tool[data-status='failed'] {
  --tool-accent: var(--error);
  border-color: color-mix(in srgb, var(--error) 40%, transparent);
}

.tool[data-tool-family='shell'] {
  --tool-accent: var(--tool-shell);
}

.tool[data-tool-family='read'] {
  --tool-accent: var(--tool-read);
}

.tool[data-tool-family='write'] {
  --tool-accent: var(--tool-write);
}

.tool[data-tool-family='search'] {
  --tool-accent: var(--tool-search);
}

.tool[data-tool-family='web'] {
  --tool-accent: var(--tool-web);
}

.tool[data-tool-family='record'] {
  --tool-accent: var(--tool-record);
}

.tool[data-tool-family='schedule'] {
  --tool-accent: var(--tool-schedule);
}

.tool[data-tool-family='agent'] {
  --tool-accent: var(--tool-agent);
}

.tool[data-tool-family='control'] {
  --tool-accent: var(--tool-control);
}

summary {
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-block-size: 2.25rem;
  min-inline-size: 0;
  list-style: none;
}

summary::-webkit-details-marker {
  display: none;
}

.icon {
  flex: none;
  color: var(--tool-accent, var(--text-2));
  display: inline-flex;
  align-items: center;
  justify-content: center;
  inline-size: 1.35em;
}

.name {
  color: var(--text);
  flex: none;
  font-family: var(--font-mono);
  font-size: var(--fs-meta);
  line-height: var(--lh-code);
}

.args {
  flex: 1;
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.status {
  flex: none;
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.status::before {
  content: '';
  inline-size: 6px;
  block-size: 6px;
  border-radius: 50%;
  background: currentColor;
}

.status.ok {
  color: var(--ok);
}

.status.running {
  color: var(--accent);
}

.status.running::before {
  animation: pulse-dot 1.4s ease-in-out infinite;
}

.status.failed {
  color: var(--error);
}

@keyframes pulse-dot {
  50% {
    opacity: 0.25;
  }
}

.section {
  margin-block-start: var(--space-3);
  display: grid;
  gap: var(--space-1);
  min-inline-size: 0;
}

.tool-section-label {
  font-size: var(--fs-meta);
  color: var(--text-3);
}

pre {
  margin: 0;
  padding: var(--space-3);
  background: var(--surface-2);
  border-radius: var(--radius-sm);
  overflow: auto;
  max-block-size: 20rem;
  font-family: var(--font-mono);
  font-size: var(--fs-meta);
  line-height: var(--lh-code);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
