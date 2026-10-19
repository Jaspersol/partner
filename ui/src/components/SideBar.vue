<script setup lang="ts">
import { nextTick, ref } from 'vue'
import type { IconName } from '../icons'
import { isActivationKey, rovingTarget } from '../rovingFocus'
import Icon from './Icon.vue'

export interface NavItem {
  key: string
  label: string
  /** A name from the icon set, not a character: an unknown name is a type error, and an unknown character
   *  is a blank square that reaches a reader. */
  icon: IconName
}

const props = defineProps<{
  items: NavItem[]
  active: string
  collapsed: boolean
  /** The window is too narrow for the sidebar to open at all, so the toggle is not offered. */
  toggleHidden?: boolean | undefined
}>()

const emit = defineEmits<{
  select: [key: string]
  'update:collapsed': [value: boolean]
}>()

/**
 * One tab stop, arrows to move.
 *
 * <para>
 * Horizontal, and **wrapping**: a rail has no "below", so Right from the last view landing on the first is what
 * every tab bar does, and a reader who overshoots a six-item rail should come back round rather than stop.
 * </para>
 */
const focusIndex = ref(0)
const listElement = ref<HTMLElement | null>(null)

function onKeydown(event: KeyboardEvent, index: number) {
  const target = rovingTarget(index, props.items.length, event.key, { axis: 'horizontal' })
  if (target === null) {
    if (isActivationKey(event.key)) {
      event.preventDefault()
      choose(index)
    }
    return
  }

  event.preventDefault()
  focusIndex.value = target
  void nextTick(() => {
    listElement.value?.querySelectorAll<HTMLElement>('[data-row]')[target]?.focus()
  })
}

function choose(index: number): void {
  const item = props.items[index]
  if (item === undefined) return
  focusIndex.value = index
  emit('select', item.key)
}
</script>

<template>
  <!--
    A floating glass rail, inset from the window edge so the page reads as a surface behind it.

    <para>
    The obvious alternative was a panel that peeks open on hover. It is the wrong shape: the pointer sits on
    the track, so anything that widens the track grows *under* the cursor and the content under it reflows. So
    the rail is a rail, and one button opens it (ui.md §12 S5).
    </para>

    <para>
    Navigation and nothing else: an action a reader cannot act on is a false promise on a rail.
    </para>
  -->
  <nav class="sidebar g-float" :class="{ 'is-collapsed': collapsed }" data-test="sidebar" aria-label="主导航">
    <!-- The one place the sidebar is opened and closed (Ctrl+B does the same). -->
    <div class="brand">
      <button
        v-if="toggleHidden !== true"
        class="ghost brand-toggle"
        type="button"
        data-test="sidebar-toggle"
        :aria-label="collapsed ? '展开侧栏' : '收起侧栏'"
        :title="`${collapsed ? '展开侧栏' : '收起侧栏'}（Ctrl+B）`"
        @click="emit('update:collapsed', !collapsed)"
      >
        <Icon name="panel-left" :size="18" />
      </button>
      <span class="brand-name" aria-hidden="true">Partner</span>
    </div>

    <ul ref="listElement" class="nav" role="list" aria-label="视图导航">
      <li
        v-for="(item, index) in items"
        :key="item.key"
        :class="{ active: item.key === active }"
        :data-test="`tab-${item.label}`"
        :data-row="index"
        :title="item.label"
        :tabindex="index === focusIndex ? 0 : -1"
        :aria-current="item.key === active ? 'true' : undefined"
        @click="choose(index)"
        @focus="focusIndex = index"
        @keydown="onKeydown($event, index)"
      >
        <span class="icon"><Icon :name="item.icon" :size="18" /></span>
        <span class="nav-label">{{ item.label }}</span>
      </li>
    </ul>
  </nav>
</template>

<style scoped>
.sidebar {
  display: flex;
  flex-direction: column;
  gap: var(--space-4);
  margin: var(--space-3) 0 var(--space-3) var(--space-3);
  padding: var(--space-4) var(--space-3);
  min-inline-size: 0;
  min-block-size: 0;
  border-radius: var(--radius-md);
  box-shadow: var(--glass-hi), var(--shadow-1);
  z-index: 12;
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding-inline: var(--space-2);
  white-space: nowrap;
  overflow: hidden;
}

.brand-toggle {
  flex: none;
  background: color-mix(in srgb, var(--text) 5%, transparent);
  inline-size: 2.25rem;
  min-block-size: 2.25rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.brand-name {
  font-weight: 600;
  letter-spacing: 0.01em;
}

.nav {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-1);
}

.nav > li {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-block-size: 2.5rem;
  padding: 0 var(--space-3);
  border-radius: var(--radius-sm);
  color: var(--text-2);
  /* A row the reader cannot name is not navigable by sight, and the rail is six rows tall. */
  white-space: nowrap;
  overflow: hidden;
  cursor: pointer;
}

.nav > li:hover {
  background: var(--surface-2);
  color: var(--text);
}

/* Selection is a soft filled pill: the page you are on, not a bar pointing at it. */
.nav > li.active {
  background: var(--accent-soft);
  color: var(--accent-pressed);
  font-weight: 500;
}

.icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  inline-size: 1.35em;
  /* A fixed box, so the labels of six rows share a left edge whether the icon is a page or a plug. */
  flex: none;
}

.nav-label {
  overflow: hidden;
  text-overflow: ellipsis;
}

/* ---- the rail --------------------------------------------------------------------------------
   Collapsed is the *normal* narrow state, so it gets the same care as the open panel: centred icons, a square
   hit target, and no half-measures like a heading squeezed to three characters. */
.sidebar.is-collapsed {
  padding-inline: var(--space-2);
  align-items: center;
}

.sidebar.is-collapsed .brand-name,
.sidebar.is-collapsed .nav-label {
  display: none;
}

.sidebar.is-collapsed .brand {
  padding-inline: 0;
}

.sidebar.is-collapsed .nav {
  inline-size: 100%;
}

.sidebar.is-collapsed .nav > li {
  justify-content: center;
  padding-inline: 0;
}
</style>
