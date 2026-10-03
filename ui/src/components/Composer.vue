<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { APPROVAL_GEARS } from '../api'
import type { ConsumerState, PartnerApiSurface, QueueItem, QueueSummary, SubmitResult, UploadedMedia } from '../api'
import { parseComposerCommand } from '../composerCommand'
import Icon from './Icon.vue'

/**
 * The delivery entry point (ui.md §6, design doc §2.3.1). Submitting never inserts locally: the answer is
 * re-read from the server, and what the user sees afterwards is the queue, not an optimistic row.
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
   * settings save: the composer never writes settings, so it has nothing to re-read after a send.
   */
  gears?: readonly string[] | undefined
  defaultGear?: string | undefined
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
const stripOpen = ref(true)
const uploading = ref(false)
const queueOpen = ref(false)
const dragging = ref(false)

/**
 * An attachment, with a local preview URL when it is an image.
 *
 * The name shown is the server's: it sanitised what the browser sent, and the model is told that same name, so
 * the card and the prompt cannot disagree.
 */
type Attachment = { media: UploadedMedia; preview: string | null }
const attachments = ref<Attachment[]>([])

/**
 * The gear the next message will be sent with. Held here rather than in the shell because it belongs to
 * one message: it is not a setting, it is not persisted, and it is what the send actually carries.
 *
 * Re-synced from the props so a settings save that changes the default takes effect on the next message
 * rather than being shadowed by a stale local choice.
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
 * A value the menu does not carry is still offered rather than dropped: the gear is free text (an endpoint
 * can name its own levels), and a select that silently excludes the configured default would send
 * something the user did not choose.
 */
const gearOptions = computed(() =>
  gear.value !== '' && !(props.gears ?? []).includes(gear.value) ? [...(props.gears ?? []), gear.value] : props.gears ?? [],
)

const queueStatus = computed(() => {
  const summary = props.summary
  if (!summary) {
    return ''
  }

  // No failure count. The queue holds only what is still in play, so this number is always zero — and a
  // standing 「失败 0」 next to a badge that wisely says nothing is worse than silence: it claims the question
  // was asked and answered, when it is not being tracked here at all. A failed turn is read in the
  // conversation, where it wrote its error event.
  const parts = [`队列 ${summary.pending}`]

  if (summary.runningItemId) {
    parts.push(`执行中 ${summary.runningItemId}`)
  }

  return parts.join(' · ')
})

/**
 * The rows behind the summary, ordered the way the queue runs: what is in flight, then what is waiting
 * behind it, then what failed. A failed item is listed with its reason rather than only counted — "失败 1"
 * tells a reader that something is wrong and nothing about what.
 */
const listed = computed(() => {
  const rank = (status: string) => (status === 'Running' ? 0 : status === 'Queued' ? 1 : status === 'Failed' ? 2 : 3)
  return [...(props.queueItems ?? [])].sort((left, right) => rank(left.status) - rank(right.status) || right.seq - left.seq)
})

const STATUS_TEXT: Record<string, string> = {
  Queued: '待处理',
  Running: '执行中',
  Done: '已完成',
  Failed: '失败',
  Canceled: '已取消',
}

/**
 * Which tools the model actually has. It cannot be inferred: with the desktop off the model never sees those
 * tools, so a request to control the desktop just gets a text answer — which reads as a broken capability
 * rather than a switched-off one.
 */
const tools = computed(() => props.consumer?.tools ?? [])

const toolsNote = computed(() => {
  if (tools.value.length === 0) {
    return '工具：无 · 桌面未启用（设置 → 桌面）'
  }

  const names = tools.value.map((tool) => (tool.decision === 'ask' ? `${tool.name}（需批准）` : tool.name))
  return `工具：${names.join('、')}`
})

/**
 * Why nothing will move. Rendered independently of the queue summary: when the summary is unavailable the
 * explanation matters more, not less.
 */
const consumerNote = computed(() => {
  const state = props.consumer
  return state && !state.running ? `未执行 · ${state.reason}` : ''
})

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
 * <para>
 * The box being empty is the signal that decides it, not a separate control. A reader who has just typed
 * nothing has nothing to queue, so the button they can see is the one that interrupts the turn they are
 * watching. The moment they start typing, interrupting stops being what they want — and the same button
 * becomes "add to queue", because the queue is strictly FIFO and this message is going to wait anyway. That
 * is worth saying out loud in the label: "发送" during a turn reads as "go now", which the runtime does not
 * promise (see ui.md §6).
 * </para>
 */
const action = computed<{ kind: 'send' | 'queue' | 'interrupt'; label: string }>(() => {
  if (runningId.value === null) {
    return { kind: 'send', label: '发送' }
  }
  return hasContent.value ? { kind: 'queue', label: '加入队列' } : { kind: 'interrupt', label: '中断运行' }
})

/**
 * Interrupts the running turn.
 *
 * <para>
 * This calls the API directly, exactly as `send` does — the composer is handed the API surface and uses it for
 * submissions, uploads and media fetches, and routing one call through the shell for the sake of a tidier
 * story would be the odd one out. What the shell owns is the *queue state*, so it is told afterwards, and
 * that is the only reason it hears about this at all.
 * </para>
 *
 * <para>
 * Cancel has a real failure mode worth reporting rather than swallowing: an item that finished between the
 * last poll and this click cannot be cancelled, and the API says so by returning false rather than throwing.
 * "I pressed stop and nothing happened" is precisely the case where silence is worst.
 * </para>
 */
async function interrupt() {
  const itemId = runningId.value
  if (itemId === null || busy.value) {
    return
  }

  busy.value = true
  try {
    const canceled = await props.api.cancel(itemId)
    if (!canceled) {
      emit('failed', `'${itemId}' 已完成，无法取消`)
      return
    }
    emit('cancel', itemId)
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

/**
 * Uploads a set of files, whichever entrance they came through. Picking, dropping and pasting all end here:
 * three entrances to one door, because a door that only opens for the file picker is a door most people walk
 * past.
 */
function takeFiles(files: FileList | File[] | null) {
  if (files === null || files.length === 0 || uploading.value) {
    return
  }

  uploading.value = true
  void (async () => {
    try {
      for (const file of Array.from(files)) {
        // The name goes up with the bytes so the server can sanitise it and hand back the one the model will
        // be told; what is rendered is the server's name, not the browser's.
        const media = await props.api.uploadMedia(file, file.type, file.name)

        // Only an image can be previewed as an image. Fetching the bytes of a PDF to hang them off an <img>
        // would show a broken picture and cost the download.
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
    // The source is what the message *is*, not something the user picks: typing here is always text, the
    // microphone enqueues its own items, and a scheduled task comes from the scheduler.
    // The gear rides this message, not the send: the choice is per turn, so it is sent with the payload and
    // never reset afterwards — the next message starts from the configured default again.
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
 * <para>
 * A command is a whole line: "搜一下 /status 的实现" is a sentence about a command, not the command, and
 * swallowing it would send nothing at all. Getting that backwards is the failure this anchoring prevents.
 * </para>
 */
function tryCommand(): boolean {
  const command = parseComposerCommand(draft.value)
  if (command === null) {
    return false
  }

  if (command.kind === 'status') {
    draft.value = ''
    queueOpen.value = !queueOpen.value
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
 * <para>
 * A retraction is physical: the text leaves the log the moment it is confirmed, and the tombstone left behind
 * (`purge #a..#b`) records the range, not the words. Without this, "撤回" on your own message means retyping
 * it from memory — the one destructive action with no way back at all.
 * </para>
 */
function restore(body: string) {
  draft.value = body
  queueOpen.value = false
  focus()
}

onUnmounted(() => {
  attachments.value.forEach(release)
})

defineExpose({ focus, restore })
</script>

<template>
  <div
    class="composer"
    :class="{ 'is-dragging': dragging }"
    data-test="composer"
    @dragover.prevent="dragging = true"
    @dragleave="dragging = false"
    @drop="onDrop"
    @paste="onPaste"
  >
    <div
      v-if="stripOpen"
      class="composer-strip"
      data-test="composer-strip"
    >
      <span v-if="queueStatus">{{ queueStatus }}</span>
      <span v-if="consumerNote" class="muted" data-test="composer-consumer">{{ consumerNote }}</span>
      <span class="muted" data-test="composer-tools">{{ toolsNote }}</span>
      <span class="spacer" />
      <button
        v-if="listed.length > 0"
        class="ghost"
        data-test="composer-queue-toggle"
        :aria-expanded="queueOpen"
        :aria-label="queueOpen ? '收起队列清单' : '展开队列清单'"
        @click="queueOpen = !queueOpen"
      >
        <Icon :name="queueOpen ? 'chevron-down' : 'chevron-up'" :size="14" />
        队列清单
      </button>
      <button class="ghost" data-test="composer-strip-toggle" aria-label="收起状态条" @click="stripOpen = false">
        <Icon name="chevron-up" :size="14" />
      </button>
    </div>

    <!-- The list behind the badge, from the same answer the badge was computed from. -->
    <ol v-if="queueOpen" class="queue-items" data-test="composer-queue-list">
      <li v-for="item in listed" :key="item.itemId" :data-status="item.status" data-test="composer-queue-item">
        <span class="queue-state">{{ STATUS_TEXT[item.status] ?? item.status }}</span>
        <span class="queue-id">{{ item.itemId }}</span>
        <span class="queue-payload">{{ item.payload }}</span>
        <span v-if="item.error" class="queue-error">{{ item.error }}</span>
      </li>
      <li v-if="listed.length === 0" class="queue-empty">（队列为空）</li>
    </ol>

    <div v-if="attachments.length > 0" class="attachments" data-test="composer-attachments">
      <span
        v-for="attachment in attachments"
        :key="attachment.media.mediaId"
        class="attachment"
        data-test="composer-attachment"
        :title="attachment.media.fileName"
      >
        <img v-if="attachment.preview" :src="attachment.preview" alt="附图预览" />
        <span v-else class="file-glyph"><Icon name="file" :size="14" /></span>
        <span class="file-name">{{ attachment.media.fileName }}</span>
        <span class="meta">{{ Math.round(attachment.media.bytes / 1024) }} KB</span>
        <button
          class="ghost"
          data-test="composer-detach"
          :title="'移除附件'"
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
        data-test="composer-input"
        aria-label="输入消息"
        placeholder="输入消息…（Enter 发送，Shift+Enter 换行；/status 查看队列）"
        @keydown.enter.exact="onEnter"
      />
    </div>

    <div class="composer-tools">
      <select
        v-if="approval"
        class="gear"
        :value="approval"
        aria-label="审批挡位"
        data-test="composer-approval"
        @change="emit('approval', ($event.target as HTMLSelectElement).value)"
      >
        <option v-for="gear in APPROVAL_GEARS" :key="gear.value" :value="gear.value" :title="gear.hint">
          审批：{{ gear.label }}
        </option>
      </select>

      <select
        v-if="gearOptions.length > 0"
        class="gear"
        :value="gear"
        aria-label="思考挡位"
        data-test="composer-gear"
        @change="gear = ($event.target as HTMLSelectElement).value"
      >
        <option v-for="option in gearOptions" :key="option" :value="option">思考：{{ option }}</option>
      </select>

      <label class="ghost attach" :class="{ busy: uploading }" data-test="composer-attach">
        {{ uploading ? '上传中…' : '＋ 附件' }}
        <input
          type="file"
          multiple
          data-test="composer-attach-input"
          @change="onPicked"
        />
      </label>
      <span class="spacer" />
      <span v-if="!stripOpen && queueStatus" class="meta">{{ queueStatus }}</span>
      <button
        v-if="!stripOpen"
        class="ghost"
        data-test="composer-strip-toggle"
        aria-label="展开状态条"
        @click="stripOpen = true"
      >
        <Icon name="chevron-down" :size="14" />
      </button>
      <!--
        One button, four states, decided by whether the box has anything in it and whether a turn is running.
        `composer-send` stays on the two states that submit, because that is what the tests drive; the
        interrupt is a different action and gets its own hook rather than overloading one.
      -->
      <button
        v-if="action.kind === 'interrupt'"
        class="danger"
        data-test="composer-interrupt"
        :disabled="busy"
        :title="`中断正在运行的 ${runningId}`"
        @click="interrupt"
      >
        {{ busy ? '中断中…' : action.label }}
      </button>
      <button v-else class="primary" :disabled="!canSend" data-test="composer-send" @click="send">
        {{ action.label }}
        <Icon name="return" :size="14" label="Enter 发送" />
      </button>
    </div>
  </div>
</template>

<style scoped>
.composer-strip .spacer {
  flex: 1;
}

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
  max-inline-size: 100%;
  min-inline-size: 0;
}

.attachment img {
  block-size: 32px;
  inline-size: 32px;
  flex: none;
  object-fit: cover;
  border-radius: var(--radius-sm);
}

.file-glyph {
  flex: none;
  color: var(--text-2);
  padding-inline: 0.2em;
}

/* A long path has to give way somewhere, and the middle is where a reader does not look. */
.file-name {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-inline-size: 16rem;
}

.queue-items {
  list-style: none;
  margin: 0;
  padding: var(--space-1) var(--space-3);
  display: grid;
  gap: 2px;
  border-block-end: 1px solid var(--line);
  /* A scroll container, which ui.md §2 otherwise forbids — and deliberately so. This one is opt-in: it appears
     only when someone asks for it (/status, or the ⌃ button), it is capped so a fifty-deep queue cannot push
     the input box off the window, and it goes away when dismissed. The rule targets the page layout — the
     top bar, the sidebar and the composer itself must never scroll — and a bounded list the reader opened on
     purpose is none of those. */
  max-block-size: 12rem;
  overflow-y: auto;
}

.queue-items > li {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
  font-size: var(--fs-meta);
  min-inline-size: 0;
}

.queue-state {
  flex: none;
  min-inline-size: 3.5em;
  color: var(--text-3);
}

.queue-items > li[data-status='Running'] .queue-state {
  color: var(--accent);
}

.queue-items > li[data-status='Failed'] .queue-state,
.queue-error {
  color: var(--error);
}

.queue-id {
  flex: none;
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
}

.queue-payload {
  flex: 1;
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.queue-error {
  flex: none;
  max-inline-size: 40%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.queue-empty {
  color: var(--text-3);
}

/* The drop target has to announce itself: a paste that silently fails is worse than one that is refused. */
.composer.is-dragging {
  outline: 2px dashed var(--accent);
  outline-offset: -2px;
}

.gear {
  font-size: var(--fs-meta);
}

.attach {
  /* The file picker is a `<label>`, not a `<button>`, so the `.ghost` tier in styles.css — which is written
     against the element — never reached it and "＋ 附件" rendered as plain text while opening a file dialog
     on click. The tokens are the tier's, repeated here, because the component owns the element and the tier
     rules are about `<button>`. */
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  padding: var(--space-1) var(--space-3);
  border: 1px solid transparent;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--text-2);
  cursor: pointer;
  white-space: nowrap;
  font-size: var(--fs-ui);
}

.attach:hover {
  background: var(--surface-2);
  color: var(--text);
}

.attach:focus-within {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
  border-radius: var(--radius-sm);
}

.attach input {
  display: none;
}

.attach.busy {
  color: var(--text-2);
  cursor: progress;
}
</style>
