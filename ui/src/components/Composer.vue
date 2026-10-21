<script setup lang="ts">
import { t } from '../i18n'
import { computed, onUnmounted, ref, watch } from 'vue'
import { APPROVAL_GEARS } from '../api'
import type { AgentJobView, ConsumerState, PartnerApiSurface, QueueItem, QueueSummary, SubmitResult, UploadedMedia } from '../api'
import { parseComposerCommand } from '../composerCommand'
import { gearName } from '../gears'
import HintTip from './HintTip.vue'
import Icon from './Icon.vue'
import Segmented from './Segmented.vue'

/**
 * The delivery entry point. Submitting never inserts locally: the queue is re-read from the server, not
 * optimistically patched.
 */
const props = defineProps<{
  api: PartnerApiSurface
  summary?: QueueSummary | undefined
  /** Every item the last poll see, so the list under the badge is the same answer and not a second one. */
  queueItems?: readonly QueueItem[] | undefined
  consumer?: ConsumerState | undefined
  /** The current approval gear, or null while the desktop capability is off. */
  approval?: string | null | undefined
  /**
   * The gears on offer, and the one new messages use unless the sender picks another. Read once per
   * settings save; the composer never writes settings.
   */
  gears?: readonly string[] | undefined
  defaultGear?: string | undefined
  /**
   * How full the model's memory is, as the occupancy ring next to the send button. `label` and `hint` are the
   * sentences behind its ⓘ; `fraction` is 0–1. Absent until the session has been read once.
   */
  context?: { fraction: number; label: string; hint: string } | undefined
  /**
   * A sub-agent is working (or only just stopped) and the main agent has yet to answer it. Nothing is running in
   * the queue then, but the work is not over, so the button stays the stop button.
   */
  delegating?: boolean | undefined
  /** The sub-agent jobs that can be stopped. */
  jobs?: readonly AgentJobView[] | undefined
}>()
const emit = defineEmits<{
  sent: [result: SubmitResult]
  failed: [message: string]
  /** The gear changed: the app shell owns the setting, the composer only offers the choice. */
  approval: [gear: string]
  /** A running turn was stopped. The shell needs to hear about it to re-read the queue. */
  cancel: [itemId: string]
}>()

const box = ref<HTMLTextAreaElement | null>(null)
const draft = ref('')
const uploading = ref(false)
/**
 * Whether the waiting items are folded away.
 *
 * Open by default; folding only matters for two or more entries.
 */
const queueCollapsed = ref(false)
const dragging = ref(false)

/**
 * An attachment, with a local preview URL when it is an image.
 *
 * The name shown is the server's sanitised name, which is also what the model is told.
 */
type Attachment = { media: UploadedMedia; preview: string | null }
const attachments = ref<Attachment[]>([])

/**
 * The gear the next message will be sent with. Held here rather than in the shell because it belongs to one
 * message: it is not a setting and is not persisted.
 *
 * Re-synced from the props so a changed default applies to the next message rather than a stale local choice.
 */
const gear = ref('')
watch(
  () => [props.gears, props.defaultGear] as const,
  () => {
    gear.value = props.defaultGear ?? ''
  },
  { immediate: true },
)

/**
 * A value the menu does not carry is still offered rather than dropped: the gear is free text, and a select
 * that silently excluded the configured default would send something the user did not choose.
 */
const gearOptions = computed(() =>
  gear.value !== '' && !(props.gears ?? []).includes(gear.value) ? [...(props.gears ?? []), gear.value] : props.gears ?? [],
)

/**
 * The items still waiting their turn, oldest first — the order the queue will run them in.
 *
 * Only items that have not started; a running turn is already visible in the conversation.
 */
const waiting = computed(() =>
  [...(props.queueItems ?? [])]
    .filter((item) => item.status === 'Queued')
    .sort((left, right) => left.seq - right.seq),
)

/** Whether the entries are on screen. One entry is never folded. */
const queueShown = computed(() => waiting.value.length === 1 || !queueCollapsed.value)

/** Why nothing will move; shown even when the queue summary is unavailable. */
const modelName = computed(() => props.consumer?.model ?? '')

const consumerNote = computed(() => {
  const state = props.consumer
  return state && !state.running ? t('未执行 · {reason}', { reason: state.reason }) : ''
})

/**
 * Which pill's popover is open. Opens in place above the dock; closes on an outside click or Esc.
 */
const picker = ref<'gear' | 'approval' | null>(null)
const dock = ref<HTMLElement | null>(null)

function togglePicker(which: 'gear' | 'approval'): void {
  picker.value = picker.value === which ? null : which
}

function onDocumentPointerDown(event: Event): void {
  if (picker.value !== null && dock.value !== null && event.target instanceof Node) {
    const open = dock.value.querySelector('.picker-pop')
    const pills = dock.value.querySelectorAll('.pill')
    const inside = (open?.contains(event.target) ?? false) || Array.from(pills).some((pill) => pill.contains(event.target as Node))
    if (!inside) picker.value = null
  }
}

watch(picker, (open) => {
  if (open !== null) document.addEventListener('pointerdown', onDocumentPointerDown)
  else document.removeEventListener('pointerdown', onDocumentPointerDown)
})

const gearChoices = computed(() => gearOptions.value.map((value) => ({ value, label: gearName(value) })))

const approvalOptions = computed(() =>
  APPROVAL_GEARS.map((option) => ({
    value: option.value,
    label: ({ none: t('不审批'), dangerous: t('危险指令'), all: t('全部') } as Record<string, string>)[option.value] ?? t(option.label),
    title: t(option.hint),
  })),
)
const approvalLabel = computed(() => approvalOptions.value.find((option) => option.value === props.approval)?.label ?? props.approval ?? '')
const approvalHint = computed(() => t(APPROVAL_GEARS.find((option) => option.value === props.approval)?.hint ?? ''))

/** The ring: circumference 2πr with r=8 in a 20-unit box. */
const RING = 2 * Math.PI * 8
const ringOffset = computed(() => RING * (1 - Math.min(1, Math.max(0, props.context?.fraction ?? 0))))

const busy = ref(false)

const canSend = computed(
  () => !busy.value && !uploading.value && (draft.value.trim() !== '' || attachments.value.length > 0),
)

/** Whether there is a turn in flight, and which queue item that is. Empty string counts as nothing. */
const runningId = computed(() => {
  const id = props.summary?.runningItemId
  return id === null || id === undefined || id === '' ? null : id
})

const hasContent = computed(() => draft.value.trim() !== '' || attachments.value.length > 0)

/**
 * What the one button on the right is, given what is in the box and what the queue is doing.
 *
 * The box being empty decides it: with nothing typed the button interrupts the running turn. Once the reader
 * types, the same button queues, because the queue is strictly FIFO — so "发送" during a turn would promise a
 * "go now" the runtime does not provide.
 */
const action = computed<{ kind: 'send' | 'queue' | 'interrupt'; label: string }>(() => {
  if (runningId.value === null) {
    // Delegated work is not a queue item, so a typed message is not waiting behind anything: it is simply sent.
    return props.delegating === true && !hasContent.value
      ? { kind: 'interrupt', label: t('中断子任务') }
      : { kind: 'send', label: t('发送') }
  }
  return hasContent.value ? { kind: 'queue', label: t('加入队列') } : { kind: 'interrupt', label: t('中断运行') }
})

/**
 * Interrupts the running turn.
 *
 * Calls the API directly like `send`; the shell is told afterwards because it owns the queue state.
 *
 * Cancel has a real failure mode: an item that finished between the last poll and this click cannot be
 * cancelled, and the API returns false rather than throwing. That is reported, not swallowed.
 */
async function interrupt() {
  const itemId = runningId.value
  if (busy.value) {
    return
  }

  if (itemId === null) {
    await interruptJobs()
    return
  }

  busy.value = true
  try {
    const canceled = await props.api.cancel(itemId)
    if (!canceled) {
      emit('failed', t('\'{itemId}\' 已完成，无法取消', { itemId: itemId }))
      return
    }
    emit('cancel', itemId)
  } catch (failure) {
    emit('failed', String(failure))
  } finally {
    busy.value = false
  }
}

/** Stops the sub-agents that are working. Each one's report still comes back, saying it was cancelled. */
async function interruptJobs() {
  const ids = (props.jobs ?? []).map((job) => job.id)
  if (ids.length === 0) {
    return
  }

  busy.value = true
  try {
    const results = await Promise.all(ids.map((id) => props.api.cancelAgentJob(id)))
    if (!results.some(Boolean)) {
      emit('failed', t('子任务已完成，无法取消'))
      return
    }
    emit('cancel', ids[0] as string)
  } catch (failure) {
    emit('failed', String(failure))
  } finally {
    busy.value = false
  }
}

function release(attachment: Attachment) {
  if (attachment.preview !== null) {
    URL.revokeObjectURL(attachment.preview)
  }
}

/** Uploads a set of files from the picker, drag-drop or paste. */
function takeFiles(files: FileList | File[] | null) {
  if (files === null || files.length === 0 || uploading.value) {
    return
  }

  uploading.value = true
  void (async () => {
    try {
      for (const file of Array.from(files)) {
        // The name goes up with the bytes so the server can sanitise it; what is rendered is the server's name.
        const media = await props.api.uploadMedia(file, file.type, file.name)

        // Only an image can be previewed as an image.
        const preview = media.contentType.startsWith('image/')
          ? URL.createObjectURL(await props.api.mediaBlob(media.mediaId))
          : null
        attachments.value = [...attachments.value, { media, preview }]
      }
    } catch (failure) {
      emit('failed', String(failure))
    } finally {
      uploading.value = false
    }
  })()
}

function onPicked(event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  takeFiles(files)
}

function onDrop(event: DragEvent) {
  dragging.value = false
  takeFiles(event.dataTransfer?.files ?? null)
}

function onPaste(event: ClipboardEvent) {
  takeFiles(event.clipboardData?.files ?? null)
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

  busy.value = true
  try {
    const media = attachments.value.map((attachment) => attachment.media.mediaId)
    // The source is what the message is, not something the user picks: typing here is always text.
    // The gear rides this message only, so it is never reset afterwards; the next message uses the default.
    const result = await props.api.submitInput(payload, 'text', { media, reasoningEffort: gear.value })

    attachments.value.forEach(release)
    attachments.value = []
    draft.value = ''
    emit('sent', result)
  } catch (failure) {
    emit('failed', String(failure))
  } finally {
    busy.value = false
  }
}

/**
 * Runs the box's contents as a command if that is what they are, and says whether it consumed them.
 *
 * A command is a whole line: "搜一下 /status 的实现" is a sentence about a command, not the command, and
 * swallowing it would send nothing at all.
 */
function tryCommand(): boolean {
  const command = parseComposerCommand(draft.value)
  if (command === null) {
    return false
  }

  if (command.kind === 'status') {
    draft.value = ''
    queueCollapsed.value = !queueCollapsed.value
  }

  return true
}

function onEnter(event: KeyboardEvent) {
  // Checked before sending, so a command is never delivered as a message.
  if (tryCommand()) {
    event.preventDefault()
    return
  }

  event.preventDefault()
  void send()
}

function focus() {
  box.value?.focus()
}

/**
 * Puts a removed message back in the box rather than making it be retyped.
 *
 * A retraction is physical: the text leaves the log, and the tombstone (`purge #a..#b`) records the range,
 * not the words. Without this, "撤回" on your own message means retyping it from memory.
 */
function restore(body: string) {
  draft.value = body
  focus()
}

onUnmounted(() => {
  attachments.value.forEach(release)
  document.removeEventListener('pointerdown', onDocumentPointerDown)
})

defineExpose({ focus, restore })
</script>

<template>
  <div
    class="composer-root"
    :class="{ 'is-dragging': dragging }"
    data-test="composer"
    @dragover.prevent="dragging = true"
    @dragleave="dragging = false"
    @drop="onDrop"
    @paste="onPaste"
    @keydown.escape="picker = null"
  >
    <!-- Why nothing will move; absent when something will. -->
    <div v-if="consumerNote" class="dock-note" data-test="composer-strip">
      <span data-test="composer-consumer">{{ consumerNote }}</span>
    </div>

    <div ref="dock" class="dock g-float">
      <div v-if="attachments.length > 0" class="attachments" data-test="composer-attachments">
        <span
          v-for="attachment in attachments"
          :key="attachment.media.mediaId"
          class="attachment"
          data-test="composer-attachment"
          :title="attachment.media.fileName"
        >
          <img v-if="attachment.preview" :src="attachment.preview" :alt="t('附图预览')" />
          <span v-else class="file-glyph"><Icon name="file" :size="14" /></span>
          <span class="file-name">{{ attachment.media.fileName }}</span>
          <span class="meta">{{ Math.round(attachment.media.bytes / 1024) }} KB</span>
          <button
            class="ghost"
            data-test="composer-detach"
            :title="t('移除附件')"
            :aria-label="t('移除附件')"
            @click="detach(attachment.media.mediaId)"
          >
            <Icon name="x" :size="12" />
          </button>
        </span>
      </div>

      <!--
        What is waiting, in the order it will run. The arrow folds the list only when there is more than one entry.
      -->
      <div v-if="waiting.length > 0" class="queue" data-test="composer-queue">
        <ol v-if="queueShown" class="queue-items" data-test="composer-queue-list">
          <li v-for="item in waiting" :key="item.itemId" data-test="composer-queue-item">
            <span class="queue-payload">{{ item.payload }}</span>
            <span class="queue-id sr-only">{{ item.itemId }}</span>
          </li>
        </ol>
        <button
          v-if="waiting.length > 1"
          class="ghost queue-toggle"
          data-test="composer-queue-toggle"
          :aria-expanded="queueShown"
          :aria-label="queueShown ? t('收起队列') : t('展开队列')"
          @click="queueCollapsed = !queueCollapsed"
        >
          <Icon :name="queueShown ? 'chevron-up' : 'chevron-down'" :size="14" />
        </button>
      </div>

      <div class="composer-input">
        <textarea
          ref="box"
          v-model="draft"
          rows="2"
          data-test="composer-input"
          :aria-label="t('输入消息')"
          :placeholder="t('输入消息…')"
          @keydown.enter.exact="onEnter"
        />
        <HintTip align="end" class="input-hint">{{ t('Enter 发送，Shift+Enter 换行。输入') }} <code>/status</code> {{ t('查看排队情况。') }}</HintTip>
      </div>

      <div class="composer-tools">
        <label class="attach" :class="{ busy: uploading }" :aria-busy="uploading || undefined" data-test="composer-attach" :title="t('添加附件')">
          <Icon name="paperclip" :size="16" />
          <span :class="{ 'sr-only': !uploading }">{{ uploading ? t('上传中…') : t('添加附件') }}</span>
          <input type="file" multiple data-test="composer-attach-input" @change="onPicked" />
        </label>

        <!--
          The two pickers sit next to the send button, where the choice they carry lands: they are read at the
          moment of sending.
        -->
        <span v-if="approval" class="pill-wrap">
          <button
            class="pill"
            type="button"
            :aria-expanded="picker === 'approval'"
            :aria-label="t('审批挡位')"
            :data-value="approval"
            data-test="composer-approval"
            @click="togglePicker('approval')"
          >
            <Icon name="shield" :size="14" /> {{ t('审批 · {approvalLabel}', { approvalLabel: approvalLabel }) }}
          </button>
          <div v-if="picker === 'approval'" class="picker-pop g-float" data-test="composer-approval-pop">
            <Segmented
              :model-value="approval"
              :options="approvalOptions"
              :label="t('审批挡位')"
              @update:model-value="emit('approval', $event)"
            />
            <p class="picker-hint">{{ approvalHint }}</p>
          </div>
        </span>

        <span class="spacer" />

        <!-- The model that answers, read off the consumer, beside the gear and the ring. -->
        <span v-if="modelName" class="model" :title="t('当前模型：{modelName}', { modelName: modelName })" data-test="composer-model">{{ modelName }}</span>

        <span v-if="gearOptions.length > 0" class="pill-wrap">
          <button
            class="pill"
            type="button"
            :aria-expanded="picker === 'gear'"
            :aria-label="t('思考强度')"
            :data-value="gear"
            data-test="composer-gear"
            @click="togglePicker('gear')"
          >
            <Icon name="sparkle" :size="14" /> {{ t('思考 · {gearName}', { gearName: gearName(gear) }) }}
          </button>
          <div v-if="picker === 'gear'" class="picker-pop is-end g-float" data-test="composer-gear-pop">
            <Segmented
              :model-value="gear"
              :options="gearChoices"
              :label="t('思考强度')"
              @update:model-value="gear = $event"
            />
            <p class="picker-hint">{{ t('越高想得越仔细，回答也越慢。') }}</p>
          </div>
        </span>

        <!-- Memory occupancy: a ring that fills as the history grows and turns amber near the limit. -->
        <span
          v-if="context"
          class="ctx"
          :class="{ 'context-tight': context.fraction >= 0.85 }"
          :title="context.hint"
          data-test="session-context"
        >
          <svg width="20" height="20" viewBox="0 0 20 20" aria-hidden="true">
            <circle cx="10" cy="10" r="8" class="ring-track" />
            <circle cx="10" cy="10" r="8" class="ring-fill" :stroke-dasharray="RING" :stroke-dashoffset="ringOffset" />
          </svg>
          <span class="ctx-pct" aria-hidden="true">{{ Math.round(context.fraction * 100) }}%</span>
          <span class="sr-only">{{ context.label }}</span>
        </span>

        <!--
          One button, three states, decided by whether the box has anything in it and whether a turn is running.
          `composer-send` stays on the two states that submit; the interrupt gets its own hook. The words stay in
          the accessible name even though the shape is a circle with an arrow.
        -->
        <button
          v-if="action.kind === 'interrupt'"
          class="send is-stop"
          data-test="composer-interrupt"
          :disabled="busy"
          :aria-busy="busy || undefined"
          :title="runningId === null ? t('中断正在运行的子任务') : t('中断正在运行的 {runningId}', { runningId: runningId })"
          @click="interrupt"
        >
          <Icon name="square" :size="14" /><span class="sr-only">{{ busy ? t('中断中…') : action.label }}</span>
        </button>
        <button
          v-else
          class="send primary"
          :disabled="!canSend"
          :aria-busy="busy || undefined"
          data-test="composer-send"
          :title="action.label"
          @click="send"
        >
          <Icon name="arrow-up" :size="16" :stroke-width="2.2" /><span class="sr-only">{{ action.label }}</span>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.composer-root {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
  inline-size: 100%;
}

/* The drop target must announce itself. */
.composer-root.is-dragging .dock {
  outline: 2px dashed var(--accent);
  outline-offset: 2px;
}

.dock-note {
  justify-self: center;
  max-inline-size: 100%;
  padding: var(--space-1) var(--space-4);
  border-radius: var(--radius-pill);
  background: var(--glass-2);
  border: 1px solid var(--glass-edge);
  color: var(--text-2);
  font-size: var(--fs-meta);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* The dock: a floating glass bar. The message list scrolls underneath it. */
.dock {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-1);
  padding: var(--space-3) var(--space-3) var(--space-2);
}

.attachments {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  padding-inline: var(--space-2);
}

.attachment {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  background: var(--surface-2);
  border-radius: var(--radius-sm);
  padding: 2px var(--space-1) 2px 2px;
  max-inline-size: 100%;
  min-inline-size: 0;
}

.attachment img {
  block-size: 32px;
  inline-size: 32px;
  flex: none;
  object-fit: cover;
  border-radius: 8px;
}

.attachment button {
  min-block-size: 1.5rem;
  padding: 0 var(--space-1);
  border-radius: var(--radius-sm);
}

.file-glyph {
  flex: none;
  color: var(--text-2);
  padding-inline: 0.5em;
}

/* A long path has to give way somewhere, and the middle is where a reader does not look. */
.file-name {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-inline-size: 14rem;
}

.queue {
  display: flex;
  align-items: flex-start;
  gap: var(--space-1);
  padding-inline: var(--space-2);
}

.queue-items {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-1);
  flex: 1;
  min-inline-size: 0;
  /* A scroll container, deliberately: capped so a deep queue cannot push the input off the window. */
  max-block-size: 5rem;
  overflow-y: auto;
}

.queue-items > li {
  display: inline-flex;
  max-inline-size: 100%;
  min-inline-size: 0;
  padding: 2px var(--space-3);
  border-radius: var(--radius-pill);
  background: var(--surface-2);
  font-size: var(--fs-meta);
}

.queue-payload {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-2);
}

.queue-toggle {
  min-block-size: 1.5rem;
  padding: 0 var(--space-1);
  border-radius: var(--radius-sm);
}

.composer-input {
  position: relative;
}

.composer-input textarea {
  min-block-size: 3.25rem;
  max-block-size: 12rem;
  padding: var(--space-2) var(--space-6) var(--space-2) var(--space-3);
  border: none;
  background: transparent;
  resize: none;
  font-size: var(--fs-prose);
}

/* The dock itself is the field, so the textarea shows no ring of its own; the dock gets one instead. */
.composer-input textarea:focus-visible {
  outline: none;
  box-shadow: none;
}

.dock {
  transition: box-shadow var(--motion-fast) var(--ease);
}

.dock.g-float:has(textarea:focus) {
  box-shadow:
    var(--glass-hi),
    0 2px 6px rgb(0 0 0 / 7%),
    0 12px 32px rgb(0 0 0 / 10%);
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme='light']) .dock.g-float:has(textarea:focus) {
    box-shadow:
      var(--glass-hi),
      0 0 0 1px rgb(255 255 255 / 6%),
      0 10px 30px rgb(255 255 255 / 7%);
  }
}

:root[data-theme='dark'] .dock.g-float:has(textarea:focus) {
  box-shadow:
    var(--glass-hi),
    0 0 0 1px rgb(255 255 255 / 6%),
    0 10px 30px rgb(255 255 255 / 7%);
}

.input-hint {
  position: absolute;
  inset-block-start: var(--space-2);
  inset-inline-end: var(--space-2);
}

.composer-tools {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.attach {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  min-block-size: 2.25rem;
  min-inline-size: 2.25rem;
  justify-content: center;
  padding: 0 var(--space-2);
  border-radius: var(--radius-pill);
  color: var(--text-2);
  cursor: pointer;
  white-space: nowrap;
  font-size: var(--fs-meta);
}

.attach:hover {
  background: var(--surface-2);
  color: var(--text);
}

.attach:focus-within {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.attach input {
  display: none;
}

.attach.busy {
  cursor: progress;
}

.pill-wrap {
  position: relative;
  display: inline-flex;
}

.pill {
  min-block-size: 2rem;
  padding: 0 var(--space-3);
  border-radius: var(--radius-pill);
  border-color: transparent;
  background: var(--surface-2);
  box-shadow: none;
  color: var(--text-2);
  font-size: var(--fs-meta);
}

.pill:hover:not(:disabled),
.pill[aria-expanded='true'] {
  background: var(--accent-soft);
  color: var(--accent-pressed);
}

.picker-pop {
  position: absolute;
  inset-block-end: calc(100% + var(--space-2));
  inset-inline-start: 0;
  z-index: 25;
  inline-size: min(26rem, 80vw);
  padding: var(--space-4) var(--space-5) var(--space-3);
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
}

.picker-pop.is-end {
  inset-inline-start: auto;
  inset-inline-end: 0;
}

.model {
  max-inline-size: 11rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.picker-pop .segmented {
  justify-self: start;
}

.picker-hint {
  margin: 0;
  color: var(--text-2);
  font-size: var(--fs-meta);
}

.ctx {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  color: var(--text-3);
  font-size: var(--fs-meta);
  font-variant-numeric: tabular-nums;
}

.ctx svg {
  transform: rotate(-90deg);
}

.ring-track,
.ring-fill {
  fill: none;
  stroke-width: 2.5;
}

.ring-track {
  stroke: var(--surface-2);
}

.ring-fill {
  stroke: var(--accent);
  stroke-linecap: round;
  transition: stroke-dashoffset var(--motion-slow) var(--ease);
}

.ctx.context-tight {
  color: var(--warn);
  font-weight: 600;
}

.ctx.context-tight .ring-fill {
  stroke: var(--warn);
}

.send {
  flex: none;
  inline-size: 2.25rem;
  min-block-size: 2.25rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.send.is-stop {
  background: var(--error);
  color: var(--on-accent);
  border-color: transparent;
}
</style>
