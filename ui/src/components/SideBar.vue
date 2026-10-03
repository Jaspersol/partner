<script setup lang="ts">
import { nextTick, ref } from 'vue'
import type { RebuildReport } from '../api'
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

const props = defineProps<{ items: NavItem[]; active: string; collapsed: boolean; rebuild: RebuildReport | null }>()

const emit = defineEmits<{
  select: [key: string]
  'new-query': []
  rebuild: []
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
    A rail, and a button.

    <para>
    The obvious alternative was a panel that peeks open on hover. It is the wrong shape: the pointer sits on
    the track, so anything that widens the track grows *under* the cursor and the content under it reflows.
    Making it an overlay instead costs a whole second layout mechanism — and one that nothing can test, since
    the only place hover lives is a real browser. So the rail is rail, and one button opens it (ui.md §12 S5).
    </para>
  -->
  <nav class="sidebar" :class="{ 'is-collapsed': collapsed }" data-test="sidebar">
    <div class="sidebar-head">
      <button class="primary new-query" data-test="sidebar-new-query" :title="'新建查询'" @click="emit('new-query')">
        <Icon name="plus" :size="16" />
        <span class="nav-label">新建查询</span>
      </button>
    </div>

    <div class="nav-section-title">工作区</div>

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
        <span class="icon"><Icon :name="item.icon" :size="16" /></span>
        <span class="nav-label">{{ item.label }}</span>
      </li>
    </ul>

    <div class="spacer" />

    <!-- Destructive and rare: kept away from navigation, behind a divider (ui.md §4). -->
    <div class="sidebar-extra">
      <button class="ghost" data-test="sidebar-rebuild" title="重建投影" @click="emit('rebuild')">
        <Icon name="refresh" :size="14" />
        <span class="nav-label">重建投影</span>
      </button>
      <div v-if="rebuild" class="meta" data-test="sidebar-rebuild-report">
        会话 {{ rebuild.sessionItems }} · 索引 {{ rebuild.memoryIndexed }} · 队列日志
        {{ rebuild.queueLogEntries }}
      </div>
    </div>
  </nav>
</template>

<style scoped>
.sidebar {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: var(--space-2);
  min-inline-size: 0;
  min-block-size: 0;
  border-inline-end: 1px solid var(--line);
  background: var(--surface-1);
}

.sidebar-head {
  display: flex;
}

.new-query {
  inline-size: 100%;
  justify-content: flex-start;
}

.nav-section-title {
  font-size: var(--fs-meta);
  line-height: var(--lh-meta);
  color: var(--text-3);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding-inline: var(--space-2);
  white-space: nowrap;
  overflow: hidden;
}

.nav {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 2px;
}

.nav > li {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2);
  border-radius: var(--radius-sm);
  /* A row the reader cannot name is not navigable by sight, and the rail is six rows tall. */
  white-space: nowrap;
  overflow: hidden;
  cursor: pointer;
}

.nav > li:hover {
  background: var(--surface-2);
}

/* Selection is a thin accent bar plus a very light fill — no large saturated block (ui.md §4). */
.nav > li.active {
  background: var(--selected);
  box-shadow: inset 2px 0 0 var(--accent-pressed);
}

.icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  inline-size: 1.35em;
  /* A fixed box, so the labels of six rows share a left edge whether the icon is a page or a plug. */
  flex: none;
  color: var(--text-2);
}

.nav > li.active .icon,
.nav > li:hover .icon {
  color: var(--text);
}

.nav-label {
  overflow: hidden;
  text-overflow: ellipsis;
}

.spacer {
  flex: 1;
}

.sidebar-extra {
  border-block-start: 1px solid var(--line);
  padding-block-start: var(--space-2);
  display: grid;
  gap: var(--space-1);
  justify-items: start;
}

/* ---- the rail --------------------------------------------------------------------------------
   Collapsed is now the *normal* narrow state rather than a compromise forced on a small window, so it gets
   the same care the expanded panel has: centred icons, a square icon button, and no half-measures like a
   section heading squeezed to three characters. */
.sidebar.is-collapsed {
  gap: var(--space-1);
  padding: var(--space-2) var(--space-1);
  align-items: center;
}

.sidebar.is-collapsed .nav-section-title,
.sidebar.is-collapsed .nav-label {
  display: none;
}

.sidebar.is-collapsed .sidebar-head,
.sidebar.is-collapsed .nav,
.sidebar.is-collapsed .sidebar-extra {
  inline-size: 100%;
}

.sidebar.is-collapsed .new-query {
  inline-size: 100%;
  padding-inline: 0;
  justify-content: center;
}

.sidebar.is-collapsed .nav > li {
  justify-content: center;
  padding-inline: 0;
  /* The bar moves inside now: with no left gutter there is nothing outside it to align to. */
  box-shadow: none;
}

.sidebar.is-collapsed .nav > li.active {
  box-shadow: inset 0 0 0 1px var(--accent-pressed);
}

.sidebar.is-collapsed .sidebar-extra {
  justify-items: center;
}

.sidebar.is-collapsed .sidebar-extra .ghost {
  padding-inline: 0;
}
</style>