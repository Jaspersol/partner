<script setup lang="ts">
import { useId } from 'vue'
import Icon from './Icon.vue'

/**
 * The mark that replaces a footnote.
 *
 * <para>
 * Every explanatory sentence this app used to print under a field or a card heading — 「留空表示不配置」,
 * 「关闭会立即释放麦克风设备」 — cost a line of height on every screen it appeared on, and a settings page with
 * twenty-seven of them is a page a reader has to scroll through twice: once past the prose to find the control,
 * and again to read the control beside whatever survived the first pass. The sentence is still worth having. It
 * is simply not worth *standing*. So it becomes a small round grey exclamation mark beside the thing it is
 * about, and the sentence appears when the reader asks for it.
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
 * It is a button because a hover-only affordance is not an affordance: `:focus-within` gives the keyboard the
 * same reveal the pointer gets. It carries no action, so activating it does nothing — and it stops the click
 * from reaching an enclosing `<label>`, which would otherwise move the caret into the field beside it.
 * </para>
 */
const props = withDefaults(
  defineProps<{
    /**
     * Which edge the pop is anchored to.
     *
     * <para>
     * `end` is for a mark that sits at the right-hand end of a row, where a pop opening to the right would run
     * off the edge of the panel it is in. Nothing measures anything: the caller knows where the mark is.
     * </para>
     */
    align?: 'start' | 'end' | undefined
  }>(),
  { align: 'start' },
)

/** Ties the button to the one sentence it opens. Per instance, so a page of them cannot cross-describe. */
const id = useId()
</script>

<template>
  <span class="hint-tip">
    <button
      type="button"
      class="hint-dot"
      aria-label="补充说明"
      :aria-describedby="id"
      @click.stop.prevent
    >
      <Icon name="info" :size="13" :stroke-width="2" />
    </button>
    <span :id="id" class="hint-pop" :class="`is-${props.align}`" role="tooltip">
      <slot />
    </span>
  </span>
</template>

<style scoped>
.hint-tip {
  position: relative;
  display: inline-block;
  /* `none` so the mark keeps its 13px in a flex row — the settings card headings are a flexed `<h2>` whose
     full-width toggle would otherwise squeeze the mark to nothing. */
  flex: none;
}

/* The circle is the glyph, so the button around it is nothing: no border, no background, no size of its own.
   Grey rather than the accent — this is a footnote, and a page of footnotes in the app's blue would pull the
   eye off the controls it is annotating. --text-3 measures 4.9:1 on both surfaces, which is what a non-text
   control needs. */
.hint-dot {
  padding: 0;
  border: none;
  background: none;
  color: var(--text-3);
  line-height: 0;
  vertical-align: -0.15em;
  cursor: help;
}

.hint-dot:hover {
  color: var(--text-2);
}

/* Hidden by opacity, deliberately: `visibility: hidden` would drop the sentence out of the accessibility tree,
   and `aria-describedby` above would then describe nothing. `pointer-events: none` keeps an invisible box from
   swallowing clicks meant for the field underneath. */
.hint-pop {
  position: absolute;
  inset-block-start: calc(100% + 0.35rem);
  inset-inline-start: -0.25rem;
  z-index: 30;
  inline-size: max-content;
  max-inline-size: 20rem;
  padding: var(--space-2) var(--space-3);
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  box-shadow: var(--shadow-3);
  color: var(--text-2);
  font-size: var(--fs-meta);
  line-height: var(--lh-meta);
  text-align: start;
  white-space: normal;
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--motion-fast) var(--ease);
}

.hint-tip:hover > .hint-pop,
.hint-tip:focus-within > .hint-pop {
  opacity: 1;
}

/* For a mark at the end of a row: the pop hangs off the right edge instead of running past it. */
.is-end {
  inset-inline-start: auto;
  inset-inline-end: -0.25rem;
}

/* The sentences with markup in them keep it. `:deep` because the content is the caller's, so it carries the
   caller's scope id rather than this component's. */
.hint-pop :deep(code) {
  padding: 0.1em 0.3em;
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  background: var(--surface-0);
  font-size: 0.92em;
}
</style>
