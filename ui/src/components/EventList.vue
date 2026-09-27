<script setup lang="ts">
import { computed } from 'vue'
import type { SessionItem } from '../api'
import MarkdownBody from './MarkdownBody.vue'
import ToolCard from './ToolCard.vue'

const props = defineProps<{ items: SessionItem[]; selectedSeq: number | null }>()
const emit = defineEmits<{ select: [seq: number] }>()

/** One rendered line: either a conversation event, or a tool call with its result folded in. */
type Row =
  | { kind: 'event'; item: SessionItem }
  | { kind: 'tool'; seq: number; name: string; args: string; status: 'running' | 'ok' | 'failed'; result: string | null }

/**
 * Pairs each tool call with its result.
 * <para>
 * They are two events in the log and one thing to a reader. The pairing key is the call id the runner wrote
 * into both events' meta; a result whose call is missing (a partial removal, an older log) still renders, so
 * nothing disappears silently.
 * </para>
 */
const rows = computed<Row[]>(() => {
  const out: Row[] = []
  const open = new Map<string, Extract<Row, { kind: 'tool' }>>()

  for (const item of props.items) {
    if (item.kind === 'ToolCall') {
      const card: Row = {
        kind: 'tool',
        seq: item.seq,
        name: item.meta?.name ?? item.body,
        args: item.meta?.args ?? '{}',
        status: 'running',
        result: null,
      }

      const id = item.meta?.id
      if (id !== undefined) {
        open.set(id, card as Extract<Row, { kind: 'tool' }>)
      }

      out.push(card)
      continue
    }

    if (item.kind === 'ToolResult') {
      const id = item.meta?.id
      const card = id === undefined ? undefined : open.get(id)

      if (card !== undefined) {
        card.status = item.meta?.ok === 'true' ? 'ok' : 'failed'
        card.result = item.body
        if (id !== undefined) {
          open.delete(id)
        }

        continue
      }

      // An orphan result: shown on its own rather than dropped.
      out.push({
        kind: 'tool',
        seq: item.seq,
        name: item.meta?.name ?? 'tool',
        args: '{}',
        status: item.meta?.ok === 'true' ? 'ok' : 'failed',
        result: item.body,
      })
      continue
    }

    out.push({ kind: 'event', item })
  }

  return out
})
</script>

<template>
  <ul class="list" data-test="session-list">
    <li
      v-for="row in rows"
      :key="row.kind === 'tool' ? `tool-${row.seq}` : row.item.streaming ? 'streaming' : row.item.seq"
      :class="{
        selected: row.kind === 'event' && row.item.seq === selectedSeq,
        streaming: row.kind === 'event' && row.item.streaming === true,
      }"
      :data-kind="row.kind === 'tool' ? 'Tool' : row.item.kind"
      @click="row.kind === 'event' && row.item.streaming !== true ? emit('select', row.item.seq) : undefined"
    >
      <ToolCard
        v-if="row.kind === 'tool'"
        :name="row.name"
        :args="row.args"
        :status="row.status"
        :result="row.result"
      />

      <template v-else>
        <div class="meta">
          <template v-if="row.item.streaming">
            <span data-test="session-streaming">正在回复…</span>
          </template>
          <template v-else>
            #{{ row.item.seq }} {{ row.item.kind }} / {{ row.item.source }} {{ row.item.at.slice(11, 19) }}
            <span v-if="row.item.revisionCount > 0" class="edited">已编辑 {{ row.item.revisionCount }}</span>
          </template>
        </div>

        <MarkdownBody :body="row.item.body" />
        <!-- Always visible, never only in the inspector: an edit must be obvious from the list itself. -->
        <div v-if="row.item.revisionCount > 0" class="edited" data-test="session-revision-note">
          已编辑 {{ row.item.revisionCount }} 次 · 原文：{{ row.item.originalBody }}
        </div>
      </template>
    </li>
  </ul>
</template>

<style scoped>
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
