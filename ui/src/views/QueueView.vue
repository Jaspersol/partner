<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { PartnerApiSurface, QueueView } from '../api'
import QueueList from '../components/QueueList.vue'

const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string]; changed: [] }>()

const view = ref<QueueView | null>(null)
const error = ref('')

const pending = computed(
  () => (view.value?.items ?? []).filter((item) => item.status === 'Queued' || item.status === 'Running').length,
)
const failed = computed(() => (view.value?.items ?? []).filter((item) => item.status === 'Failed').length)

async function refresh() {
  error.value = ''
  try {
    view.value = await props.api.queue()
    emit('context', `队列 · 深度 ${view.value.depth}`)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function cancel(itemId: string) {
  error.value = ''
  try {
    const canceled = await props.api.cancel(itemId)
    await refresh()
    emit('changed')

    // 404 means "already finished", which is not an error worth shouting about.
    if (!canceled) {
      error.value = `'${itemId}' 已完成，无法取消`
    }
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)
defineExpose({ refresh })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="queue-refresh" @click="refresh">刷新</button>
      <span class="spacer" />
      <span class="muted" data-test="queue-summary">
        深度 {{ view?.depth ?? 0 }} · 执行中 {{ view?.runningItemId ?? '无' }} · 待处理 {{ pending }} · 失败
        {{ failed }}
      </span>
    </div>

    <div class="scroll">
      <QueueList :items="view?.items ?? []" @cancel="cancel" />
      <p v-if="(view?.items.length ?? 0) === 0" class="muted" style="padding: var(--space-3)">（队列为空）</p>
    </div>

    <p v-if="error" class="error notice" data-test="error">{{ error }}</p>
  </div>
</template>

<style scoped>
.view {
  display: flex;
  flex-direction: column;
  min-block-size: 0;
  block-size: 100%;
}

.spacer {
  flex: 1;
}

.notice {
  margin: 0;
  padding: var(--space-1) var(--space-3);
  border-block-start: 1px solid var(--line);
}
</style>
