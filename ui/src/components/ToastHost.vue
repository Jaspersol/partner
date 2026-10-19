<script setup lang="ts">
import { toasts } from '../toast'
</script>

<template>
  <div class="toast-host" aria-live="polite" role="status">
    <TransitionGroup name="toast">
      <div v-for="toast in toasts" :key="toast.id" class="toast g-float" :class="`is-${toast.tone}`" data-test="toast">
        {{ toast.text }}
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.toast-host {
  position: fixed;
  inset-inline: 0;
  inset-block-end: var(--space-6);
  z-index: 80;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  justify-items: center;
  gap: var(--space-2);
  pointer-events: none;
}

.toast {
  padding: var(--space-2) var(--space-5);
  border-radius: var(--radius-pill);
  font-size: var(--fs-ui);
  color: var(--text);
}

.toast.is-error {
  color: var(--error);
}

.toast-enter-active,
.toast-leave-active {
  transition:
    opacity var(--motion-base) var(--ease),
    transform var(--motion-base) var(--ease);
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateY(0.5rem);
}
</style>
