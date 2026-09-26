<script setup lang="ts">
import { ref, watch } from 'vue'

/**
 * The shared right-hand panel for session events and memory records (ui.md §5.3). The two differ only in
 * wording, the revision rows and the optional destructive action, so the mechanism lives here once.
 *
 * `testPrefix` keeps the existing `session-*` / `memory-*` hooks that the frontend tests and the smoke rely
 * on, without deriving them from which buttons happen to be present.
 */
const props = defineProps<{
  testPrefix: 'session' | 'memory'
  title: string
  target: string
  draft: string
  revisions: { at: string; actor?: string; body: string }[]
  saveLabel?: string
  /** Present only where deleting is meaningful (memory). */
  deleteLabel?: string
  /** Shown above the editor when the selection itself carries a note (an edited event, a superseded record). */
  note?: string
  noteTest?: string
}>()

const emit = defineEmits<{
  save: [body: string]
  remove: []
  restore: [body: string]
}>()

const draft = ref(props.draft)

// The panel is a projection of the selection, so the editor follows it rather than owning a copy.
watch(
  () => props.draft,
  (value) => {
    draft.value = value
  },
)
</script>

<template>
  <aside class="inspector" data-test="inspector">
    <div class="inspector-head">
      <div>
        <h2>{{ title }}</h2>
        <div class="meta break" :data-test="`${testPrefix}-target`">{{ target }}</div>
        <div v-if="note" class="edited" :data-test="noteTest">{{ note }}</div>
      </div>
    </div>

    <div class="inspector-body">
      <textarea v-model="draft" rows="6" :data-test="`${testPrefix}-draft`" />

      <div class="inspector-actions">
        <button class="primary" :data-test="`${testPrefix}-save`" @click="emit('save', draft.trim())">
          {{ saveLabel ?? '保存' }}
        </button>
        <button v-if="deleteLabel" class="danger" :data-test="`${testPrefix}-delete`" @click="emit('remove')">
          {{ deleteLabel }}
        </button>
      </div>

      <h3>修订历史</h3>
      <ul class="list" :data-test="`${testPrefix}-revisions`">
        <li v-for="(revision, index) in revisions" :key="index">
          <div class="meta">
            {{ revision.at.slice(11, 19) }}
            <span v-if="revision.actor"> · {{ revision.actor }}</span>
          </div>
          <div class="break">{{ revision.body }}</div>
          <!-- Undo needs no special mechanism: restoring appends another edit pointing at the old text. -->
          <button class="ghost" data-test="inspector-restore" @click="draft = revision.body">
            恢复此版本
          </button>
        </li>
      </ul>
      <p v-if="revisions.length === 0" class="muted">（无修订）</p>
    </div>
  </aside>
</template>
