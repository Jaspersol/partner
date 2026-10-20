<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import Icon from './Icon.vue'

/**
 * A glass panel that slides in from the right over the page it belongs to.
 *
 * Absolutely positioned inside the nearest positioned ancestor (the view), so it covers that view and not the
 * rail. Esc and the scrim close it; focus moves in when it opens and back to what opened it when it closes.
 */
const props = withDefaults(defineProps<{ open: boolean; title: string; id?: string | undefined; wide?: boolean }>(), {
  id: undefined,
  wide: false,
})
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
  <Transition name="drawer">
    <div v-if="open" class="drawer-layer" data-test="drawer">
      <div class="drawer-scrim" data-test="drawer-scrim" @click="emit('close')" />
      <aside
        :id="id"
        ref="panel"
        class="drawer g-float"
        :class="{ 'is-wide': wide }"
        role="dialog"
        :aria-label="title"
        tabindex="-1"
        @keydown.escape.stop="emit('close')"
      >
        <header class="drawer-head">
          <h2>{{ title }}</h2>
          <button class="ghost drawer-close" data-test="drawer-close" aria-label="关闭" @click="emit('close')">
            <Icon name="x" :size="16" />
          </button>
        </header>
        <div class="drawer-body"><slot /></div>
        <footer v-if="$slots.footer" class="drawer-foot"><slot name="footer" /></footer>
      </aside>
    </div>
  </Transition>
</template>

<style scoped>
.drawer-layer {
  position: absolute;
  inset: 0;
  z-index: 40;
}

.drawer-scrim {
  position: absolute;
  inset: 0;
  background: rgb(0 0 0 / 0.18);
}

.drawer {
  position: absolute;
  inset-block: var(--space-3);
  inset-inline-end: var(--space-3);
  display: flex;
  flex-direction: column;
  inline-size: min(28rem, calc(100% - var(--space-5)));
}

.drawer.is-wide {
  inline-size: min(40rem, calc(100% - var(--space-5)));
}

.drawer-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-4) var(--space-4) var(--space-2) var(--space-5);
}

.drawer-head h2 {
  font-size: 1rem;
}

.drawer-body {
  flex: 1;
  min-block-size: 0;
  overflow: auto;
  padding: var(--space-2) var(--space-5) var(--space-4);
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-4);
  align-content: start;
}

.drawer-foot {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-3) var(--space-5) var(--space-4);
  border-block-start: 1px solid var(--glass-edge);
}

.drawer-enter-active,
.drawer-leave-active {
  transition: opacity var(--motion-base) var(--ease);
}

.drawer-enter-active .drawer,
.drawer-leave-active .drawer {
  transition: transform var(--motion-base) var(--ease);
}

.drawer-enter-from,
.drawer-leave-to {
  opacity: 0;
}

.drawer-enter-from .drawer,
.drawer-leave-to .drawer {
  transform: translateX(1.5rem);
}
</style>
