<script setup lang="ts">
import { computed } from 'vue'
import { iconPaths, type IconName } from '../icons'

/**
 * One icon.
 *
 * Stroke-based and `currentColor`, so it takes the colour of whatever it sits in.
 *
 * Decorative by default: `aria-hidden`, because every icon here sits beside or inside a control that already
 * carries the name in text. Passing `label` makes the icon the accessible name.
 */
const props = withDefaults(
  defineProps<{
    name: IconName
    /** Rendered size in pixels. Icons are drawn on a 24-unit grid, so this is a scale. */
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
   elsewhere: an SVG is a flex item by default and will shrink to nothing inside a row. */
.icon {
  display: inline-block;
  flex: none;
  vertical-align: -0.15em;
}
</style>