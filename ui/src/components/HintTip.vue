<script setup lang="ts">
import { t } from '../i18n'
import { ref, useId } from 'vue'
import Icon from './Icon.vue'

/**
 * The mark that replaces a footnote.
 *
 * <para>
 * An explanatory sentence under a field or a card heading — 「留空表示不配置」, 「关闭会立即释放麦克风设备」
 * — costs a line of height on every screen it appears on, and a settings page with twenty-seven of them is a
 * page a reader has to scroll through twice: once past the prose to find the control, and again to read the
 * control beside whatever survived the first pass. The sentence is still worth having. It is simply not worth
 * *standing*. So it becomes a small round grey exclamation mark beside the thing it is about, and the sentence
 * appears when the reader asks for it.
 * </para>
 *
 * <para>
 * The hint is a real element in the document, not a `title` attribute: it keeps `<strong>` and `<code>` in the
 * sentences that have them, it appears the moment the pointer arrives instead of after the platform's
 * tooltip delay, and it is what `aria-describedby` on the button points at, so a screen reader reads it with the
 * field rather than losing it. Which is why the pop is hidden with `opacity` and not `visibility` — the latter
 * would take it out of the accessibility tree and leave the description pointing at nothing.
 * </para>
 *
 * <para>
 * It is deliberately *not* a `<button>`. The mark sits inside the row's `<label>`, and a label forwards a click
 * anywhere on the row to its first labelable descendant: with a button there, clicking the row's name or its
 * empty space "clicked" the mark and opened the sentence, and the field itself never got the click. A focusable
 * `role="img"` span is not labelable, so the label reaches the real field. The sentence opens on hover and on
 * keyboard focus, and nothing else: a click on the mark does nothing.
 * </para>
 */
const props = withDefaults(
  defineProps<{
    /**
     * Which edge the pop is anchored to. A caller that knows its mark sits at the right-hand end of a row can
     * say so; a mark that is wrong about it is corrected at open time (see `place`).
     */
    align?: 'start' | 'end' | undefined
  }>(),
  { align: 'start' },
)

/** Ties the button to the one sentence it opens. Per instance, so a page of them cannot cross-describe. */
const id = useId()

const root = ref<HTMLElement | null>(null)
const pop = ref<HTMLElement | null>(null)
const flipEnd = ref(props.align === 'end')
const flipUp = ref(false)

/**
 * Keeps the pop inside the window. Measured when it is about to show rather than on every resize, because it
 * only matters while it is visible and it is laid out (just transparent) the whole time.
 */
function place(): void {
  const dot = root.value
  const box = pop.value
  if (dot === null || box === null) return

  const anchor = dot.getBoundingClientRect()
  const width = box.offsetWidth
  const height = box.offsetHeight
  const margin = 8
  const wantEnd = props.align === 'end'
  // The pop hangs 0.5rem past the mark on whichever side it is anchored to.
  const startOverflows = anchor.left - 8 + width > window.innerWidth - margin
  const endOverflows = anchor.right + 8 - width < margin

  flipEnd.value = wantEnd ? !endOverflows : startOverflows && !endOverflows
  flipUp.value = anchor.bottom + height + margin > window.innerHeight && anchor.top - height - margin > 0
}

</script>

<template>
  <span ref="root" class="hint-tip" @pointerenter="place" @focusin="place">
    <span
      class="hint-dot"
      role="img"
      tabindex="0"
      :aria-label="t('补充说明')"
      :aria-describedby="id"
      @click.stop.prevent
    >
      <Icon name="info" :size="14" :stroke-width="2" />
    </span>
    <span
      :id="id"
      ref="pop"
      class="hint-pop g-float"
      :class="{ 'is-end': flipEnd, 'is-up': flipUp }"
      role="tooltip"
    >
      <slot />
    </span>
  </span>
</template>

<style scoped>
.hint-tip {
  position: relative;
  display: inline-block;
  /* `none` so the mark keeps its size in a flex row. */
  flex: none;
}

/* The circle is the glyph, so the button around it is nothing. Grey rather than the accent: this is a
   footnote, and a page of footnotes in the brand colour would pull the eye off the controls they annotate. */
.hint-dot {
  display: inline-block;
  border-radius: 50%;
  color: var(--text-3);
  line-height: 0;
  vertical-align: -0.2em;
  cursor: help;
}

.hint-dot:hover {
  color: var(--text);
}

/* Hidden by opacity, deliberately: `visibility: hidden` would take the sentence out of the accessibility tree
   and `aria-describedby` would then describe nothing. `pointer-events: none` keeps an invisible box from
   swallowing clicks meant for the field underneath. */
.hint-pop {
  position: absolute;
  inset-block-start: calc(100% + 0.4rem);
  inset-inline-start: -0.5rem;
  z-index: 30;
  inline-size: max-content;
  max-inline-size: 18rem;
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-sm);
  color: var(--text-2);
  font-size: var(--fs-meta);
  font-weight: 400;
  line-height: 1.6;
  text-align: start;
  white-space: normal;
  opacity: 0;
  transform: translateY(-4px);
  pointer-events: none;
  transition:
    opacity var(--motion-fast) var(--ease),
    transform var(--motion-fast) var(--ease);
}

.hint-tip:hover > .hint-pop,
.hint-tip:has(> .hint-dot:focus-visible) > .hint-pop {
  opacity: 1;
  transform: none;
}

/* Flipped so it stays inside the window: to the right-hand edge, and/or above the mark. */
.is-end {
  inset-inline-start: auto;
  inset-inline-end: -0.5rem;
}

.is-up {
  inset-block-start: auto;
  inset-block-end: calc(100% + 0.4rem);
  transform: translateY(4px);
}

.hint-pop :deep(code) {
  padding: 0.1em 0.35em;
  border-radius: 6px;
  background: var(--surface-2);
  font-size: 0.92em;
}
</style>
