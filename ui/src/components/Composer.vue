<script setup lang="ts">
import { computed, ref } from 'vue'
import type { PartnerApiSurface, QueueSummary, SubmitResult } from '../api'

/**
 * The delivery entry point (ui.md §6, design doc §2.3.1). Submitting never inserts locally: the answer is
 * re-read from the server, and what the user sees afterwards is the queue, not an optimistic row.
 */
const props = defineProps<{ api: PartnerApiSurface; summary?: QueueSummary | undefined }>()
const emit = defineEmits<{ sent: [result: SubmitResult]; failed: [message: string] }>()

const SOURCES = ['text', 'voice', 'schedule'] as const

const box = ref<HTMLTextAreaElement | null>(null)
const draft = ref('')
const source = ref<(typeof SOURCES)[number]>('text')
const sending = ref(false)
const stripOpen = ref(true)

const status = computed(() => {
  const summary = props.summary
  if (!summary) {
    return ''
  }

  const parts = [`队列 ${summary.pending}`, `失败 ${summary.failed}`]
  if (summary.runningItemId) {
    parts.push(`执行中 ${summary.runningItemId}`)
  }

  return parts.join(' · ')
})

async function send() {
  const payload = draft.value.trim()
  if (payload === '' || sending.value) {
    return
  }

  sending.value = true
  try {
    const result = await props.api.submitInput(payload, source.value)
    draft.value = ''
    emit('sent', result)
  } catch (failure) {
    emit('failed', String(failure))
  } finally {
    sending.value = false
  }
}
function focus() {
  box.value?.focus()
}

defineExpose({ focus })
</script>

<template>
  <div class="composer" data-test="composer">
    <div v-if="stripOpen && status" class="composer-strip" data-test="composer-strip">
      <span>{{ status }}</span>
      <button class="ghost" data-test="composer-strip-toggle" @click="stripOpen = false">⌃</button>
    </div>

    <div class="composer-input">
      <textarea
        ref="box"
        v-model="draft"
        rows="2"
        placeholder="输入消息…（Enter 发送，Shift+Enter 换行）"
        data-test="composer-input"
        @keydown.enter.exact.prevent="send"
      />
    </div>

    <div class="composer-tools">
      <select v-model="source" data-test="composer-source" aria-label="来源">
        <option v-for="option in SOURCES" :key="option" :value="option">{{ option }}</option>
      </select>
      <span class="spacer" />
      <span v-if="!stripOpen && status" class="meta">{{ status }}</span>
      <button
        v-if="!stripOpen"
        class="ghost"
        data-test="composer-strip-toggle"
        @click="stripOpen = true"
      >
        ⌄
      </button>
      <button class="primary" :disabled="draft.trim() === '' || sending" data-test="composer-send" @click="send">
        发送 ⏎
      </button>
    </div>
  </div>
</template>
