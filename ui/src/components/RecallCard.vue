<script setup lang="ts">
import { t } from '../i18n'
import { computed } from 'vue'
import Icon from './Icon.vue'
import MarkdownBody from './MarkdownBody.vue'

/**
 * What a turn was handed from memory, as one card.
 *
 * <para>
 * The same shape as a tool card, because it is the same kind of thing to a reader: something the turn did
 * before it spoke, folded to a line and openable for the detail. Rendered as a plain system row it read as a
 * wall of text between two messages — and its first line, the header that says "background, not something the
 * person just said", was indistinguishable from the facts under it.
 * </para>
 *
 * <para>
 * The head says <code>recall</code> rather than 记忆检索: it is the name of the thing in the prompt and in the
 * log, and a reader looking for it should be able to look for the same word in both places.
 * </para>
 */
const props = defineProps<{
  body: string
}>()

/** How many facts were handed over: every line that starts with the list marker. */
const count = computed(() => props.body.split('\n').filter((line) => line.trimStart().startsWith('- ')).length)

/** The first fact, trimmed: the summary is a glance, not the payload. */
const summary = computed(() => {
  const first = props.body
    .split('\n')
    .map((line) => line.trim())
    .find((line) => line.startsWith('- '))

  if (first === undefined) {
    return props.body.split('\n').map((line) => line.trim()).find((line) => line.length > 0) ?? ''
  }

  const fact = first.slice(2).trim()
  return fact.length > 70 ? fact.slice(0, 70) + '…' : fact
})
</script>

<template>
  <details class="recall" data-test="session-recall">
    <summary>
      <span class="icon" :title="t('记忆召回')">
        <Icon name="database" :size="14" />
      </span>
      <span class="name">recall</span>
      <span class="summary-line">{{ summary }}</span>
      <span v-if="count > 0" class="count">{{ t('{count} 条', { count: count }) }}</span>
    </summary>

    <div class="recall-body">
      <MarkdownBody :body="body" />
    </div>
  </details>
</template>

<style scoped>
/* The tool capsule, in the record family's colour: a recall is not a call the model made, but it is the same
   kind of row — something that happened in the turn, folded to a line. */
.recall {
  max-inline-size: 100%;
  min-inline-size: 0;
  border: 1px solid var(--glass-edge);
  border-radius: var(--radius-md);
  background: var(--surface-2);
  padding: 0 var(--space-3);
}

.recall[open] {
  padding-block-end: var(--space-3);
  background: var(--glass-1);
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
  color: var(--tool-record);
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

.summary-line {
  flex: 1;
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.count {
  flex: none;
  font-size: var(--fs-meta);
  color: var(--text-3);
  font-variant-numeric: tabular-nums;
}

.recall-body {
  margin-block-start: var(--space-1);
  color: var(--text-2);
  font-size: var(--fs-ui);
  overflow-wrap: anywhere;
}
</style>
