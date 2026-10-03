<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import type { SessionItem } from '../api'
import { clampIndex, isActivationKey, normalizeKey, rovingTarget } from '../rovingFocus'
import { sessionKindLabel, sessionRole } from '../sessionKind'
import Icon from './Icon.vue'
import MarkdownBody from './MarkdownBody.vue'
import ToolCard from './ToolCard.vue'
import ThinkingRow from './ThinkingRow.vue'

const props = defineProps<{
  items: SessionItem[]
  selectedSeq: number | null
  /**
   * Results only: keep the conversation, fold everything the reader did not ask for.
   *
   * <para>
   * Tool cards are already collapsed, but "collapsed" still leaves one line per call, and a turn that ran ten
   * of them is ten lines between two sentences. This drops them entirely — the conversation is what the
   * person came to read, and the calls are still in the log and in the queue view.
   * </para>
   */
  resultsOnly?: boolean | undefined
}>()
const emit = defineEmits<{
  select: [seq: number]
  /** Removes this event only. The view decides what that means for the session. */
  remove: [seq: number]
  /** Removes this event and everything after it — destructive, so the view asks twice. */
  retract: [seq: number]
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

// Results only keeps the conversation: the reader came for what was said, and the calls and the thinking are
// both still in the log behind the switch.
const visible = computed(() =>
  props.resultsOnly === true ? rows.value.filter((row) => row.kind === 'event') : rows.value,
)

/**
 * A failure is a `System` event whose body starts with "error: " — that prefix is written by the turn runner
 * (AgentTurnRunner.cs), so it is a contract rather than a phrase to match loosely.
 */
function isError(item: SessionItem): boolean {
  return item.kind === 'System' && item.body.startsWith('error: ')
}

/**
 * Only the reader's own typed messages can be put back in the box. An assistant reply restored there would be
 * sent as the human's words, and a scheduled task's text is not theirs to resend either.
 */
function restorable(item: SessionItem): boolean {
  return item.kind === 'User' && item.source.toLowerCase() === 'text' && item.streaming !== true
}

/** Inline two-step confirmation: a native dialog would block the webview and cannot be driven in tests. */
const armed = ref<{ seq: number; action: 'delete' | 'retract' } | null>(null)

/**
 * What is said when an action arms.
 *
 * <para>
 * Arming is invisible to a screen reader: the button that was 「删除此条」 becomes 「确认删除此条」, and a control
 * whose accessible name changed under the cursor is not announced. Without this, the confirmation reads as a
 * button that simply stopped responding the second time.
 * </para>
 *
 * <para>
 * It says what will happen <b>and</b> that a second press is needed, because the whole point of arming is that
 * the destructive thing has not happened yet.
 * </para>
 */
const armAnnouncement = computed(() => {
  const state = armed.value
  if (state === null) return ''
  return state.action === 'delete'
    ? '已准备删除此条。再次按 Enter 确认，这不会撤销。'
    : '已准备删除此条及之后的所有事件。再次按 Enter 确认，这不会撤销。'
})

/**
 * Which row has its menu open, by seq.
 *
 * <para>
 * One at a time. A screen of twenty open menus is a screen of noise, and the rows below the open one would all
 * show the same three actions floating over different bodies of text.
 * </para>
 */
const menuFor = ref<number | null>(null)

function closeMenu() {
  menuFor.value = null
  armed.value = null
}

function toggleMenu(seq: number) {
  // Re-opening the same row starts clean rather than from whatever was armed last time — otherwise a reader
  // who closed a menu over "delete this and everything after it" and reopened it a minute later would be
  // one press from a retraction they had stopped intending.
  if (menuFor.value === seq) {
    closeMenu()
    return
  }

  menuFor.value = seq
  armed.value = null
}

/** Escape closes the menu, which is what a keyboard reader will try first. */
function onListEscape(event: KeyboardEvent) {
  if (normalizeKey(event.key) === 'escape' && menuFor.value !== null) {
    event.stopPropagation()
    closeMenu()
  }
}

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

/**
 * Clicking away closes the menu.
 *
 * Bound only while a menu is open. A permanent document listener would run on every click in the app to serve
 * a menu that is almost never open, and a list of a few hundred rows makes that a cost nobody notices until
 * it is there.
 */
const onDocumentClick = (event: MouseEvent) => {
  if (menuFor.value === null) return
  const target = event.target
  if (target instanceof Element && target.closest('.row-actions') !== null) return
  closeMenu()
}

watch(menuFor, (open) => {
  if (open === null) {
    document.removeEventListener('click', onDocumentClick)
    return
  }

  document.addEventListener('click', onDocumentClick)
})

onUnmounted(() => {
  document.removeEventListener('click', onDocumentClick)
})

function act(row: Row, action: 'delete' | 'retract') {
  // Nothing to remove by sequence from a card or a stretch of thinking: those are folded views over events,
  // and a reader who wants one gone removes the event behind it from its own row.
  if (row.kind !== 'event') {
    return
  }

  const seq = row.item.seq

  // Arming is per row and per action: a second press on the *same* item is the confirmation, and a press on
  // the other item is not — otherwise "删除" would arm "删除此处及之后" by accident, and those two remove very
  // different amounts of history.
  if (armed.value === null || armed.value.seq !== seq || armed.value.action !== action) {
    armed.value = { seq, action }
    return
  }

  armed.value = null
  closeMenu()
  if (action === 'delete') {
    emit('remove', seq)
  } else {
    emit('retract', seq)
  }
}

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
watch(visible, (rows) => {
  focusIndex.value = clampIndex(focusIndex.value, rows.length)
})

function onListKeydown(event: KeyboardEvent, index: number) {
  // Escape first: it is the key a keyboard reader reaches for when a menu is open, and it must not be eaten
  // by the arrow handling below — or by the row's own activation keys.
  if (normalizeKey(event.key) === 'escape' && menuFor.value !== null) {
    event.stopPropagation()
    closeMenu()
    return
  }

  const target = rovingTarget(index, visible.value.length, event.key)
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
  const row = visible.value[index]
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
    @keydown="onListEscape"
  >
    <!--
      The armed-state announcement. A separate node rather than `aria-live` on the menu, because the menu is
      itself being opened and closed by these very actions — a live region that appears at the moment its
      content changes is announced unreliably, so it has to already be on the page when the change happens.
    -->
    <span class="sr-only" role="status" aria-live="polite" data-test="session-arm-announcer">{{ armAnnouncement }}</span>
    <li
      v-for="(row, index) in visible"
      :key="row.kind === 'tool' ? `tool-${row.seq}` : row.kind === 'thinking' ? row.key : row.item.streaming ? 'streaming' : row.item.seq"
      :class="{
        selected: row.kind === 'event' && row.item.seq === selectedSeq,
        streaming: row.kind === 'event' && row.item.streaming === true,
        'is-error': row.kind === 'event' && isError(row.item),
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
          <template v-if="row.item.streaming">
            <span class="role" data-test="session-streaming" aria-live="polite">正在回复…</span>
          </template>
          <template v-else>
            <span v-if="isError(row.item)" class="error-mark" data-test="session-error-mark">错误</span>
            <!-- The row says what the event *is*; `data-kind` on the <li> keeps the wire value for tests and
                 for anyone reading the markup. Both are deliberate — the word is for the reader, the enum for us. -->
            <span class="role">{{ sessionKindLabel(row.item.kind) }}</span>
            <span class="seq">#{{ row.item.seq }}</span>
            <span v-if="row.item.source" class="source">{{ row.item.source }}</span>
            <span v-if="row.item.revisionCount > 0" class="edited">已编辑 {{ row.item.revisionCount }}</span>

            <!-- Pushed to the far end with `margin-inline-start: auto`, and tabular so a column of them lines
                 up: the clock is read by comparison far more often than by reading. -->
            <time class="at" :datetime="row.item.at">{{ row.item.at.slice(11, 19) }}</time>

            <!-- The actions, back on the row itself. They went behind the `⋯` below for one reason: the long
                 labels used to float over the row and cover its own text. Short labels remove that reason —
                 「删除」 is two characters where 「删除此处及之后」 was seven — so the common case costs no extra
                 click again. What each action actually *does* is still written out one hover away, on the `⋯`. -->
            <span class="row-actions" data-test="session-row-actions">
              <button
                v-if="restorable(row.item)"
                class="ghost row-action"
                data-test="session-row-restore"
                title="把这轮输入放回输入框"
                @click.stop="emit('restore', row.item.seq); closeMenu()"
              >
                撤回
              </button>
              <button
                class="ghost row-action"
                data-test="session-row-delete-inline"
                title="删除此条"
                @click.stop="act(row, 'delete')"
              >
                {{ armed?.seq === row.item.seq && armed.action === 'delete' ? '确认删除' : '删除' }}
              </button>
              <button
                class="ghost row-action"
                :class="{ danger: armed?.seq === row.item.seq && armed.action === 'retract' }"
                data-test="session-row-retract-inline"
                title="删除此条及之后的全部事件"
                @click.stop="act(row, 'retract')"
              >
                {{ armed?.seq === row.item.seq && armed.action === 'retract' ? '确认删除之后' : '删除之后' }}
              </button>
              <button
                class="ghost menu-toggle"
                data-test="session-row-menu"
                :aria-expanded="menuFor === row.item.seq"
                aria-haspopup="menu"
                :aria-label="`#${row.item.seq} 的操作`"
                @click.stop="toggleMenu(row.item.seq)"
              >
                <Icon name="more" :size="14" />
              </button>

              <div
                v-if="menuFor === row.item.seq"
                class="row-menu"
                role="menu"
                :aria-label="`#${row.item.seq} 的操作`"
                @click.stop
              >
                <button
                  v-if="restorable(row.item)"
                  class="menu-item"
                  role="menuitem"
                  data-test="session-restore"
                  @click="emit('restore', row.item.seq); closeMenu()"
                >
                  撤回
                  <span class="hint">放回输入框</span>
                </button>
                <button
                  class="menu-item"
                  role="menuitem"
                  data-test="session-row-delete"
                  @click="act(row, 'delete')"
                >
                  <template v-if="armed?.seq === row.item.seq && armed.action === 'delete'">确认删除此条</template>
                  <template v-else>删除此条</template>
                  <span v-if="armed?.seq !== row.item.seq || armed.action !== 'delete'" class="hint">不可撤销</span>
                </button>
                <button
                  class="menu-item is-danger"
                  role="menuitem"
                  data-test="session-row-retract"
                  @click="act(row, 'retract')"
                >
                  <template v-if="armed?.seq === row.item.seq && armed.action === 'retract'">
                    确认删除此处及之后
                  </template>
                  <template v-else>删除此处及之后</template>
                  <span
                    v-if="armed?.seq !== row.item.seq || armed.action !== 'retract'"
                    class="hint"
                    >含本条之后全部</span
                  >
                </button>
                <button
                  v-if="armed?.seq === row.item.seq"
                  class="menu-item is-quiet"
                  role="menuitem"
                  data-test="session-row-cancel"
                  @click="cancelArm"
                >
                  取消
                </button>
              </div>
            </span>
          </template>
        </div>

        <MarkdownBody :body="row.item.body" />
        <!-- Always visible, never only in the inspector: an edit must be obvious from the list itself. -->
        <div v-if="row.item.revisionCount > 0" class="edited revision-note" data-test="session-revision-note">
          已编辑 {{ row.item.revisionCount }} 次 · 原文：{{ row.item.originalBody }}
        </div>
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

.event-list > li.is-error .role-dot {
  background: var(--error);
}

.event-list > li.is-error {
  border-inline-start: 2px solid var(--error);
  padding-inline-start: calc(var(--space-4) - 2px);
}

.error-mark {
  color: var(--error);
  border: 1px solid var(--error);
  border-radius: var(--radius-sm);
  padding: 0 var(--space-1);
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
   the rest of the meta line — they are about one row, not the main event — and `nowrap` keeps 「确认删除之后」
   from breaking mid-phrase when it arms. */
.row-action {
  padding: 0 var(--space-2);
  font-size: var(--fs-meta);
  white-space: nowrap;
}

.menu-toggle {
  padding: 0 var(--space-1);
  line-height: 1;
}

.row-menu {
  position: absolute;
  inset-block-start: calc(100% + var(--space-1));
  inset-inline-end: 0;
  z-index: 2;
  display: grid;
  min-inline-size: 13rem;
  padding: var(--space-1);
  gap: 1px;
  /* The one thing in this app that genuinely leaves the page, so it gets a real shadow rather than a
     border — and its own surface step so it does not read as part of the row underneath. */
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  background: var(--surface-2);
  box-shadow: var(--shadow-3);
}

.menu-item {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-2) var(--space-2);
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--text);
  font-size: var(--fs-ui);
  text-align: start;
  cursor: pointer;
}

.menu-item:hover {
  background: var(--surface-1);
}

/* The second, fuller label — "确认删除此处及之后" against "删除此处及之后" — is what makes the arming
   readable. Losing the "确认" would turn the confirmation into a trap. */
.menu-item.is-danger {
  color: var(--error);
}

.menu-item.is-danger:hover {
  background: color-mix(in srgb, var(--error) 12%, transparent);
}

.menu-item.is-quiet {
  color: var(--text-3);
  font-weight: 400;
}

.hint {
  color: var(--text-3);
  font-size: var(--fs-meta);
  white-space: nowrap;
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