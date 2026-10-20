<script lang="ts">
export default { name: 'MarkdownInline' }
</script>

<script setup lang="ts">
/**
 * One inline run, rendered. Recursion lives here rather than being open-coded at each block site that renders
 * runs, because emphasis can contain anything emphasis can contain.
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
