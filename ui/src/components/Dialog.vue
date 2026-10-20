<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'

/**
 * A short form in the middle of the page, over a dimmed scrim.
 *
 * Absolutely positioned in the nearest positioned ancestor, so it covers its view; Esc and the scrim close it.
 * Focus moves to the first field on open and back to the opener on close.
 */
const props = defineProps<{ open: boolean; title: string }>()
const emit = defineEmits<{ close: [] }>()

const panel = ref<HTMLElement | null>(null)
let opener: HTMLElement | null = null

watch(
  () => props.open,
  async (open) => {
    if (open) {
      opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
      await nextTick()
      const target = panel.value?.querySelector<HTMLElement>('textarea, input, select')
      ;(target ?? panel.value)?.focus()
    } else {
      opener?.focus?.()
      opener = null
    }
  },
)
</script>

<template>
  <Transition name="dialog">
    <div v-if="open" class="dialog-layer" data-test="dialog">
      <div class="dialog-scrim" @click="emit('close')" />
      <section
        ref="panel"
        class="dialog g-float"
        role="dialog"
        aria-modal="true"
        :aria-label="title"
        tabindex="-1"
        @keydown.escape.stop="emit('close')"
      >
        <h2>{{ title }}</h2>
        <div class="dialog-body"><slot /></div>
        <footer v-if="$slots.footer" class="dialog-foot"><slot name="footer" /></footer>
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.dialog-layer {
  position: absolute;
  inset: 0;
  z-index: 50;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  place-items: center;
  padding: var(--space-4);
}

.dialog-scrim {
  position: absolute;
  inset: 0;
  background: rgb(0 0 0 / 0.22);
}

.dialog {
  position: relative;
  inline-size: min(30rem, 100%);
  padding: var(--space-5);
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-4);
}

.dialog h2 {
  font-size: 1rem;
}

.dialog-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
}

.dialog-foot {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-2);
}

.dialog-enter-active,
.dialog-leave-active {
  transition: opacity var(--motion-base) var(--ease);
}

.dialog-enter-active .dialog,
.dialog-leave-active .dialog {
  transition: transform var(--motion-base) var(--ease);
}

.dialog-enter-from,
.dialog-leave-to {
  opacity: 0;
}

.dialog-enter-from .dialog,
.dialog-leave-to .dialog {
  transform: translateY(0.5rem) scale(0.98);
}
</style>
