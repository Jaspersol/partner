<script setup lang="ts">
import type { MemoryRow } from '../memoryRow'

defineProps<{ rows: MemoryRow[]; selectedId: string | null }>()
const emit = defineEmits<{ select: [row: MemoryRow] }>()
</script>

<template>
  <ul class="list" data-test="memory-list">
    <li
      v-for="row in rows"
      :key="row.recordId"
      :class="{ selected: row.recordId === selectedId }"
      @click="emit('select', row)"
    >
      <div class="meta break">{{ row.recordId }} · {{ row.tier }} / {{ row.scope }}</div>
      <div class="break">{{ row.body }}</div>
      <!-- Validity belongs in the list, not only in the inspector (ui.md §5.5). -->
      <div class="validity break" :class="{ edited: row.supersededBy !== null }">{{ row.validity }}</div>
    </li>
  </ul>
</template>
