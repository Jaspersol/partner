<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { SessionItem } from '../api'
import { clampIndex, isActivationKey, normalizeKey, rovingTarget } from '../rovingFocus'
import { sessionKindLabel, sessionRole, sourceHasChip, sourceLabel } from '../sessionKind'
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
    The event list is the product, so it is the one place this app spends its whole visual budget on.

    It used to be a table: every row a strip with a 1px rule under it, and user messages, assistant replies,
    tool calls, reasoning and system notes all looking like the same kind of row with different words in it.
    A reader scanning a long turn had to read every row to find out who was talking. Three changes fix that, and
    none of them are cosmetic:

      - rows are grouped by *space* rather than by rules, because a hairline between two rows of a conversation
        reads as two separate documents;
      - each row carries who said it as a word **and** a dot, so the word is what a reader uses and the dot is
        what the eye scans;
      - the body is the largest text in the app, because it is the only thing here anybody actually reads.
  -->
  <!--
    `role="list"`, not `role="listbox"`.

    A listbox may only contain options, and this one cannot: a tool card and a stretch of reasoning are part of
    the conversation but are not things one *selects* — clicking them does nothing and they have no sequence to
    edit. They were `role="undefined"` children of a listbox, which is invalid markup that a screen reader
    renders as an unpredictable number of unnamed items. A list says "these are things", which is true, and
    loses nothing that was being announced reliably.
  -->
  <ul
    ref="listElement"
    class="list event-list"
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
        // The row keeps its actions on screen while a confirmation is armed. They appear on hover, and a
        // confirmation that vanishes the moment the pointer wanders off is worse than no confirmation at all:
        // the reader has to go hunting for the button again to finish what they started.
        'is-armed': row.kind === 'event' && armed?.seq === row.item.seq,
      }"
      :data-kind="row.kind === 'tool' ? 'Tool' : row.kind === 'thinking' ? 'Thinking' : row.item.kind"
      :data-chat-anchor-key="row.kind === 'tool' ? `tool-${row.seq}` : row.kind === 'thinking' ? row.key : `event-${row.item.streaming ? 'streaming' : row.item.seq}`"
      :data-row="index"
      :tabindex="row.kind === 'event' && row.selectable && index === focusIndex ? 0 : -1"
      :aria-current="row.kind === 'event' && row.selectable && row.item.seq === selectedSeq ? 'true' : undefined"
      @click="row.kind === 'event' && row.selectable ? onSelect(row.item.seq, index) : undefined"
      @focus="row.kind === 'event' && row.selectable ? (focusIndex = index) : undefined"
      @keydown="row.kind === 'event' && row.selectable ? onListKeydown($event, index) : undefined"
    >
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
        <div class="meta">
          <!-- The dot is a second channel, never the only one: the word beside it says the same thing in
               text, so a reader who cannot separate the tints loses nothing (ui.md §5.2). -->
          <span class="role-dot" :data-role="sessionRole(row.item.kind) ?? 'other'" aria-hidden="true" />
          <!-- The row says what the event *is*; `data-kind` on the <li> keeps the wire value for tests and
               for anyone reading the markup. Both are deliberate — the word is for the reader, the enum for us. -->
          <span class="role">{{ sessionKindLabel(row.item.kind) }}</span>
          <span class="seq">#{{ row.item.seq }}</span>
          <span v-if="row.item.source && sourceHasChip(row.item.source)" class="source">{{
            sourceLabel(row.item.source)
          }}</span>

          <!-- Pushed to the far end with `margin-inline-start: auto`, and tabular so a column of them lines
               up: the clock is read by comparison far more often than by reading. -->
          <time class="at" :datetime="row.item.at">{{ row.item.at.slice(11, 19) }}</time>

          <!-- The actions, on the row itself and nothing else. There is no `⋯` any more: every one of them fits
               on the row at these label lengths, so a second place to reach them was a second place to look. -->
          <span class="row-actions" data-test="session-row-actions">
            <button
              v-if="restorable(row.item)"
              class="ghost row-action"
              data-test="session-row-restore"
              title="把这轮输入放回输入框"
              @click.stop="emit('restore', row.item.seq)"
            >
              复用
            </button>
            <button
              v-if="deletable(row.item)"
              class="ghost row-action"
              data-test="session-row-delete-inline"
              title="删除最新一次对话（不可恢复）"
              @click.stop="act(row)"
            >
              {{ armed?.seq === row.item.seq ? '确认删除' : '删除' }}
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
              title="撤回此处及之后的全部对话（发送新消息前可取消）"
              @click.stop="withdraw(row)"
            >
              撤回
            </button>
          </span>
        </div>

        <MarkdownBody :body="row.item.body" />
      </template>
    </li>
  </ul>
</template>

<style scoped>
/* Scoped through `.event-list` rather than a bare `li`, which would be `li[data-v-x]` — the same specificity
   as the global `.list > li` and therefore decided by which stylesheet happened to be injected last. Naming the
   list raises this to `.event-list > li[data-v-x]` and makes the override a fact rather than an ordering
   accident. */
.event-list > li {
  position: relative;
  /* Space, not rules. A hairline between two rows of one conversation reads as two separate documents, which
     is the opposite of what it was for. 12px above and below puts roughly one line of breathing room between
     turns while still fitting a dense log on one screen. */
  padding: var(--space-3) var(--space-4);
  border-block-end: none;
}

/* The head line is a flex row, so its children default to `min-width: auto` and a long source name would push
   the clock past the row's edge rather than being clipped (ui.md §2: nothing may make a horizontal scrollbar). */
.meta > * {
  min-inline-size: 0;
}

.source {
  /* Already at meta size and already faint; the slash used to separate it read as an even smaller thing than
     it is, so the divider is a rule rather than a character — and it is clipped rather than allowed to push
     the clock off the row (ui.md §2). */
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  padding-inline-start: var(--space-1);
  border-inline-start: 1px solid var(--line);
}

/* The body is the largest text in the app, and the only thing here anybody reads for longer than a glance.
   Scoped to a direct child so a tool card's result and a stretch of thinking keep their own size — a JSON
   payload at 15px is unreadable, not grand. */
li > .markdown {
  font-size: var(--fs-prose);
  line-height: var(--lh-prose);
  color: var(--text);
}

/* ---- the head line ----------------------------------------------------------------------------- */
.meta {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--fs-meta);
  line-height: var(--lh-meta);
  color: var(--text-3);
  margin-block-end: var(--space-1);
}

.role {
  color: var(--text-2);
  font-weight: 500;
}

.seq {
  font-family: var(--font-mono);
}

/* The clock is compared far more often than it is read, so the figures are tabular — otherwise two rows a
   second apart are a pixel apart in the digit and nobody can tell which is which. */
.at {
  margin-inline-start: auto;
  font-variant-numeric: tabular-nums;
}

/* ---- the role dot ------------------------------------------------------------------------------ */
.role-dot {
  flex: none;
  inline-size: 6px;
  block-size: 6px;
  border-radius: 50%;
  background: var(--text-3);
}

.role-dot[data-role='user'] {
  background: var(--accent);
}

.role-dot[data-role='assistant'] {
  background: var(--text-2);
}

.role-dot[data-role='system'] {
  background: var(--warn);
}

/* Tool and thinking rows never render this — they are their own component and carry their own stripe. */
.role-dot[data-role='summary'] {
  background: var(--ok);
}

/* ---- row actions ------------------------------------------------------------------------------- */
.row-actions {
  position: relative;
  flex: none;
  display: none;
  margin-inline-start: var(--space-1);
}

/* Shown on hover, and on focus-within, so a control only a pointer can reach is not a control at all. Kept in
   the flow rather than floated to the corner: floating it meant it covered the row's own text. */
li:hover > .meta > .row-actions,
li:focus-within > .meta > .row-actions,
/* …and while a confirmation is armed, for the reason given on the `is-armed` class above. */
li.is-armed > .meta > .row-actions {
  display: flex;
}

/* Short labels, so the buttons can live on the row without covering it. `--fs-meta` puts them at the size of
   the rest of the meta line — they are about one row, not the main event — and `nowrap` keeps 「确认删除」
   from breaking mid-phrase when it arms. */
.row-action {
  padding: 0 var(--space-2);
  font-size: var(--fs-meta);
  white-space: nowrap;
}

/* The way out of a confirmation is quieter than the confirmation itself: 取消 must be findable, not inviting. */
.row-action.is-quiet {
  color: var(--text-3);
  font-weight: 400;
}

/* ---- state ------------------------------------------------------------------------------------- */
.revision-note {
  margin-block-start: var(--space-1);
}

.streaming .meta {
  color: var(--accent);
}

/* The caret is the whole point of the streaming row: the text is still arriving. It also blinks, which is the
   one animation in the app that carries meaning rather than decoration — so under `prefers-reduced-motion` it
   is not removed, it stops moving. The glyph stays, because "the model is still writing" is a fact the row
   has to keep saying; only the pulse goes. */
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

@media (prefers-reduced-motion: reduce) {
  .streaming .meta::after {
    animation: none;
  }
}
</style>