<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import type { QueueItem } from '../api'

const props = defineProps<{ items: QueueItem[] }>()
const emit = defineEmits<{ cancel: [itemId: string] }>()

/**
 * Elapsed time for running items. This is the one place with a local timer, and it is a display value
 * derived from a known start instant — not state the UI owns (ui.md §5.4).
 */
const now = ref(Date.now())
let ticker: ReturnType<typeof setInterval> | undefined

watch(
  () => props.items.some((item) => item.status === 'Running'),
  (running) => {
    if (running && ticker === undefined) {
      ticker = setInterval(() => {
        now.value = Date.now()
      }, 1000)
    } else if (!running && ticker !== undefined) {
      clearInterval(ticker)
      ticker = undefined
    }
  },
  { immediate: true },
)

onUnmounted(() => {
  if (ticker !== undefined) {
    clearInterval(ticker)
  }
})

function elapsed(item: QueueItem): string {
  if (item.status !== 'Running' || item.startedAt === null) {
    return ''
  }

  const started = Date.parse(item.startedAt)
  if (Number.isNaN(started)) {
    return ''
  }

  const seconds = Math.max(0, Math.floor((now.value - started) / 1000))
  return `已运行 ${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
}

const cancellable = computed(() => (item: QueueItem) => item.status === 'Queued' || item.status === 'Running')
</script>

<template>
  <ul class="list" data-test="queue-list">
    <li
      v-for="item in items"
      :key="item.itemId"
      class="queue-item"
      :data-status="item.status"
    >
      <div class="meta break">
        {{ item.itemId }} · {{ item.source }} · <strong>{{ item.status }}</strong>
        <span v-if="elapsed(item)"> · {{ elapsed(item) }}</span>
        <span v-if="item.attempt > 0"> · attempt={{ item.attempt }}</span>
      </div>
      <div class="break">{{ item.payload }}</div>
      <div v-if="item.error" class="error break">{{ item.error }}</div>
      <button
        v-if="cancellable(item)"
        class="ghost"
        data-test="queue-cancel"
        @click="emit('cancel', item.itemId)"
      >
        取消
      </button>
    </li>
  </ul>
</template>
