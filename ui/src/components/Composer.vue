<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import type { ConsumerState, PartnerApiSurface, QueueSummary, SubmitResult, UploadedMedia } from '../api'

/**
 * The delivery entry point (ui.md §6, design doc §2.3.1). Submitting never inserts locally: the answer is
 * re-read from the server, and what the user sees afterwards is the queue, not an optimistic row.
 */
const props = defineProps<{
  api: PartnerApiSurface
  summary?: QueueSummary | undefined
  consumer?: ConsumerState | undefined
  desktopEnabled?: boolean | undefined
}>()
const emit = defineEmits<{ sent: [result: SubmitResult]; failed: [message: string] }>()

const box = ref<HTMLTextAreaElement | null>(null)
const draft = ref('')
const sending = ref(false)
const stripOpen = ref(true)
const uploading = ref(false)

/** What is attached, with a local preview URL so the user can see what they are about to send. */
type Attachment = { media: UploadedMedia; preview: string }
const attachments = ref<Attachment[]>([])

const queueStatus = computed(() => {
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

/**
 * Why nothing will move. Rendered independently of the queue summary: when the summary is unavailable the
 * explanation matters more, not less.
 */
/**
 * Which tools the model actually has. Absent by default is not a detail the reader can infer: with the
 * desktop off, the model never sees those tools at all, and a request to control the desktop just gets a
 * text answer — which looks like a broken capability rather than a switched-off one.
 */
const desktopOff = computed(() => props.desktopEnabled !== true)

const toolsNote = '工具：无 · 桌面未启用（设置 → 桌面）'

const consumerNote = computed(() => {
  const state = props.consumer
  return state && !state.running ? `未执行 · ${state.reason}` : ''
})

const canSend = computed(
  () => !sending.value && !uploading.value && (draft.value.trim() !== '' || attachments.value.length > 0),
)

function release(attachment: Attachment) {
  URL.revokeObjectURL(attachment.preview)
}

async function attach(event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''

  if (files.length === 0) {
    return
  }

  uploading.value = true
  try {
    for (const file of files) {
      const media = await props.api.uploadMedia(file)
      const preview = URL.createObjectURL(await props.api.mediaBlob(media.mediaId))
      attachments.value = [...attachments.value, { media, preview }]
    }
  } catch (failure) {
    emit('failed', String(failure))
  } finally {
    uploading.value = false
  }
}

function detach(mediaId: string) {
  const going = attachments.value.find((attachment) => attachment.media.mediaId === mediaId)
  if (going) {
    release(going)
  }

  attachments.value = attachments.value.filter((attachment) => attachment.media.mediaId !== mediaId)
}

async function send() {
  const payload = draft.value.trim()
  if (!canSend.value) {
    return
  }

  sending.value = true
  try {
    const media = attachments.value.map((attachment) => attachment.media.mediaId)
    // The source is what the message *is*, not something the user picks: typing here is always text, the
    // microphone enqueues its own items, and a scheduled task comes from the scheduler.
    const result = await props.api.submitInput(payload, 'text', undefined, media)

    attachments.value.forEach(release)
    attachments.value = []
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

onUnmounted(() => {
  attachments.value.forEach(release)
})

defineExpose({ focus })
</script>

<template>
  <div class="composer" data-test="composer">
    <div
      v-if="stripOpen && (queueStatus || consumerNote || desktopOff)"
      class="composer-strip"
      data-test="composer-strip"
    >
      <span v-if="queueStatus">{{ queueStatus }}</span>
      <span v-if="consumerNote" class="muted" data-test="composer-consumer">{{ consumerNote }}</span>
      <span v-if="desktopOff" class="muted" data-test="composer-tools">{{ toolsNote }}</span>
      <button class="ghost" data-test="composer-strip-toggle" @click="stripOpen = false">⌃</button>
    </div>

    <div v-if="attachments.length > 0" class="attachments" data-test="composer-attachments">
      <span
        v-for="attachment in attachments"
        :key="attachment.media.mediaId"
        class="attachment"
        data-test="composer-attachment"
      >
        <img :src="attachment.preview" alt="附图" />
        <span class="meta">{{ Math.round(attachment.media.bytes / 1024) }} KB</span>
        <button
          class="ghost"
          data-test="composer-detach"
          :title="'移除附图'"
          @click="detach(attachment.media.mediaId)"
        >
          ×
        </button>
      </span>
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
      <label class="ghost attach" :class="{ busy: uploading }" data-test="composer-attach">
        {{ uploading ? '上传中…' : '＋ 图片' }}
        <input
          type="file"
          accept="image/png,image/jpeg,image/webp,image/gif"
          multiple
          data-test="composer-attach-input"
          @change="attach"
        />
      </label>
      <span class="spacer" />
      <span v-if="!stripOpen && queueStatus" class="meta">{{ queueStatus }}</span>
      <button
        v-if="!stripOpen"
        class="ghost"
        data-test="composer-strip-toggle"
        @click="stripOpen = true"
      >
        ⌄
      </button>
      <button class="primary" :disabled="!canSend" data-test="composer-send" @click="send">发送 ⏎</button>
    </div>
  </div>
</template>

<style scoped>
.attachments {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-1);
  padding: var(--space-1) var(--space-3);
}

.attachment {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  padding: 2px var(--space-1);
}

.attachment img {
  block-size: 32px;
  inline-size: 32px;
  object-fit: cover;
  border-radius: var(--radius-sm);
}

.attach input {
  display: none;
}

.attach.busy {
  color: var(--muted);
}
</style>
