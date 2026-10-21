<script setup lang="ts">
import { t } from '../i18n'
import { nextTick, ref, watch } from 'vue'
import { clampIndex, isActivationKey, rovingTarget } from '../rovingFocus'
import { scopeLabel, tierShort, type MemoryRow } from '../memoryRow'
import HintTip from './HintTip.vue'
import Icon from './Icon.vue'

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
  <!--
    A card per memory: the sentence first and large, then one quiet line — which kind, whether it is pinned, and when it
    was last used. Everything else a row used to carry (the id, who edited it, the raw validity string) is behind the ⓘ
    on the card: it is for the person debugging a conflict, not for the person reading what is remembered.
  -->
  <ul ref="listElement" class="memory-list" role="list" :aria-label="t('记忆条目')" data-test="memory-list">
    <li
      v-for="(row, index) in rows"
      :key="row.recordId"
      class="memory-card"
      :class="{ selected: row.recordId === selectedId, retired: row.retired }"
      :data-tier="row.tier"
      :data-row="index"
      :tabindex="index === focusIndex ? 0 : -1"
      :aria-current="row.recordId === selectedId ? 'true' : undefined"
      @click="choose(row, index)"
      @focus="focusIndex = index"
      @keydown="onKeydown($event, index)"
    >
      <p class="memory-text break" :class="{ dim: row.retired }">{{ row.body }}</p>
      <div class="foot">
        <span class="g-chip" data-test="memory-row-tier">{{ tierShort(row.tier) }}</span>
        <span v-if="row.scope === 'Retain'" class="g-chip is-accent" data-test="memory-row-retain">
          <Icon name="pin" :size="12" />{{ scopeLabel(row.scope) }}
        </span>
        <span class="brief">{{ row.brief }}</span>
        <!-- Validity belongs in the list, not only in the drawer, but as a sentence behind a mark. -->
        <HintTip align="end" class="more"><span class="validity">{{ row.validity }}</span> · {{ row.recordId }}</HintTip>
      </div>
    </li>
  </ul>
</template>

<style scoped>
.memory-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
}

.memory-card {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
  padding: var(--space-4) var(--space-5);
  border: 1px solid var(--glass-edge);
  border-radius: var(--radius-md);
  background: var(--glass-1);
  box-shadow: var(--glass-hi), var(--shadow-1);
  cursor: pointer;
  outline: none;
  transition:
    transform var(--motion-fast) var(--ease),
    background-color var(--motion-fast) var(--ease);
}

.memory-card:hover {
  background: var(--glass-2);
}

.memory-card.selected {
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
}

.memory-card:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.memory-text {
  margin: 0;
  font-size: var(--fs-prose);
  line-height: var(--lh-prose);
  display: -webkit-box;
  -webkit-line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

/* A retired record stays listed — "fully visible" means being able to see what replaced it — but it must not
   read as live, so the text recedes rather than the row disappearing. */
.memory-card.retired {
  opacity: 0.72;
}

.dim {
  text-decoration: line-through;
  text-decoration-color: var(--text-3);
}

.foot {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-inline-size: 0;
  font-size: var(--fs-meta);
  color: var(--text-3);
}

.brief {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.more {
  margin-inline-start: auto;
}
</style>
