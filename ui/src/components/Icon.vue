<script setup lang="ts">
import { computed } from 'vue'
import { iconPaths, type IconName } from '../icons'

/**
 * One icon.
 *
 * <para>
 * Stroke-based and `currentColor`, so it takes the colour of whatever it sits in and cannot end up a
 * different weight from the text beside it — which was the whole problem with the Unicode glyphs this
 * replaced.
 * </para>
 *
 * <para>
 * Decorative by default: `aria-hidden`, because every icon here sits beside or inside a control that already
 * carries the name in text. Passing `label` is for the case where it does not, and that is the only thing
 * that changes about the accessibility of this element.
 * </para>
 */
const props = withDefaults(
  defineProps<{
    name: IconName
    /** Rendered size in pixels. Icons are drawn on a 24-unit grid, so this is a scale, not a design. */
    size?: number
    /** Makes the icon the accessible name. Omit it wherever text already says what this is. */
    label?: string | undefined
    strokeWidth?: number
  }>(),
  { size: 16, label: undefined, strokeWidth: 1.75 },
)

const paths = computed(() => iconPaths(props.name))
</script>

<template>
  <svg
    class="icon"
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    :stroke-width="strokeWidth"
    stroke-linecap="round"
    stroke-linejoin="round"
    :aria-hidden="label ? undefined : 'true'"
    :aria-label="label"
    :role="label ? 'img' : undefined"
    :data-icon="name"
  >
    <path v-for="(d, index) in paths" :key="index" :d="d" />
  </svg>
</template>

<style scoped>
/* Inline by default so it sits on the text baseline inside a button or a line of prose. `flex: none`
   everywhere else: an SVG is a flex item by default and will happily shrink to nothing inside a row. */
.icon {
  display: inline-block;
  flex: none;
  vertical-align: -0.15em;
}
</style>