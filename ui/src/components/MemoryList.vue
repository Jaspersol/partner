<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { clampIndex, isActivationKey, rovingTarget } from '../rovingFocus'
import { scopeLabel, tierLabel, type MemoryRow } from '../memoryRow'

const props = defineProps<{ rows: MemoryRow[]; selectedId: string | null }>()
const emit = defineEmits<{ select: [row: MemoryRow] }>()

/** One tab stop for the list; the arrows move within it. See `rovingFocus.ts`. */
const focusIndex = ref(0)
const listElement = ref<HTMLElement | null>(null)

watch(
  () => props.rows.length,
  (count) => {
    focusIndex.value = clampIndex(focusIndex.value, count)
  },
)

function onKeydown(event: KeyboardEvent, index: number) {
  const target = rovingTarget(index, props.rows.length, event.key)
  if (target === null) {
    if (isActivationKey(event.key)) {
      event.preventDefault()
      const row = props.rows[index]
      if (row !== undefined) choose(row, index)
    }
    return
  }

  event.preventDefault()
  focusIndex.value = target
  void nextTick(() => {
    listElement.value?.querySelectorAll<HTMLElement>('[data-row]')[target]?.focus()
  })
}

function choose(row: MemoryRow, index: number): void {
  focusIndex.value = index
  emit('select', row)
}
</script>

<template>
  <ul ref="listElement" class="list" role="list" aria-label="记忆条目" data-test="memory-list">
    <li
      v-for="(row, index) in rows"
      :key="row.recordId"
      :class="{ selected: row.recordId === selectedId, retired: row.retired }"
      :data-tier="row.tier"
      :data-row="index"
      :tabindex="index === focusIndex ? 0 : -1"
      :aria-current="row.recordId === selectedId ? 'true' : undefined"
      @click="choose(row, index)"
      @focus="focusIndex = index"
      @keydown="onKeydown($event, index)"
    >
      <!-- Tier first and named, because "is this still true" and "how sure am I" are different questions. -->
      <div class="meta break">
        <span class="tier" data-test="memory-row-tier">{{ tierLabel(row.tier) }}</span>
        <span v-if="row.scope === 'Retain'" class="retain" data-test="memory-row-retain">{{ scopeLabel(row.scope) }}</span>
        <span class="id">{{ row.recordId }}</span>
      </div>
      <div class="break" :class="{ dim: row.retired }">{{ row.body }}</div>
      <!-- Validity belongs in the list, not only in the inspector (ui.md §5.5). -->
      <div class="validity break" :class="{ edited: row.retired }">{{ row.validity }}</div>
    </li>
  </ul>
</template>

<style scoped>
.tier {
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  padding: 0 var(--space-1);
  color: var(--text-3);
}

.id {
  color: var(--text-2);
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
}

/* Retention is a promise, not a rank, so it is outlined rather than filled: a filled badge next to the tier would
   read as "a higher tier" rather than "this one will not be forgotten". */
.retain {
  border: 1px solid var(--accent);
  border-radius: var(--radius-sm);
  padding: 0 var(--space-1);
  color: var(--accent);
}

/* A retired record stays listed — "fully visible" means being able to see what replaced it — but it must not
   read as live, so the text recedes rather than the row disappearing. */
li.retired {
  opacity: 0.72;
}

.dim {
  text-decoration: line-through;
  text-decoration-color: var(--line);
}
</style>
