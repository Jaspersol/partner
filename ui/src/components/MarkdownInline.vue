<script lang="ts">
export default { name: 'MarkdownInline' }
</script>

<script setup lang="ts">
/**
 * One inline run, rendered. Emphasis can contain anything emphasis can contain, so the recursion lives here
 * instead of being open-coded at each of the five block sites that render runs.
 */
import type { Inline } from './markdownTypes'

defineProps<{ run: Inline }>()
</script>

<template>
  <code v-if="run.kind === 'code'">{{ run.value }}</code>
  <strong v-else-if="run.kind === 'strong'">
    <MarkdownInline v-for="(inner, index) in run.runs" :key="index" :run="inner" />
  </strong>
  <em v-else-if="run.kind === 'em'">
    <MarkdownInline v-for="(inner, index) in run.runs" :key="index" :run="inner" />
  </em>
  <template v-else>{{ run.value }}</template>
</template>
