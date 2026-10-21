<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { t } from '../i18n'

/**
 * The one save control for every page that edits a document (settings, prompts, MCP, skills).
 *
 * <para>
 * It exists only while there is something to say: unsaved edits, a result worth a few seconds, or an error. A
 * page with nothing to save has no save button on it, which is most of what "low density" means here. It floats
 * over the bottom of the page, so a long form never needs a scroll to reach it.
 * </para>
 *
 * <para>
 * Discarding is confirmed in place — the first press turns the button into 「确认放弃」 — because a native
 * `confirm()` blocks the webview and cannot be driven in a test. Esc backs out.
 * </para>
 */
const props = withDefaults(
  defineProps<{
    /** Prefix for the `data-test` hooks: `${prefix}-save`, `-discard`, `-dirty`, and the status line. */
    prefix: string
    dirty: boolean
    busy?: boolean | undefined
    status?: string | undefined
    /** The status line's own hook, for the pages whose tests predate the prefix scheme. */
    statusTest?: string | undefined
    error?: string | undefined
    errors?: readonly string[] | undefined
    /** Save is refused for a reason the page can name (a prompt over its limit). */
    blockedReason?: string | undefined
    saveLabel?: string | undefined
    /** Offered only by pages that can throw their drafts away. */
    canDiscard?: boolean | undefined
  }>(),
  {
    busy: false,
    status: '',
    statusTest: undefined,
    error: '',
    errors: () => [],
    blockedReason: '',
    canDiscard: false,
  },
)

const emit = defineEmits<{ save: []; discard: [] }>()

/** The result line fades after a few seconds; a standing 「已保存」 would be a sentence about nothing. */
const STATUS_MS = 4000
const statusShown = ref(false)
let statusTimer: ReturnType<typeof setTimeout> | undefined

watch(
  () => props.status,
  (value) => {
    clearTimeout(statusTimer)
    statusShown.value = value !== ''
    if (value !== '') {
      statusTimer = setTimeout(() => {
        statusShown.value = false
      }, STATUS_MS)
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => clearTimeout(statusTimer))

const confirming = ref(false)

watch(
  () => props.dirty,
  (dirty) => {
    if (!dirty) confirming.value = false
  },
)

function onDiscard(): void {
  if (!confirming.value) {
    confirming.value = true
    return
  }

  confirming.value = false
  emit('discard')
}

const visible = () =>
  props.dirty || props.blockedReason !== '' || statusShown.value || props.error !== '' || props.errors.length > 0
</script>

<template>
  <div
    v-if="visible()"
    class="savebar g-float"
    :data-test="`${prefix}-savebar`"
    @keydown.escape="confirming = false"
  >
    <div class="savebar-main">
      <span v-if="dirty" class="savebar-note" :data-test="`${prefix}-dirty`">{{ t('savebar.dirty') }}</span>
      <span
        v-if="statusShown"
        class="savebar-note is-ok"
        role="status"
        :data-test="statusTest ?? `${prefix}-status`"
      >{{ status }}</span>
      <span v-if="blockedReason" class="savebar-note is-error" :data-test="`${prefix}-over-limit`">{{ blockedReason }}</span>
      <span v-if="error" class="savebar-note is-error" data-test="error">{{ error }}</span>
      <span class="spacer" />
      <template v-if="dirty || blockedReason !== ''">
        <template v-if="canDiscard && dirty">
          <button
            class="ghost"
            :class="{ danger: confirming }"
            :data-test="`${prefix}-discard`"
            @click="onDiscard"
          >
            {{ confirming ? t('savebar.confirmDiscard') : t('savebar.discard') }}
          </button>
          <button v-if="confirming" class="ghost" @click="confirming = false">{{ t('savebar.cancel') }}</button>
        </template>
        <button
          class="primary"
          :disabled="busy || blockedReason !== ''"
          :aria-busy="busy || undefined"
          :data-test="`${prefix}-save`"
          @click="emit('save')"
        >
          {{ saveLabel ?? t('savebar.save') }}
        </button>
      </template>
    </div>
    <ul v-if="(errors?.length ?? 0) > 0" class="savebar-errors" :data-test="`${prefix}-errors`">
      <li v-for="(entry, index) in errors" :key="index">{{ entry }}</li>
    </ul>
  </div>
</template>

<style scoped>
.savebar {
  position: absolute;
  inset-inline: 0;
  inset-block-end: var(--space-4);
  z-index: 20;
  margin-inline: auto;
  inline-size: min(40rem, calc(100% - var(--space-5) * 2));
  padding: var(--space-2) var(--space-2) var(--space-2) var(--space-5);
}

.savebar-main {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-block-size: 2.5rem;
  flex-wrap: wrap;
}

.savebar-note {
  color: var(--text-2);
  font-size: var(--fs-ui);
}

.savebar-note.is-ok {
  color: var(--ok);
}

.savebar-note.is-error {
  color: var(--error);
}

.savebar-errors {
  margin: 0 0 var(--space-2);
  padding-inline: var(--space-5) var(--space-4);
  color: var(--error);
  font-size: var(--fs-meta);
  line-height: 1.6;
}
</style>
