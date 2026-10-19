<script setup lang="ts">
import HintTip from './HintTip.vue'

/**
 * One settings row: the name (and its ⓘ) on the left, the control on the right.
 *
 * <para>
 * A `<label>`, so the whole row is the hit target for a switch or a field and the name is the control's
 * accessible name. The ⓘ stops its own click (see HintTip), so reading a hint never toggles the row. `stack`
 * puts the control under the name for the wide ones — a text area, a list, the reasoning dial.
 * </para>
 */
withDefaults(
  defineProps<{
    label: string
    /** A short plain-language sentence for the ⓘ. Markup goes in the `hint` slot instead. */
    hint?: string | undefined
    stack?: boolean | undefined
    /** A unit shown after the control (毫秒, 秒, token…) so it never has to live in the label. */
    unit?: string | undefined
    disabled?: boolean | undefined
  }>(),
  { hint: undefined, stack: false, unit: undefined, disabled: false },
)
</script>

<template>
  <label class="g-row" :class="{ 'is-stack': stack, 'is-disabled': disabled, 'has-unit': unit }">
    <span class="g-row-name">
      <span class="g-row-label">{{ label }}</span>
      <HintTip v-if="hint || $slots.hint"><slot name="hint">{{ hint }}</slot></HintTip>
    </span>
    <span class="g-row-control"><slot /><span v-if="unit" class="g-unit">{{ unit }}</span></span>
  </label>
</template>

<style scoped>
.g-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  min-block-size: 3.25rem;
  padding-block: var(--space-2);
  border-block-start: 1px solid var(--glass-edge);
}

.g-row:first-child {
  border-block-start: none;
}

.g-row.is-stack {
  flex-direction: column;
  align-items: stretch;
  gap: var(--space-2);
}

.g-row.is-disabled {
  opacity: 0.55;
}

.g-row-name {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  min-inline-size: 0;
  flex: 0 1 auto;
}

.g-row-label {
  font-size: var(--fs-ui);
  color: var(--text);
}

.g-row-control {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-2);
  min-inline-size: 0;
  flex: 1 1 auto;
}

.g-row.is-stack .g-row-control {
  justify-content: stretch;
}

.g-row-control > input:not([type='checkbox']),
.g-row-control > select {
  inline-size: min(100%, 20rem);
}

.g-row.is-stack .g-row-control > input:not([type='checkbox']),
.g-row.is-stack .g-row-control > select {
  inline-size: 100%;
}

/* The unit sits inside the field's right edge, so every number in a column ends at the same place whether or not it
   has one, and a long label never has to carry it. */
.g-unit {
  position: absolute;
  inset-inline-end: var(--space-3);
  color: var(--text-3);
  font-size: var(--fs-meta);
  pointer-events: none;
}

.has-unit .g-row-control > input {
  padding-inline-end: calc(var(--space-3) + 3rem);
}

.g-row-control > input[type='number'] {
  inline-size: 8rem;
  text-align: end;
}
</style>
