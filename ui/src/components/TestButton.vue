<script setup lang="ts">
import { computed, ref } from 'vue'
import type { VoiceTestResult } from '../api'

/**
 * 「测试」 for one service block: the button, and what came of the last press.
 *
 * <para>
 * It lives in the block's own title row, because the thing it tests is the block's fields — a test button at the
 * foot of a long card is a button the reader has to scroll to find. The outcome is a chip: green with the
 * latency, or red, in which case the chip opens to show the reason. A long error string never sits in the row.
 * </para>
 *
 * <para>
 * `stale` is the part the old layout could not say. Edit the address and the green tick from the previous press
 * is still on screen, vouching for a configuration that no longer exists. A stale result goes grey and says so.
 * </para>
 */
const props = defineProps<{
  /** The hook prefix: `test-${kind}`, `test-${kind}-result`. */
  kind: string
  label?: string | undefined
  testing: boolean
  result: VoiceTestResult | null | undefined
  /** The block's fields changed after this result was recorded. */
  stale: boolean
  /** The test runs against what is stored, not what is typed — said next to the button when the two differ. */
  unsaved?: boolean | undefined
}>()

const emit = defineEmits<{ test: [] }>()

const open = ref(false)

const chip = computed(() => {
  const result = props.result
  if (!result) return null
  if (props.stale) return { tone: 'is-stale', text: '改动后未重测' }
  return result.ok
    ? { tone: 'is-ok', text: `✓ 通过 · ${result.latencyMs} ms` }
    : { tone: 'is-error', text: '✗ 失败' }
})
</script>

<template>
  <div class="test-button">
    <button
      v-if="chip"
      class="g-chip result"
      :class="chip.tone"
      type="button"
      :aria-expanded="open"
      :data-test="`test-${kind}-result`"
      @click="open = !open"
    >
      {{ chip.text }}
    </button>
    <button
      class="secondary"
      type="button"
      :disabled="testing"
      :aria-busy="testing || undefined"
      :data-test="`test-${kind}`"
      @click="emit('test')"
    >
      {{ testing ? '测试中…' : (label ?? '测试') }}
    </button>
    <p v-if="chip && open && result" class="detail break" :class="{ 'is-error': !result.ok }">
      {{ result.detail }}（{{ result.latencyMs }}ms）
    </p>
    <p v-if="unsaved" class="detail">测试用的是已保存的设置，先保存再测。</p>
  </div>
</template>

<style scoped>
.test-button {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-2);
  min-inline-size: 0;
}

.result {
  min-block-size: 1.75rem;
  border: none;
  box-shadow: none;
  cursor: pointer;
}

.result.is-stale {
  background: var(--surface-2);
  color: var(--text-3);
}

.test-button button.secondary {
  min-block-size: 2rem;
  padding-inline: var(--space-4);
  font-size: var(--fs-meta);
}

.detail {
  flex-basis: 100%;
  margin: 0;
  text-align: end;
  color: var(--text-2);
  font-size: var(--fs-meta);
  line-height: 1.6;
}

.detail.is-error {
  color: var(--error);
}
</style>
