<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { PartnerApiSurface, SessionRevision, SessionView } from '../api'

const props = defineProps<{ api: PartnerApiSurface }>()

const view = ref<SessionView | null>(null)
const selectedSeq = ref<number | null>(null)
const draft = ref('')
const revisions = ref<SessionRevision[]>([])
const error = ref('')

const selected = computed(() => view.value?.items.find((item) => item.seq === selectedSeq.value) ?? null)

// The original body is never hidden: an edit appends a revision, it does not replace anything.
const revisionNote = computed(() => {
  const item = selected.value
  if (!item || item.revisionCount === 0) {
    return ''
  }

  return `已编辑 ${item.revisionCount} 次 · 原文：${item.originalBody ?? ''}`
})

async function refresh() {
  error.value = ''
  try {
    view.value = await props.api.session()
  } catch (failure) {
    error.value = String(failure)
  }
}

async function select(seq: number) {
  selectedSeq.value = seq
  draft.value = view.value?.items.find((item) => item.seq === seq)?.body ?? ''

  try {
    revisions.value = await props.api.sessionRevisions(seq)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save() {
  const seq = selectedSeq.value
  if (seq === null) {
    error.value = '先选中一条事件'
    return
  }
  if (draft.value.trim() === '') {
    error.value = '正文不能为空'
    return
  }

  error.value = ''
  try {
    await props.api.editSession(seq, draft.value.trim())
    // Rule: the window holds no state — everything is re-read from the API after a write.
    await refresh()
    await select(seq)
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
        <span class="muted">
          {{ view?.items.length ?? 0 }} 条事件 · 最后 seq={{ view?.lastSeq ?? 0 }}
        </span>
      </header>

      <ul class="list" data-test="session-list">
        <li
          v-for="item in view?.items ?? []"
          :key="item.seq"
          :class="{ selected: item.seq === selectedSeq }"
          @click="select(item.seq)"
        >
          <div class="meta">#{{ item.seq }} {{ item.kind }} / {{ item.source }} {{ item.at.slice(11, 19) }}</div>
          <div>{{ item.body }}</div>
          <div v-if="item.revisionCount > 0" class="edited">
            已编辑 {{ item.revisionCount }} 次 · 原文：{{ item.originalBody }}
          </div>
        </li>
      </ul>
    </section>

    <section class="pane detail">
      <h2>编辑选中事件（追加修订，原文保留）</h2>
      <p class="muted" data-test="session-target">
        <template v-if="selected">
          #{{ selected.seq }} · {{ selected.kind }} · {{ selected.source }} · 修订 {{ selected.revisionCount }} 次
        </template>
        <template v-else>未选中</template>
      </p>
      <p v-if="revisionNote" class="edited" data-test="session-revision-note">{{ revisionNote }}</p>

      <textarea v-model="draft" rows="6" data-test="session-draft" />
      <button @click="save" data-test="session-save">保存</button>

      <h3>编辑历史</h3>
      <ul class="list compact" data-test="session-revisions">
        <li v-for="revision in revisions" :key="revision.seq">
          <span class="muted">{{ revision.at.slice(11, 19) }}</span> {{ revision.body }}
        </li>
      </ul>

      <p v-if="error" class="error" data-test="error">{{ error }}</p>
    </section>
  </div>
</template>
