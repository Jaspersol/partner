<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, PartnerApi, type PartnerApiSurface } from './api'
import { resolveConnection } from './config'
import MemoryView from './views/MemoryView.vue'
import QueueView from './views/QueueView.vue'
import SessionView from './views/SessionView.vue'
import SettingsView from './views/SettingsView.vue'

const tabs = ['会话', '队列', '记忆', '设置'] as const
const active = ref<(typeof tabs)[number]>('会话')

const connectionError = ref('')
const blocked = ref('')
const api = ref<PartnerApiSurface | null>(null)
const sessionView = ref<InstanceType<typeof SessionView> | null>(null)
const queueView = ref<InstanceType<typeof QueueView> | null>(null)
const memoryView = ref<InstanceType<typeof MemoryView> | null>(null)
const settingsView = ref<InstanceType<typeof SettingsView> | null>(null)

const status = computed(() => (connectionError.value === '' ? 'ok' : connectionError.value))

async function refreshBlocked() {
  if (!api.value) {
    return
  }

  try {
    const queue = await api.value.queue()
    const pending = queue.items.filter((i) => i.status === 'Queued' || i.status === 'Running').length
    const failed = queue.items.filter((i) => i.status === 'Failed').length

    // Rule: blocked work is visible without opening the tab.
    blocked.value = `队列 ${pending} · 失败 ${failed}` + (queue.runningItemId ? ` · 执行中 ${queue.runningItemId}` : '')
  } catch (failure) {
    blocked.value = failure instanceof ApiError ? `API ${failure.status}` : '队列状态未知'
  }
}

onMounted(async () => {
  const resolved = resolveConnection()
  if (!resolved.ok) {
    connectionError.value = resolved.reason
    return
  }

  api.value = new PartnerApi(resolved.connection)
  await refreshBlocked()
})
</script>

<template>
  <div class="app">
    <nav>
      <button
        v-for="tab in tabs"
        :key="tab"
        :class="{ active: tab === active }"
        :data-test="`tab-${tab}`"
        @click="active = tab"
      >
        {{ tab }}
      </button>
    </nav>

    <main v-if="api">
      <SessionView v-show="active === '会话'" ref="sessionView" :api="api" />
      <QueueView v-show="active === '队列'" ref="queueView" :api="api" />
      <MemoryView v-show="active === '记忆'" ref="memoryView" :api="api" />
      <SettingsView v-show="active === '设置'" ref="settingsView" :api="api" />
    </main>

    <p v-else class="error" data-test="connection-error">{{ connectionError }}</p>

    <footer>
      <span data-test="status">{{ status }}</span>
      <span class="blocked" data-test="blocked">{{ blocked }}</span>
    </footer>
  </div>
</template>
