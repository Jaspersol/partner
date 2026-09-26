<script setup lang="ts">
import { onMounted, ref } from 'vue'
import type { MemoryItem, MemoryRevision, PartnerApiSurface } from '../api'

const props = defineProps<{ api: PartnerApiSurface }>()

interface Row {
  recordId: string
  tier: string
  scope: string
  body: string
  validity: string
  hit?: string
}

const rows = ref<Row[]>([])
const selected = ref<Row | null>(null)
const draft = ref('')
const revisions = ref<MemoryRevision[]>([])
const query = ref('')
const summary = ref('')
const error = ref('')

function toRow(item: MemoryItem): Row {
  return {
    recordId: item.recordId,
    tier: item.tier,
    scope: item.scope,
    body: item.body,
    // The edit model is visible in the list: superseded records say so instead of disappearing silently.
    validity:
      item.validTo === null
        ? `有效 · 编辑者 ${item.editedBy ?? '原始'}`
        : `已失效 → ${item.supersededBy ?? '(无后继)'}`,
  }
}

async function refresh() {
  error.value = ''
  try {
    const items = await props.api.memory()
    rows.value = items.map(toRow)
    summary.value = `${rows.value.length} 条有效记忆`
  } catch (failure) {
    error.value = String(failure)
  }
}

async function recall() {
  error.value = ''
  const text = query.value.trim()

  if (text === '') {
    await refresh()
    return
  }

  try {
    const hits = await props.api.recall(text)
    rows.value = hits.map((hit) => ({
      recordId: hit.recordId,
      tier: 'recall',
      scope: '-',
      body: hit.body,
      validity: `命中 ${hit.total.toFixed(3)} · ${hit.reasons.join(', ')}`,
      hit: hit.recordId,
    }))
    summary.value = `检索「${text}」→ ${hits.length} 条命中`
  } catch (failure) {
    error.value = String(failure)
  }
}

async function select(row: Row) {
  selected.value = row
  draft.value = row.body

  try {
    revisions.value = await props.api.memoryRevisions(row.recordId)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save() {
  const row = selected.value
  if (!row) {
    error.value = '先选中一条记忆'
    return
  }
  if (draft.value.trim() === '') {
    error.value = '正文不能为空'
    return
  }

  error.value = ''
  try {
    await props.api.editMemory(row.recordId, draft.value.trim())
    await refresh()
    const updated = rows.value.find((candidate) => candidate.recordId === row.recordId)
    if (updated) {
      await select(updated)
    }
  } catch (failure) {
    error.value = String(failure)
  }
}

async function remove() {
  const row = selected.value
  if (!row) {
    error.value = '先选中一条记忆'
    return
  }

  error.value = ''
  try {
    await props.api.deleteMemory(row.recordId)
    selected.value = null
    draft.value = ''
    revisions.value = []
    await refresh()
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)
defineExpose({ refresh })
</script>

<template>
  <div class="split">
    <section class="pane">
      <header>
        <button @click="refresh">刷新</button>
        <input v-model="query" placeholder="检索…" data-test="memory-query" />
        <button @click="recall" data-test="memory-recall">检索</button>
        <span class="muted" data-test="memory-summary">{{ summary }}</span>
      </header>

      <ul class="list" data-test="memory-list">
        <li
          v-for="row in rows"
          :key="row.recordId"
          :class="{ selected: row.recordId === selected?.recordId }"
          @click="select(row)"
        >
          <div class="meta">{{ row.recordId }} {{ row.tier }} / {{ row.scope }}</div>
          <div>{{ row.body }}</div>
          <div class="validity">{{ row.validity }}</div>
        </li>
      </ul>
    </section>

    <section class="pane detail">
      <h2>编辑选中记忆（旧记录软失效并标记 supersededBy）</h2>
      <p class="muted" data-test="memory-target">{{ selected?.recordId ?? '未选中' }}</p>

      <textarea v-model="draft" rows="5" data-test="memory-draft" />
      <div class="row">
        <button @click="save" data-test="memory-save">保存</button>
        <button @click="remove" data-test="memory-delete">删除</button>
      </div>

      <h3>修订历史</h3>
      <ul class="list compact" data-test="memory-revisions">
        <li v-for="(revision, index) in revisions" :key="index">
          <span class="muted">{{ revision.actor }} {{ revision.at.slice(11, 19) }}</span> {{ revision.after }}
        </li>
      </ul>

      <p v-if="error" class="error" data-test="error">{{ error }}</p>
    </section>
  </div>
</template>
