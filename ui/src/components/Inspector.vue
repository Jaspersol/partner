<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

/**
 * The shared right-hand panel for session events and memory records (ui.md §5.3). The two differ only in
 * wording, the revision rows and the optional destructive action, so the mechanism lives here once.
 *
 * `testPrefix` keeps the existing `session-*` / `memory-*` hooks that the frontend tests and the smoke rely
 * on, without deriving them from which buttons happen to be present.
 */
const props = defineProps<{
  /**
   * Optional so the two existing callers stay unchanged. Present when a toolbar button toggles this panel,
   * which is what lets that button point `aria-controls` at it.
   */
  id?: string | undefined
  testPrefix: 'session' | 'memory'
  title: string
  target: string
  draft: string
  revisions: { at: string; actor?: string; body: string }[]
  saveLabel?: string
  /** Present only where deleting is meaningful (memory). */
  deleteLabel?: string
  /** Shown above the editor when the selection itself carries a note (an edited event, a superseded record). */
  note?: string
  noteTest?: string
  /**
   * Whether the panel may be dragged wider. False when it is a floating overlay on a narrow window: there is
   * nothing beside it to take width from, and a handle that cannot do anything is worse than no handle.
   */
  resizable?: boolean
}>()

const emit = defineEmits<{
  save: [body: string]
  remove: []
  /** Removes this event only. */
  delete: []
  /** Removes this event and everything after it — destructive, so it needs a second click. */
  retract: []
}>()

/** Inline two-step confirmation: a native dialog would block the webview and cannot be driven in tests. */
const confirmingRetract = ref(false)
const confirmingDelete = ref(false)

/**
 * What is said when a destructive action arms.
 *
 * <para>
 * Arming replaces one set of buttons with another, and a screen reader is not told: the button that said
 * 「删除此条」 is gone and a different one appeared. Silence there makes the second press look broken.
 * </para>
 */
const armAnnouncement = computed(() => {
  if (confirmingDelete.value) return '已准备删除此条。再次按下确认，这不会撤销。'
  if (confirmingRetract.value) return '已准备删除此条及之后的所有事件。再次按下确认，这不会撤销。'
  return ''
})

const draft = ref(props.draft)

// ---- width ---------------------------------------------------------------------------------------
/**
 * How wide the panel may be, in rem, and why those bounds.
 *
 * <para>
 * `MIN_REM` is the same 16rem as the floor in `.split`'s `minmax(16rem, 0.42fr)`: dragging to exactly the
 * floor should look identical to not having dragged at all. They are written twice because a CSS floor and a
 * keyboard step cannot be one value — a custom property would have to be a length, and the default is a
 * fraction — so the pair is kept honest by being adjacent in meaning rather than by being shared.
 * </para>
 *
 * <para>
 * `MAX_REM` is the only ceiling there is. Nothing above the default's fraction refuses a wider panel, because a
 * drag writes the custom property directly and replaces the whole track: a limit that only existed in a clamp
 * would be bypassed rather than enforced.
 * </para>
 *
 * <para>
 * 26rem is where the panel stops being a detail panel and starts eating the log, which is the thing the
 * reader came for.
 * </para>
 */
const MIN_REM = 16
const MAX_REM = 26
const KEY_PREFIX = 'partner.inspector.'

const storageKey = `${KEY_PREFIX}${props.testPrefix}`
const widthRem = ref<number | null>(null)

function readStoredWidth(): number | null {
  try {
    const raw = window.localStorage.getItem(storageKey)
    if (raw === null) return null
    const parsed = Number(raw)
    return Number.isFinite(parsed) && parsed >= MIN_REM && parsed <= MAX_REM ? parsed : null
  } catch {
    // A sandboxed context without storage still gets a draggable panel, just one that forgets.
    return null
  }
}

function writeStoredWidth(rem: number): void {
  try {
    window.localStorage.setItem(storageKey, String(rem))
  } catch {
    // As above: failing to remember is survivable, failing to drag is not, so this is the only thing dropped.
  }
}

/**
 * Applies the width to the grid track above this panel.
 *
 * <para>
 * Written as a custom property on the `.split` ancestor rather than as an inline width here, because the panel
 * is a grid *item*: setting its own width would leave the track at its old value and the panel would either be
 * clipped or overflow. The track is what the reader is actually dragging.
 * </para>
 */
function applyWidth(rem: number): void {
  const host = panel.value?.closest('.split') as HTMLElement | null
  host?.style.setProperty('--inspector', `${rem}rem`)
}

const panel = ref<HTMLElement | null>(null)

let dragStart: { clientX: number; trackPx: number } | null = null

function clampTo(rem: number): number {
  return Math.min(MAX_REM, Math.max(MIN_REM, Math.round(rem)))
}

/** Pixels per rem, which is what a drag in pixels has to be divided by. */
function pixelsPerRem(): number {
  const size = Number.parseFloat(getComputedStyle(document.documentElement).fontSize)
  return Number.isFinite(size) && size > 0 ? size : 16
}

function track(): HTMLElement | null {
  return panel.value?.closest('.split') ?? null
}

function onPointerDown(event: PointerEvent): void {
  const host = track()
  if (!props.resizable || host === null) return

  event.preventDefault()
  // The reference is the track's width at the moment of the press, not the pointer's absolute position: the
  // panel may already have been dragged, so subtracting clientX from the current width would jump it to a
  // different size on the first move.
  dragStart = { clientX: event.clientX, trackPx: host.getBoundingClientRect().width }
  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', onPointerUp)
}

function onPointerMove(event: PointerEvent): void {
  if (dragStart === null) return

  const px = dragStart.trackPx + (event.clientX - dragStart.clientX)
  widthRem.value = clampTo(px / pixelsPerRem())
  applyWidth(widthRem.value)
}

function onPointerUp(): void {
  if (dragStart === null) return

  dragStart = null
  window.removeEventListener('pointermove', onPointerMove)
  window.removeEventListener('pointerup', onPointerUp)
  if (widthRem.value !== null) writeStoredWidth(widthRem.value)
}

/**
 * Keyboard equivalent.
 *
 * <para>
 * `role="separator"` with the value attributes is the ARIA pattern for a draggable edge, and arrow keys are
 * the only way a reader who cannot use a pointer resizes anything. 1rem per press, 4rem with shift, which is
 * about one character of width and a phrase.
 * </para>
 */
function onResizerKeydown(event: KeyboardEvent): void {
  const step = event.shiftKey ? 4 : 1
  let next: number | null = null
  if (event.key === 'ArrowLeft') next = clampTo((widthRem.value ?? 20) + step)
  else if (event.key === 'ArrowRight') next = clampTo((widthRem.value ?? 20) - step)
  if (next === null) return

  event.preventDefault()
  widthRem.value = next
  applyWidth(next)
  writeStoredWidth(next)
}

onMounted(() => {
  const stored = readStoredWidth()
  if (stored !== null && props.resizable === true) {
    widthRem.value = stored
    applyWidth(stored)
  }
})

onBeforeUnmount(() => {
  // Listeners are on `window`, not on this element: a drag that ends by releasing outside the panel would
  // otherwise leave them attached for the lifetime of the app, and every later pointer move would resize a
  // panel that is no longer there.
  window.removeEventListener('pointermove', onPointerMove)
  window.removeEventListener('pointerup', onPointerUp)
})

// The panel is a projection of the selection, so the editor follows it rather than owning a copy.
watch(
  () => props.draft,
  (value) => {
    draft.value = value
  },
)
</script>

<template>
  <aside
    :id="id"
    ref="panel"
    class="inspector"
    :class="{ 'is-resizable': resizable === true }"
    :aria-label="title"
    data-test="inspector"
  >
    <!--
      A real focusable handle, not the `::before` decoration: `col-resize` is a pointer affordance and a
      keyboard reader has no pointer. `role="separator"` with `aria-valuenow/min/max` is the ARIA pattern for a
      draggable edge, and the arrow keys below are the only way anyone resizes without a mouse.
    -->
    <div
      v-if="resizable === true"
      class="inspector-resizer"
      role="separator"
      tabindex="0"
      aria-orientation="vertical"
      aria-label="调整检视面板宽度"
      :aria-valuenow="widthRem ?? undefined"
      :aria-valuemin="MIN_REM"
      :aria-valuemax="MAX_REM"
      data-test="inspector-resizer"
      @pointerdown="onPointerDown"
      @keydown="onResizerKeydown"
    />

    <div class="inspector-head">
      <div>
        <h2>{{ title }}</h2>
        <div class="meta break" :data-test="`${testPrefix}-target`">{{ target }}</div>
        <div v-if="note" class="edited" :data-test="noteTest">{{ note }}</div>
      </div>
    </div>

    <div class="inspector-body">
      <!-- Always on the page, so the change is announced when it happens rather than when the node appears. -->
      <span class="sr-only" role="status" aria-live="polite" data-test="inspector-arm-announcer">{{ armAnnouncement }}</span>

      <textarea v-model="draft" rows="6" :data-test="`${testPrefix}-draft`" />

      <div class="inspector-actions">
        <button class="primary" :data-test="`${testPrefix}-save`" @click="emit('save', draft.trim())">
          {{ saveLabel ?? '保存' }}
        </button>
        <button v-if="deleteLabel" class="danger" :data-test="`${testPrefix}-delete`" @click="emit('remove')">
          {{ deleteLabel }}
        </button>
      </div>

      <h3>历史</h3>
      <!-- Both removals are destructive and neither is reversible: the text leaves the log, and the tombstone
           left behind records the range, not the words. Two clicks each, and each confirms only itself. -->
      <div v-if="testPrefix === 'session'" class="inspector-actions">
        <template v-if="confirmingDelete">
          <button class="danger" data-test="session-delete-confirm" @click="emit('delete')">确认删除此条</button>
          <button class="ghost" data-test="session-delete-cancel" @click="confirmingDelete = false">取消</button>
        </template>
        <template v-else-if="confirmingRetract">
          <button class="danger" data-test="session-retract-confirm" @click="emit('retract')">
            确认撤回（含此条之后全部）
          </button>
          <button class="ghost" data-test="session-retract-cancel" @click="confirmingRetract = false">取消</button>
        </template>
        <template v-else>
          <button class="ghost" data-test="session-delete" @click="confirmingDelete = true">删除此条</button>
          <button class="ghost danger" data-test="session-retract" @click="confirmingRetract = true">
            撤回此条及之后
          </button>
        </template>
      </div>

      <h3>修订历史</h3>
      <ul class="list" :data-test="`${testPrefix}-revisions`">
        <li v-for="(revision, index) in revisions" :key="index">
          <div class="meta">
            {{ revision.at.slice(11, 19) }}
            <span v-if="revision.actor"> · {{ revision.actor }}</span>
          </div>
          <div class="break">{{ revision.body }}</div>
          <!-- Undo needs no special mechanism: restoring appends another edit pointing at the old text. -->
          <button class="ghost" data-test="inspector-restore" @click="draft = revision.body">
            恢复此版本
          </button>
        </li>
      </ul>
      <p v-if="revisions.length === 0" class="muted">（无修订）</p>
    </div>
  </aside>
</template>
