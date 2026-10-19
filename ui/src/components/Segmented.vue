<script setup lang="ts">
import { nextTick, ref } from 'vue'

/**
 * 2–4 mutually exclusive options that read as one control.
 *
 * <para>
 * Buttons with `aria-pressed` inside a `role="group"`, matching the `.segmented` rules in styles.css. Arrow keys
 * move the selection the way a radio group does, so a keyboard reader does not tab through every option.
 * </para>
 */
const props = defineProps<{
  modelValue: string
  options: readonly { value: string; label: string; title?: string | undefined }[]
  label: string
}>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const group = ref<HTMLElement | null>(null)

function choose(value: string): void {
  if (value !== props.modelValue) {
    emit('update:modelValue', value)
  }
}

function onKeydown(event: KeyboardEvent, index: number): void {
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0
  if (step === 0) return

  event.preventDefault()
  const count = props.options.length
  const next = (index + step + count) % count
  const option = props.options[next]
  if (option === undefined) return

  choose(option.value)
  void nextTick(() => group.value?.querySelectorAll<HTMLElement>('button')[next]?.focus())
}
</script>

<template>
  <div ref="group" class="segmented" role="group" :aria-label="label">
    <button
      v-for="(option, index) in options"
      :key="option.value"
      type="button"
      :aria-pressed="option.value === modelValue"
      :title="option.title"
      :tabindex="option.value === modelValue ? 0 : -1"
      :data-value="option.value"
      @click="choose(option.value)"
      @keydown="onKeydown($event, index)"
    >
      {{ option.label }}
    </button>
  </div>
</template>
