<script setup lang="ts">
import { computed } from 'vue'
import { toolAppearance } from '../toolAppearance'
import Icon from './Icon.vue'
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
.tool {
  /* An inset block, not a box. It used to draw a full hairline on all four sides *and* sit on --surface-1,
     which is two signals for one relationship and left the card invisible against the row behind it. Now the
     surface step carries it and only the family stripe stays — the stripe is information, the border was not. */
  border-inline-start: 3px solid var(--tool-accent, var(--line-strong));
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  padding: var(--space-1) var(--space-2);
  min-inline-size: 0;
}

/* The colour says what kind of tool it is; the status says whether it worked. Two channels, two meanings —
   conflating them would leave a failed search indistinguishable from a successful terminal command. */
.tool[data-status='failed'] {
  --tool-accent: var(--error);
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
  align-items: baseline;
  gap: var(--space-2);
  min-inline-size: 0;
}

.icon {
  flex: none;
  color: var(--tool-accent, var(--accent));
  display: inline-flex;
  align-items: center;
  justify-content: center;
  /* A fixed box so the family glyph, the name and the status all sit on the same baseline whether the icon is
     a page or a crosshair — the whole point of replacing the characters. */
  inline-size: 1.35em;
}

.name {
  color: var(--accent);
  flex: none;
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
}

.args {
  flex: 1;
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-2);
}

.status {
  flex: none;
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.status.ok {
  color: var(--ok);
}

.status.running {
  color: var(--accent);
}

.status.failed {
  color: var(--error);
}

.section {
  margin-block-start: var(--space-2);
  display: grid;
  gap: var(--space-1);
  min-inline-size: 0;
}

.tool-section-label {
  font-size: var(--fs-meta);
  color: var(--text-3);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

pre {
  margin: 0;
  padding: var(--space-2);
  background: var(--surface-0);
  border-radius: var(--radius-sm);
  overflow: auto;
  max-block-size: 20rem;
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
  font-size: var(--fs-meta);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
