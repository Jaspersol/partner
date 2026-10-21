<script setup lang="ts">
import { t } from '../i18n'
import { computed, ref } from 'vue'
import { gearName, normalizeGear as normalize } from '../gears'
import Icon from './Icon.vue'

/**
 * Thinking strength as a table: what the reader sees, what is actually sent, and which row is the default.
 *
 * <para>
 * Nothing here is a slider. The list is a mapping — the text on the left is only a name for the value on the right,
 * and the value is what goes on the wire as `reasoning_effort` — so it is shown as one, and a row is added or
 * removed like any list. The radio picks the default. `off` is a row like the others; it means the field is not
 * sent at all, and the table says so.
 * </para>
 *
 * <para>
 * The text is the reader's own: typed when a row is added, editable in place. Left empty it falls back to the
 * built-in name for the six common words and to the word itself for any other (see `gears.ts`). Rules for the
 * values are the old ones: trimmed, lower-case, no blanks, no repeats.
 * </para>
 */
const props = defineProps<{
  modelValue: string
  gears: readonly string[]
  /** Names typed by the reader (word → text). A word with no entry shows its built-in name, or itself. */
  labels?: Record<string, string> | undefined
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  'update:gears': [value: string[]]
  'update:labels': [value: Record<string, string>]
}>()

const uid = Math.random().toString(36).slice(2, 8)

/** A default the list does not carry is still a row: dropping it would silently change what is sent. */
const rows = computed(() => {
  const current = normalize(props.modelValue)
  return current !== '' && !props.gears.includes(current) ? [...props.gears, current] : [...props.gears]
})

const current = computed(() => normalize(props.modelValue))

function commit(next: string[]): void {
  const clean = next.map(normalize).filter((gear, at, all) => gear !== '' && all.indexOf(gear) === at)
  emit('update:gears', clean)
}

/** What a row shows: the typed text, else the built-in name. Not `gearName`: that reads the saved labels, not these. */
function shown(gear: string): string {
  return props.labels?.[gear] ?? gearName(gear)
}

function setLabel(gear: string, text: string): void {
  const next = { ...(props.labels ?? {}) }
  const clean = text.trim()
  if (clean === '') delete next[gear]
  else next[gear] = clean
  emit('update:labels', next)
}

function remove(value: string): void {
  const rest = rows.value.filter((gear) => gear !== value)
  commit(rest)
  if (props.labels?.[value] !== undefined) setLabel(value, '')
  // Removing the default would leave it pointing at a row that is gone.
  if (value === current.value) emit('update:modelValue', rest[0] ?? '')
}

function move(at: number, delta: number): void {
  const to = at + delta
  const list = [...rows.value]
  if (to < 0 || to >= list.length) return
  const [gear] = list.splice(at, 1)
  list.splice(to, 0, gear as string)
  commit(list)
}

const draftLabel = ref('')
const draftValue = ref('')

function add(): void {
  const gear = normalize(draftValue.value)
  if (gear === '') return
  const text = draftLabel.value
  draftLabel.value = ''
  draftValue.value = ''
  // A repeat of an existing word is ignored whole, including its text: the first row keeps its name.
  if (rows.value.includes(gear)) return
  commit([...rows.value, gear])
  if (text.trim() !== '') setLabel(gear, text)
}
</script>

<template>
  <div class="gear-table" data-test="reasoning-table">
    <div class="gear-head" aria-hidden="true">
      <span>{{ t('默认') }}</span>
      <span>{{ t('显示文字') }}</span>
      <span>{{ t('实际传值') }}</span>
      <span />
    </div>

    <ul class="gear-rows" role="list">
      <li
        v-for="(gear, at) in rows"
        :key="gear"
        class="gear-row"
        :class="{ 'is-default': gear === current }"
        :data-value="gear"
        data-test="reasoning-row"
      >
        <input
          type="radio"
          class="gear-radio"
          :name="`gear-default-${uid}`"
          :checked="gear === current"
          :aria-label="t('设为默认：{shown}', { shown: shown(gear) })"
          data-test="reasoning-default"
          @change="emit('update:modelValue', gear)"
        />
        <input
          class="gear-name"
          type="text"
          :value="shown(gear)"
          :aria-label="t('{gear} 的显示文字', { gear: gear })"
          data-test="reasoning-label"
          @change="setLabel(gear, ($event.target as HTMLInputElement).value)"
        />
        <code class="gear-value">{{ gear }}</code>
        <span class="gear-actions">
          <button class="ghost" type="button" :disabled="at === 0" :aria-label="t('{shown} 上移', { shown: shown(gear) })" data-test="reasoning-up" @click="move(at, -1)">
            <Icon name="chevron-up" :size="14" />
          </button>
          <button class="ghost" type="button" :disabled="at === rows.length - 1" :aria-label="t('{shown} 下移', { shown: shown(gear) })" data-test="reasoning-down" @click="move(at, 1)">
            <Icon name="chevron-down" :size="14" />
          </button>
          <button class="ghost" type="button" :aria-label="t('删除 {gear}', { gear: gear })" data-test="reasoning-remove" @click="remove(gear)">
            <Icon name="x" :size="14" />
          </button>
        </span>
      </li>
    </ul>

    <div class="gear-add">
      <input
        v-model="draftLabel"
        type="text"
        :placeholder="t('显示文字，如 极速')"
        :aria-label="t('新增档位的显示文字')"
        data-test="reasoning-add-label"
        @keydown.enter.prevent="add"
      />
      <input
        v-model="draftValue"
        type="text"
        :placeholder="t('实际传值，如 turbo')"
        :aria-label="t('新增档位的传值')"
        data-test="reasoning-add-input"
        @keydown.enter.prevent="add"
      />
      <button class="ghost" type="button" data-test="reasoning-add" @click="add">{{ t('添加') }}</button>
    </div>
  </div>
</template>

<style scoped>
.gear-table {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-2);
  inline-size: 100%;
  min-inline-size: 0;
}

.gear-head,
.gear-row {
  display: grid;
  grid-template-columns: 3rem minmax(0, 1fr) minmax(0, 1fr) auto;
  align-items: center;
  column-gap: var(--space-3);
  padding-inline: var(--space-3);
}

.gear-head {
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.gear-rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--space-1);
}

.gear-row {
  min-block-size: 2.5rem;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.gear-row.is-default {
  background: var(--accent-soft);
}

.gear-radio {
  justify-self: start;
  margin: 0;
}

/* A field that reads as text until it is touched. */
.gear-name {
  min-inline-size: 0;
  min-block-size: 1.75rem;
  padding-inline: var(--space-2);
  border-color: transparent;
  background: transparent;
  box-shadow: none;
}

.gear-name:hover,
.gear-name:focus {
  background: var(--field-bg);
}

.gear-row.is-default .gear-name {
  color: var(--accent-pressed);
  font-weight: 500;
}

.gear-value {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-2);
}

.gear-actions {
  display: inline-flex;
  gap: 2px;
}

.gear-actions button {
  min-block-size: 1.75rem;
  inline-size: 1.75rem;
  padding: 0;
}

.gear-add {
  display: flex;
  gap: var(--space-2);
}

.gear-add input {
  flex: 1 1 0;
  min-inline-size: 0;
}
</style>
