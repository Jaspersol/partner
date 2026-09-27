<script setup lang="ts">
import type { SessionItem } from '../api'
import MarkdownBody from './MarkdownBody.vue'

defineProps<{ items: SessionItem[]; selectedSeq: number | null }>()
const emit = defineEmits<{ select: [seq: number] }>()

/**
 * Kinds that are machinery rather than conversation: a tool call and its result. They are shown — otherwise a
 * turn that called a tool looks like it did nothing — but folded away, because the payload is noise until
 * someone is debugging.
 */
const controlKinds = new Set(['ToolCall', 'ToolResult'])

function isControl(item: SessionItem): boolean {
  return controlKinds.has(item.kind)
}

/** One line that says what the folded row is, without opening it. */
function controlSummary(item: SessionItem): string {
  const label = item.kind === 'ToolCall' ? '调用' : '结果'
  const name = item.kind === 'ToolCall' ? item.body : ''
  const detail = item.kind === 'ToolCall' ? '' : item.body
  const text = (name || detail).replace(/\s+/g, ' ')

  return `${label} ${text.length > 80 ? text.slice(0, 80) + '…' : text}`.trim()
}
</script>

<template>
  <ul class="list" data-test="session-list">
    <li
      v-for="item in items"
      :key="item.streaming ? 'streaming' : item.seq"
      :class="{
        selected: item.seq === selectedSeq,
        streaming: item.streaming === true,
        control: isControl(item),
      }"
      :data-kind="item.kind"
      @click="item.streaming === true || isControl(item) ? undefined : emit('select', item.seq)"
    >
      <div class="meta">
        <template v-if="item.streaming">
          <span data-test="session-streaming">正在回复…</span>
        </template>
        <template v-else>
          #{{ item.seq }} {{ item.kind }} / {{ item.source }} {{ item.at.slice(11, 19) }}
          <span v-if="item.revisionCount > 0" class="edited">已编辑 {{ item.revisionCount }}</span>
        </template>
      </div>

      <!-- Folded by default: the row says what happened, opening it says what it contained. -->
      <details v-if="isControl(item)" class="fold" :data-test="`session-control-${item.kind}`">
        <summary>{{ controlSummary(item) }}</summary>
        <MarkdownBody :body="item.body" />
      </details>

      <template v-else>
        <MarkdownBody :body="item.body" />
        <!-- Always visible, never only in the inspector: an edit must be obvious from the list itself. -->
        <div v-if="item.revisionCount > 0" class="edited" data-test="session-revision-note">
          已编辑 {{ item.revisionCount }} 次 · 原文：{{ item.originalBody }}
        </div>
      </template>
    </li>
  </ul>
</template>

<style scoped>
.fold summary {
  cursor: pointer;
  color: var(--muted);
  font-family: var(--font-mono, monospace);
  font-size: var(--fs-meta, 0.85em);
}

.fold[open] summary {
  color: var(--text);
}

.control {
  border-inline-start: 2px solid var(--line);
  padding-inline-start: var(--space-2);
}

.streaming .meta {
  color: var(--accent);
}

/* The caret is the whole point of the streaming row: the text is still arriving. */
.streaming .meta::after {
  content: "▌";
  margin-inline-start: 0.25em;
  animation: caret 1s steps(2, start) infinite;
}

@keyframes caret {
  50% {
    opacity: 0;
  }
}
</style>
