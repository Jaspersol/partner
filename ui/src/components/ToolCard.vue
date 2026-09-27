<script setup lang="ts">
import { computed } from 'vue'
import MarkdownBody from './MarkdownBody.vue'

/**
 * One tool call and its result, as a single card.
 * <para>
 * A call and its result are two events in the log, but they are one thing to a reader: the log used to render
 * them as two unrelated rows, so a turn that called a tool looked like two fragments of noise. The summary
 * line carries what happened; opening the card carries what was asked and what came back.
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
  <details class="tool" :data-tool="name" :data-status="status">
    <summary>
      <span class="name">{{ name }}</span>
      <span class="args">{{ argsSummary }}</span>
      <span class="status" :class="status">{{ STATUS_LABEL[status] }}</span>
    </summary>

    <div class="section">
      <span class="label">参数</span>
      <pre>{{ argsPretty }}</pre>
    </div>

    <div v-if="result !== null" class="section">
      <span class="label">结果</span>
      <MarkdownBody :body="result" />
    </div>
  </details>
</template>

<style scoped>
.tool {
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  padding: var(--space-1) var(--space-2);
  margin-block: var(--space-1);
}

summary {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
  cursor: pointer;
  font-family: var(--font-mono, monospace);
  font-size: var(--fs-meta, 0.85em);
}

.name {
  color: var(--accent);
  font-weight: 600;
}

.args {
  color: var(--muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.status {
  margin-inline-start: auto;
  color: var(--muted);
}

/* A failed call is the one a reader must not have to open to notice. */
.status.failed {
  color: var(--error);
  font-weight: 600;
}

.section {
  margin-block-start: var(--space-1);
}

.label {
  color: var(--muted);
  font-size: var(--fs-meta, 0.85em);
}

pre {
  margin: var(--space-1) 0 0;
  padding: var(--space-1);
  background: var(--pane);
  border-radius: var(--radius-sm);
  overflow: auto;
  white-space: pre-wrap;
}
</style>
