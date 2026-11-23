<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'

const props = defineProps<{ open: boolean; title: string; wide?: boolean }>()
const emit = defineEmits<{ close: [] }>()

const panel = ref<HTMLDialogElement | null>(null)
let opener: HTMLElement | null = null
let pressedBackdrop = false

watch(
  () => props.open,
  async (open) => {
    if (open) {
      opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
      await nextTick()
      const dialog = panel.value
      if (!props.open || !dialog || dialog.open) return
      pressedBackdrop = false
      dialog.showModal()
      dialog.querySelector<HTMLElement>('textarea:not(:disabled), input:not(:disabled), select:not(:disabled)')?.focus()
    } else {
      panel.value?.close()
      opener?.focus()
      opener = null
    }
  },
  { immediate: true },
)
</script>

<template>
  <dialog
    v-if="open"
    ref="panel"
    class="dialog g-float"
    :class="{ 'is-wide': wide }"
    :aria-label="title"
    data-test="dialog"
    @cancel.prevent="emit('close')"
    @keydown.stop
    @pointerdown="pressedBackdrop = $event.target === $event.currentTarget"
    @click.self="pressedBackdrop && emit('close')"
  >
    <div class="dialog-content">
      <h2>{{ title }}</h2>
      <div class="dialog-body"><slot /></div>
      <footer v-if="$slots.footer" class="dialog-foot"><slot name="footer" /></footer>
    </div>
  </dialog>
</template>

<style scoped>
.dialog {
  inline-size: min(30rem, calc(100% - 2 * var(--space-4)));
  max-inline-size: none;
  max-block-size: calc(100dvh - 2 * var(--space-4));
  padding: 0;
  overflow: hidden;
  color: var(--text);
}

.dialog[open] {
  animation: dialog-in var(--motion-base) var(--ease);
}

.dialog::backdrop {
  background: rgb(0 0 0 / 0.28);
}

.dialog.is-wide {
  inline-size: min(44rem, calc(100% - 2 * var(--space-4)));
}

.dialog-content {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  grid-template-rows: auto minmax(0, 1fr) auto;
  max-block-size: inherit;
  padding: var(--space-5);
  gap: var(--space-4);
}

.dialog h2 {
  font-size: 1rem;
}

.dialog-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  align-content: start;
  gap: var(--space-3);
  min-block-size: 0;
  overflow: auto;
  overscroll-behavior: contain;
}

.dialog-foot {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-2);
}

@keyframes dialog-in {
  from {
    opacity: 0;
    transform: translateY(0.5rem) scale(0.98);
  }
}
</style>
