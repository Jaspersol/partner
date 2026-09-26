<script setup lang="ts">
import type { RebuildReport } from '../api'

export interface NavItem {
  key: string
  label: string
  icon: string
}

defineProps<{ items: NavItem[]; active: string; collapsed: boolean; rebuild: RebuildReport | null }>()

const emit = defineEmits<{
  select: [key: string]
  'new-query': []
  rebuild: []
}>()
</script>

<template>
  <nav class="sidebar" :class="{ 'is-collapsed': collapsed }" data-test="sidebar">
    <div class="sidebar-head">
      <button class="primary new-query" data-test="sidebar-new-query" @click="emit('new-query')">
        ＋<span class="label"> 新建查询</span>
      </button>
    </div>

    <div class="section-title">工作区</div>

    <ul class="nav">
      <li
        v-for="item in items"
        :key="item.key"
        :class="{ active: item.key === active }"
        :data-test="`tab-${item.label}`"
        @click="emit('select', item.key)"
      >
        <span class="icon">{{ item.icon }}</span>
        <span class="label">{{ item.label }}</span>
        <span v-if="collapsed" class="chevron">›</span>
      </li>
    </ul>

    <div class="spacer" />

    <!-- Destructive and rare: kept away from navigation, behind a divider (ui.md §4). -->
    <div class="sidebar-extra">
      <button class="ghost" data-test="sidebar-rebuild" @click="emit('rebuild')">⟳ 重建投影</button>
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
  overflow: auto;
  border-inline-end: 1px solid var(--line);
  background: var(--pane);
}

.sidebar-head {
  display: flex;
}

.new-query {
  inline-size: 100%;
}

.section-title {
  font-size: var(--fs-section);
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding-inline: var(--space-2);
}

.nav {
  list-style: none;
  margin: 0;
  padding: 0;
}

.nav > li {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
  border-inline-start: 2px solid transparent;
  cursor: pointer;
}

.nav > li:hover {
  background: var(--hover);
}

/* Selection is a thin accent bar plus a very light fill — no large saturated block (ui.md §4). */
.nav > li.active {
  background: var(--selected);
  border-inline-start-color: var(--accent);
}

.icon {
  inline-size: 1.2em;
  text-align: center;
}

.chevron {
  margin-inline-start: auto;
  color: var(--muted);
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
</style>
