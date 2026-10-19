<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { SessionItem } from '../api'
import { clampIndex, isActivationKey, normalizeKey, rovingTarget } from '../rovingFocus'
import { sessionKindLabel, sourceHasChip, sourceLabel } from '../sessionKind'
import { showToast } from '../toast'
import Icon from './Icon.vue'
import MarkdownBody from './MarkdownBody.vue'
import ToolCard from './ToolCard.vue'
import ThinkingRow from './ThinkingRow.vue'

const props = defineProps<{
  items: SessionItem[]
  selectedSeq: number | null
  /**
   * The first seq of the latest conversation, from `conversation.latestFromSeq`. Delete is offered only for
   * rows at or after it: deleting anything earlier would leave newer history answering a question that no
   * longer exists, which is what withdrawing is for.
   */
  latestFromSeq?: number | null | undefined
  /** True while a withdrawal is pending. Nothing may be deleted until it is cancelled or made final. */
  withdrawn?: boolean | undefined
  /** True while a turn is running: the server refuses history changes then, so the buttons are not offered. */
  busy?: boolean | undefined
}>()
const emit = defineEmits<{
  select: [seq: number]
  /** Deletes the latest conversation as a whole — permanent, so the view asks twice. */
  remove: [seq: number]
  /** Hides this conversation and everything after it. Reversible, so one press is enough. */
  withdraw: [seq: number]
  /** Puts a removed message back in the composer. Only fired for the reader's own messages. */
  restore: [seq: number]
}>()

/** One rendered line: a conversation event, a tool call with its result folded in, or a stretch of thinking. */
type Row =
  | { kind: 'event'; item: SessionItem; selectable: boolean }
  | { kind: 'tool'; seq: number; name: string; args: string; status: 'running' | 'ok' | 'failed'; result: string | null }
  | { kind: 'thinking'; key: string; body: string; running: boolean; since: string | null }

/**
 * Pairs each tool call with its result.
 * <para>
 * They are two events in the log and one thing to a reader. The pairing key is the call id the runner wrote
 * into both events' meta; a result whose call is missing (a partial removal, an older log) still renders, so
 * nothing disappears silently.
 * </para>
 */
/** A tool call and its result, already paired. */
type ToolRow = Extract<Row, { kind: 'tool' }>

/**
 * Folds the log into rows: pairs each tool call with its result, and keeps a result whose call is missing.
 *
 * <para>
 * Built immutably. It used to keep a map of call-id → card <i>object</i> and then write `card.status` and
 * `card.result` into it as the matching result came along. That works today only because the whole array is
 * rebuilt from scratch every time and Vue re-renders from it — nothing depends on the mutation, and nothing
 * would notice it. The moment anything renders a row in place (a memoised card, a virtual list, a diff against
 * the previous rows) a mutated object is the bug: the row and its card are the same object, so a change cannot
 * be observed as a change.
 * </para>
 *
 * <para>
 * The map therefore holds <i>positions</i>, and a completed card replaces the entry at that position. One new
 * object per paired call; nothing is written after the fact.
 * </para>
 */
const rows = computed<Row[]>(() => {
  const out: Row[] = []
  const openCallAt = new Map<string, number>()

  for (const item of props.items) {
    if (item.kind === 'ToolCall') {
      out.push({
        kind: 'tool',
        seq: item.seq,
        name: item.meta?.name ?? item.body,
        args: item.meta?.args ?? '{}',
        status: 'running',
        result: null,
      })

      const id = item.meta?.id
      if (id !== undefined) {
        openCallAt.set(id, out.length - 1)
      }

      continue
    }

    if (item.kind === 'ToolResult') {
      const id = item.meta?.id
      const at = id === undefined ? undefined : openCallAt.get(id)

      if (id !== undefined && at !== undefined) {
        out[at] = {
          kind: 'tool',
          seq: (out[at] as ToolRow).seq,
          name: (out[at] as ToolRow).name,
          args: (out[at] as ToolRow).args,
          status: item.meta?.ok === 'true' ? 'ok' : 'failed',
          result: item.body,
        }
        openCallAt.delete(id)
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

    // Reasoning arrives as its own row: it is a different kind of thing from the answer, and a reader who
    // wants to know "what was it working out" should not have to read the answer to find out. Both the logged
    // event and the live placeholder land here.
    if (item.kind === 'Thinking' || item.kind === 'ThinkingStreaming') {
      out.push({
        kind: 'thinking',
        // A live row has no seq of its own, so it is keyed by what it is rather than by where it sits.
        key: item.streaming === true ? 'thinking-streaming' : `thinking-${item.seq}`,
        // The live row's text lives beside the body, in `reasoning`; a logged one carries it in the body like
        // every other event. A finished row has no clock: "thinking for 90s" is what was true while it was
        // running, and showing it afterwards would read as a duration the model is still taking.
        body: item.streaming === true ? (item.reasoning ?? item.body) : item.body,
        running: item.streaming === true,
        since: item.streaming === true ? (item.reasoningSince ?? item.at) : null,
      })
      continue
    }

    // The streaming placeholder is not in the log yet, so it has no seq to select and nothing to edit.
    out.push({ kind: 'event', item, selectable: item.streaming !== true })
  }

  return out
})

/**
 * Which side of the conversation a row sits on, and whether it opens a run.
 *
 * <para>
 * The reader's own messages are on the right and the assistant's whole turn — its thinking, its tool calls and its
 * answer — is on the left; everything else the log carries (system lines, schedule triggers, summaries, kinds this
 * list has never heard of) is a centred note, because none of it is anybody speaking. Position is the first
 * channel for "who said this"; the role word stays in the row for a screen reader.
 * </para>
 *
 * <para>
 * `first` is true when the side changes from the row above (a note always starts a run). It is what puts a gap
 * between speakers and gives only the first row of an assistant run its avatar. It is computed here and applied
 * with `data-first`, so a row is still exactly one `<li>` and the markup the tests read does not move.
 * </para>
 */
type Side = 'left' | 'right' | 'center'

function sideOf(row: Row): Side {
  if (row.kind === 'tool' || row.kind === 'thinking') return 'left'
  if (row.item.kind === 'User') return 'right'
  if (row.item.kind === 'Assistant') return 'left'
  return 'center'
}

const layout = computed(() =>
  rows.value.map((row, at) => {
    const side = sideOf(row)
    const before = at > 0 ? sideOf(rows.value[at - 1] as Row) : null
    return { side, first: side === 'center' || before !== side }
  }),
)

/** The row's role as a word, for the one thing the page no longer prints: who is speaking. */
function roleWord(row: Row): string {
  if (row.kind === 'tool') return '工具'
  if (row.kind === 'thinking') return '思考'
  return sessionKindLabel(row.item.kind)
}

const NOTE_FOLD_CHARS = 140

function isLongNote(body: string): boolean {
  return body.length > NOTE_FOLD_CHARS || body.trim().includes('\n')
}

function noteTitle(body: string): string {
  const first = body.split('\n').map((line) => line.trim()).find((line) => line !== '') ?? ''
  return first.length > 60 ? `${first.slice(0, 60)}…` : first
}

async function copy(row: Row): Promise<void> {
  if (row.kind !== 'event') return
  try {
    await navigator.clipboard.writeText(row.item.body)
    showToast('已复制')
  } catch {
    showToast('复制失败', 'error')
  }
}

/**
 * Only the reader's own typed messages can be put back in the box. An assistant reply restored there would be
 * sent as the human's words, and a scheduled task's text is not theirs to resend either.
 */
function restorable(item: SessionItem): boolean {
  return item.kind === 'User' && item.source.toLowerCase() === 'text' && item.streaming !== true
}

/**
 * Whether this row may be deleted.
 *
 * <para>
 * Delete takes the whole latest conversation, so it is offered only for rows inside it. It is refused by the
 * server while a withdrawal is pending (the hidden lines would have to be purged first) and while a turn is
 * running (the turn would keep writing into the range being removed), so the button is not offered then either.
 * </para>
 */
function deletable(item: SessionItem): boolean {
  if (item.streaming === true || props.busy === true || props.withdrawn === true) return false
  const latest = props.latestFromSeq
  return latest !== null && latest !== undefined && item.seq >= latest
}

/** Inline two-step confirmation: a native dialog would block the webview and cannot be driven in tests. */
const armed = ref<{ seq: number } | null>(null)

/**
 * Selecting a row does two things, and they are not the same thing.
 *
 * <para>
 * `focusIndex` moves with the selection so that tabbing onward continues from where the reader is, rather than
 * from wherever the keyboard happened to be. With one tab stop for the whole list those two can genuinely
 * differ, so they are said out loud here instead of happening to coincide.
 * </para>
 */
function onSelect(seq: number, index: number): void {
  focusIndex.value = index
  emit('select', seq)
}

/** Deletes the row's conversation: arming on the first press, the emit on the second. */
function act(row: Row) {
  // Nothing to remove by sequence from a card or a stretch of thinking: those are folded views over events,
  // and a reader who wants one gone removes the conversation behind it from its own row.
  if (row.kind !== 'event' || !deletable(row.item)) {
    return
  }

  const seq = row.item.seq

  // Arming is per row: a second press on the *same* item is the confirmation, and a press on another item is
  // not — otherwise the arm would move and the reader would confirm a row they had not armed.
  if (armed.value?.seq !== seq) {
    armed.value = { seq }
    return
  }

  armed.value = null
  emit('remove', seq)
}

/**
 * Withdraws from this row's conversation. One press: it hides the range and can be taken back until the next
 * message is sent, so a confirmation step would only slow down an action the reader can undo.
 */
function withdraw(row: Row) {
  if (row.kind !== 'event') {
    return
  }

  armed.value = null
  emit('withdraw', row.item.seq)
}

/** Backs out of an armed delete without doing it. */
function cancelArm() {
  armed.value = null
}

/**
 * Which row the keyboard is on.
 *
 * <para>
 * One index rather than one `tabindex` per row. With every row focusable, a keyboard reader had to press Tab
 * once per event to get past the conversation — five hundred presses to reach the composer, on a list that is
 * still growing while they read it. Now the list is a single tab stop and the arrows move within it.
 * </para>
 */
const focusIndex = ref(0)

/** Follow the list as it grows: one new event must not drop the reader back at the top of the history. */
watch(rows, (list) => {
  focusIndex.value = clampIndex(focusIndex.value, list.length)
})

function onListKeydown(event: KeyboardEvent, index: number) {
  // Escape first: it is the key a keyboard reader reaches for when a delete is armed, and it must not be eaten
  // by the arrow handling below — or by the row's own activation keys.
  if (normalizeKey(event.key) === 'escape' && armed.value !== null) {
    event.stopPropagation()
    cancelArm()
    return
  }

  const target = rovingTarget(index, rows.value.length, event.key)
  if (target === null) {
    // Enter and Space activate the row. They are handled here rather than as two more template bindings so
    // that every key this row answers lives in one readable place — and so that `Space` does not also scroll
    // the list, which is the default action a row otherwise gets.
    if (isActivationKey(event.key)) {
      event.preventDefault()
      const seq = rowSeq(index)
      if (seq !== null) onSelect(seq, index)
    }
    return
  }

  event.preventDefault()
  focusIndex.value = target

  // Move the focus, not just the tab stop: an arrow key that only moves the ring leaves a keyboard reader
  // pressing arrows with no way to tell whether anything happened.
  void nextTick(() => {
    const list = listElement.value
    list?.querySelectorAll<HTMLElement>('[data-row]')[target]?.focus()
  })
}

const listElement = ref<HTMLElement | null>(null)

/** The log sequence number a rendered row stands for, or null for a row that is not a selectable event. */
function rowSeq(index: number): number | null {
  const row = rows.value[index]
  return row?.kind === 'event' && row.selectable ? row.item.seq : null
}
</script>

<template>
  <!--
    A conversation, not a log. Left is the assistant's turn (thinking, tool calls, answer), right is the reader,
    and the few things nobody is saying — system lines, schedule triggers, summaries — are centred notes.

    <para>
    Every event is still exactly one `<li>`, with `data-kind` carrying the wire value: the sides are CSS
    (`data-side`), the run gaps are CSS (`data-first`), and nothing is re-nested. That is what keeps the keyboard
    model (one tab stop, arrows move) and every test that reads rows as they are.
    </para>

    <para>
    `role="list"`, not `role="listbox"`: a tool card and a stretch of reasoning are part of the conversation but
    are not things one *selects*, and a listbox may only contain options.
    </para>
  -->
  <ul
    ref="listElement"
    class="event-list"
    role="list"
    aria-label="会话事件"
    data-test="session-list"
  >
    <li
      v-for="(row, index) in rows"
      :key="row.kind === 'tool' ? `tool-${row.seq}` : row.kind === 'thinking' ? row.key : row.item.streaming ? 'streaming' : row.item.seq"
      :class="{
        selected: row.kind === 'event' && row.item.seq === selectedSeq,
        streaming: row.kind === 'event' && row.item.streaming === true,
        // The toolbar stays up while a confirmation is armed. It appears on hover, and a confirmation that
        // vanishes the moment the pointer wanders off is worse than none: the reader would have to hunt for the
        // button again to finish what they started.
        'is-armed': row.kind === 'event' && armed?.seq === row.item.seq,
      }"
      :data-kind="row.kind === 'tool' ? 'Tool' : row.kind === 'thinking' ? 'Thinking' : row.item.kind"
      :data-side="layout[index]?.side"
      :data-first="layout[index]?.first ? 'true' : 'false'"
      :data-chat-anchor-key="row.kind === 'tool' ? `tool-${row.seq}` : row.kind === 'thinking' ? row.key : `event-${row.item.streaming ? 'streaming' : row.item.seq}`"
      :data-row="index"
      :tabindex="row.kind === 'event' && row.selectable && index === focusIndex ? 0 : -1"
      :aria-current="row.kind === 'event' && row.selectable && row.item.seq === selectedSeq ? 'true' : undefined"
      @focus="row.kind === 'event' && row.selectable ? (focusIndex = index) : undefined"
      @keydown="row.kind === 'event' && row.selectable ? onListKeydown($event, index) : undefined"
    >
      <div class="stack">
        <ToolCard
          v-if="row.kind === 'tool'"
          :name="row.name"
          :args="row.args"
          :status="row.status"
          :result="row.result"
        />

        <ThinkingRow
          v-else-if="row.kind === 'thinking'"
          :body="row.body"
          :running="row.running"
          :since="row.since"
        />

        <template v-else>
          <!-- A long note (the memory the assistant was handed, a summary) folds to its first line: it is context
               for the turn, not part of the conversation, and a screenful of it between two messages is the
               opposite of low density. A short one is just a pill. -->
          <details v-if="layout[index]?.side === 'center' && isLongNote(row.item.body)" class="bubble note-fold" :data-role="row.item.kind">
            <summary>{{ noteTitle(row.item.body) }}</summary>
            <MarkdownBody :body="row.item.body" />
          </details>
          <div v-else class="bubble" :data-role="row.item.kind">
            <MarkdownBody :body="row.item.body" />
          </div>

          <!--
            Who, when, and what can be done — shown on hover or focus and otherwise not at all. The role word is
            there for a screen reader (position is the only thing saying it on screen); the source is an icon,
            because the row beside a microphone does not need the word 「语音」 spelled out.
          -->
          <div class="meta">
            <span class="role sr-only">{{ roleWord(row) }}</span>
            <span v-if="row.item.source && sourceHasChip(row.item.source)" class="source" :title="sourceLabel(row.item.source)">
              <Icon v-if="row.item.source === 'Voice'" name="mic" :size="12" />
              <Icon v-else-if="row.item.source === 'Text'" name="keyboard" :size="12" />
              <Icon v-else-if="row.item.source === 'Schedule'" name="clock" :size="12" />
              <template v-else>{{ sourceLabel(row.item.source) }}</template>
              <span class="sr-only">{{ sourceLabel(row.item.source) }}</span>
            </span>
            <time class="at" :datetime="row.item.at">{{ row.item.at.slice(11, 16) }}</time>
            <span class="seq" title="事件序号">#{{ row.item.seq }}</span>

            <span v-if="row.item.streaming !== true" class="row-actions" data-test="session-row-actions">
              <button class="ghost row-action" data-test="session-row-copy" title="复制" @click.stop="copy(row)">
                <Icon name="copy" :size="14" /><span class="sr-only">复制</span>
              </button>
              <button
                v-if="restorable(row.item)"
                class="ghost row-action"
                data-test="session-row-restore"
                title="把这条放回输入框"
                @click.stop="emit('restore', row.item.seq)"
              >
                <Icon name="pencil" :size="14" /><span class="sr-only">复用</span>
              </button>
              <button
                v-if="deletable(row.item)"
                class="ghost row-action"
                :class="{ 'is-danger': armed?.seq === row.item.seq }"
                data-test="session-row-delete-inline"
                title="删除最新一次对话，不能恢复"
                @click.stop="act(row)"
              >
                <Icon v-if="armed?.seq !== row.item.seq" name="trash" :size="14" /><span :class="{ 'sr-only': armed?.seq !== row.item.seq }">{{ armed?.seq === row.item.seq ? '确认删除' : '删除' }}</span>
              </button>
              <!-- Armed: the way out sits next to the button that armed it. Without it, the only way back from a
                   mis-press would be Escape. -->
              <button
                v-if="armed?.seq === row.item.seq"
                class="ghost row-action is-quiet"
                data-test="session-row-cancel"
                title="取消删除"
                @click.stop="cancelArm"
              >
                取消
              </button>
              <button
                class="ghost row-action"
                data-test="session-row-withdraw-inline"
                title="撤回到这里：隐藏这条和之后的对话，发送新消息前可以取消"
                @click.stop="withdraw(row)"
              >
                <Icon name="undo" :size="14" /><span class="sr-only">撤回</span>
              </button>
            </span>
          </div>
        </template>
      </div>
    </li>
  </ul>
</template>

<style scoped>
.event-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 0;
}

/* One row = one grid: the stack. `data-first` is what separates
   speakers — a run of the same side sits 4px apart, a change of speaker 20px apart. */
.event-list > li {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  margin-block-start: var(--space-1);
  outline: none;
}

.event-list > li[data-first='true'] {
  margin-block-start: var(--space-5);
}

.event-list > li:first-child {
  margin-block-start: 0;
}

.stack {
  display: flex;
  flex-direction: column;
  min-inline-size: 0;
}

li[data-side='left'] .stack {
  align-items: stretch;
}

li[data-side='right'] .stack {
  align-items: flex-end;
}

li[data-side='center'] .stack {
  align-items: center;
}

/* ---- bubbles ------------------------------------------------------------------------------------- */
.bubble {
  position: relative;
  min-inline-size: 0;
  max-inline-size: min(100%, 44rem);
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-md);
  overflow-wrap: anywhere;
}

/* The assistant: a quiet glass panel. */
li[data-side='left'] .bubble {
  max-inline-size: 100%;
  background: var(--glass-1);
  border: 1px solid var(--glass-edge);
  box-shadow: var(--glass-hi), var(--shadow-1);
}

/* The reader: a tint of the accent over an opaque glass, so it reads as "yours" without shouting. */
li[data-side='right'] .bubble {
  max-inline-size: min(100%, 36rem);
  background: color-mix(in srgb, var(--accent) 13%, var(--glass-2-solid));
  border: 1px solid color-mix(in srgb, var(--accent) 22%, transparent);
  border-end-end-radius: 6px;
}

/* Notes: nobody is speaking, so no bubble — a small pill of text in the middle of the page. */
li[data-side='center'] .bubble {
  max-inline-size: min(100%, 36rem);
  padding: var(--space-1) var(--space-4);
  border-radius: var(--radius-md);
  background: var(--surface-2);
  color: var(--text-2);
  font-size: var(--fs-meta);
  text-align: center;
}

li[data-side='center'] .note-fold {
  inline-size: min(100%, 36rem);
  text-align: start;
}

li[data-side='center'] .note-fold summary {
  cursor: pointer;
  text-align: center;
  list-style: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

li[data-side='center'] .note-fold summary::-webkit-details-marker {
  display: none;
}

li[data-side='center'] .note-fold[open] summary {
  margin-block-end: var(--space-2);
}

li[data-side='center'] .bubble :deep(.markdown) {
  font-size: var(--fs-meta);
  line-height: 1.6;
  color: var(--text-2);
}

/* The body is the largest text in the app, and the only thing here anybody reads for longer than a glance. */
.bubble :deep(.markdown) {
  font-size: var(--fs-prose);
  line-height: var(--lh-prose);
  color: var(--text);
}

li:focus-visible .bubble {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

/* ---- the line under a bubble ------------------------------------------------------------------- */
/* Present in the layout (so hovering never moves anything) and invisible until it is wanted. */
.meta {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-block-size: 1.75rem;
  padding-inline: var(--space-1);
  font-size: var(--fs-meta);
  line-height: var(--lh-meta);
  color: var(--text-3);
  opacity: 0;
  transition: opacity var(--motion-fast) var(--ease);
}

li:hover > .stack > .meta,
li:focus-within > .stack > .meta,
li.is-armed > .stack > .meta {
  opacity: 1;
}

/* A touch screen has no hover; the line is simply there. */
@media (hover: none) {
  .meta {
    opacity: 1;
  }
}

li[data-side='center'] .meta {
  display: none;
}

.meta > * {
  min-inline-size: 0;
}

.source {
  display: inline-flex;
  align-items: center;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.seq {
  font-family: var(--font-mono);
}

.at {
  font-variant-numeric: tabular-nums;
}

.row-actions {
  display: flex;
  align-items: center;
  gap: 2px;
  margin-inline-start: var(--space-1);
}

.row-action {
  min-block-size: 1.75rem;
  padding: 0 var(--space-2);
  border-radius: var(--radius-pill);
  font-size: var(--fs-meta);
  white-space: nowrap;
}

.row-action.is-danger {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}

.row-action.is-quiet {
  color: var(--text-3);
}

/* ---- streaming ---------------------------------------------------------------------------------- */
/* The caret is the whole point of the streaming row: the text is still arriving. It is the one animation in the
   app that carries meaning rather than decoration, so under reduced motion it stops blinking but stays. */
.streaming .bubble::after {
  content: '▌';
  margin-inline-start: 0.2em;
  color: var(--accent);
  animation: caret 1s steps(2, start) infinite;
}

@keyframes caret {
  50% {
    opacity: 0;
  }
}

@media (prefers-reduced-motion: reduce) {
  .streaming .bubble::after {
    animation: none;
  }
}
</style>
