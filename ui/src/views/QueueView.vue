<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { PartnerApiSurface, QueueView } from '../api'

const props = defineProps<{ api: PartnerApiSurface }>()

const view = ref<QueueView | null>(null)
const error = ref('')

const pending = computed(() => (view.value?.items ?? []).filter((i) => i.status === 'Queued' || i.status === 'Running').length)
const failed = computed(() => (view.value?.items ?? []).filter((i) => i.status === 'Failed').length)

async function refresh() {
  error.value = ''
  try {
    view.value = await props.api.queue()
  } catch (failure) {
    error.value = String(failure)
  }
}

async function cancel(itemId: string) {
  error.value = ''
  try {
    const canceled = await props.api.cancel(itemId)
    await refresh()

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
  <section class="pane">
    <header>
      <button @click="refresh">刷新</button>
      <span class="muted" data-test="queue-summary">
        深度 {{ view?.depth ?? 0 }} · 执行中 {{ view?.runningItemId ?? '无' }} · 待处理 {{ pending }} · 失败
        {{ failed }}
      </span>
    </header>

    <ul class="list" data-test="queue-list">
      <li v-for="item in view?.items ?? []" :key="item.itemId">
        <div class="meta">
          {{ item.itemId }} {{ item.source }} <strong>{{ item.status }}</strong> attempt={{ item.attempt }}
        </div>
        <div>{{ item.payload }}</div>
        <div v-if="item.error" class="error">{{ item.error }}</div>
        <button @click="cancel(item.itemId)" data-test="queue-cancel">取消</button>
      </li>
    </ul>

    <p v-if="error" class="error" data-test="error">{{ error }}</p>
  </section>
</template>
