<script setup lang="ts">
import type { SessionItem } from '../api'
import MarkdownBody from './MarkdownBody.vue'

defineProps<{ items: SessionItem[]; selectedSeq: number | null }>()
const emit = defineEmits<{ select: [seq: number] }>()
</script>

<template>
  <ul class="list" data-test="session-list">
    <li
      v-for="item in items"
      :key="item.seq"
      :class="{ selected: item.seq === selectedSeq }"
      @click="emit('select', item.seq)"
    >
      <div class="meta">
        #{{ item.seq }} {{ item.kind }} / {{ item.source }} {{ item.at.slice(11, 19) }}
        <span v-if="item.revisionCount > 0" class="edited">已编辑 {{ item.revisionCount }}</span>
      </div>
      <MarkdownBody :body="item.body" />
      <!-- Always visible, never only in the inspector: an edit must be obvious from the list itself. -->
      <div v-if="item.revisionCount > 0" class="edited" data-test="session-revision-note">
        已编辑 {{ item.revisionCount }} 次 · 原文：{{ item.originalBody }}
      </div>
    </li>
  </ul>
</template>
