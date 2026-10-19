<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { SessionDay } from '../api'
import Icon from './Icon.vue'

/**
 * The day a reader is looking at, chosen year → month → day from the days that have conversation in them.
 *
 * <para>
 * A cascader: one button that names the day on screen, and a panel with three columns that narrow as the reader
 * goes. A column lists only what exists under the choice to its left, so a day with no conversation can never be
 * picked — and there is nothing to grey out, because it is simply not there. The list is not maintained here: it
 * is exactly the days the server reports, so a day that gains its first message appears by itself.
 * </para>
 *
 * <para>
 * Changing year or month only changes what the columns show. The view moves when a day is clicked, because a
 * month is not something the session can be looking at. Newest first in every column, since the one most people
 * want is at the top.
 * </para>
 */
const props = defineProps<{
  days: readonly SessionDay[]
  modelValue: string
}>()

const emit = defineEmits<{ 'update:modelValue': [day: string] }>()

const WEEKDAY = ['日', '一', '二', '三', '四', '五', '六']

interface Parts {
  year: number
  month: number
  date: number
}

function parts(day: string): Parts {
  const [year = 0, month = 0, date = 0] = day.split('-').map(Number)
  return { year, month, date }
}

function weekday(day: string): string {
  const { year, month, date } = parts(day)
  return WEEKDAY[new Date(year, month - 1, date).getDay()] ?? ''
}

function localKey(offsetDays: number): string {
  const when = new Date()
  when.setDate(when.getDate() + offsetDays)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${when.getFullYear()}-${pad(when.getMonth() + 1)}-${pad(when.getDate())}`
}

/** 今天 / 昨天 only when it really is — after a quiet week the newest day is just a date. */
function near(day: string): string {
  return day === localKey(0) ? '今天' : day === localKey(-1) ? '昨天' : ''
}

/** The trigger's text: the whole date, so the button alone says which day is on screen. */
const current = computed(() => {
  if (props.modelValue === '') return '选择日期'
  const { year, month, date } = parts(props.modelValue)
  const tail = near(props.modelValue)
  return `${year}年${month}月${date}日 周${weekday(props.modelValue)}${tail === '' ? '' : ` · ${tail}`}`
})

/** Every day on offer, newest first. */
const newestFirst = computed(() => [...props.days].map((entry) => entry.day).sort().reverse())

const years = computed(() => [...new Set(newestFirst.value.map((day) => parts(day).year))])

function monthsOf(year: number): number[] {
  return [...new Set(newestFirst.value.map(parts).filter((p) => p.year === year).map((p) => p.month))]
}

const open = ref(false)
const browseYear = ref(0)
const browseMonth = ref(0)

const months = computed(() => monthsOf(browseYear.value))
const dates = computed(() =>
  newestFirst.value.filter((day) => {
    const p = parts(day)
    return p.year === browseYear.value && p.month === browseMonth.value
  }),
)

/** Starts the columns on the day on screen, or on the newest day when that one is not on offer any more. */
function browseTo(day: string) {
  const known = newestFirst.value.includes(day) ? day : (newestFirst.value[0] ?? '')
  const { year, month } = parts(known)
  browseYear.value = year
  browseMonth.value = month
}

function pickYear(year: number) {
  browseYear.value = year
  // Stay on the same month of the new year when it has one; otherwise its newest.
  const selected = parts(props.modelValue)
  const available = monthsOf(year)
  browseMonth.value = available.includes(selected.month) ? selected.month : (available[0] ?? 0)
}

function pickMonth(month: number) {
  browseMonth.value = month
}

const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)

function pickDay(day: string) {
  close()
  if (day !== props.modelValue) emit('update:modelValue', day)
}

function toggle() {
  if (open.value) {
    close()
    return
  }

  browseTo(props.modelValue)
  open.value = true
}

function close(returnFocus = true) {
  if (!open.value) return
  open.value = false
  if (returnFocus) void nextTick(() => trigger.value?.focus())
}

function onOutside(event: Event) {
  if (root.value !== null && !root.value.contains(event.target as Node)) close(false)
}

// The listener lives only while the panel is open: a picker that is never opened should cost nothing.
watch(open, (isOpen) => {
  if (isOpen) document.addEventListener('pointerdown', onOutside)
  else document.removeEventListener('pointerdown', onOutside)
})

onBeforeUnmount(() => document.removeEventListener('pointerdown', onOutside))
</script>

<template>
  <div v-if="days.length > 0" ref="root" class="day-picker" @keydown.escape.stop="close()">
    <button
      ref="trigger"
      type="button"
      class="day-trigger"
      aria-haspopup="dialog"
      :aria-expanded="open ? 'true' : 'false'"
      aria-label="选择日期"
      data-test="session-day"
      @click="toggle"
    >
      <span class="day-current">{{ current }}</span>
      <Icon name="chevron-down" :size="14" />
    </button>

    <div v-if="open" class="day-panel g-float" role="dialog" aria-label="选择日期" data-test="session-day-panel">
      <ul class="day-col" role="listbox" aria-label="年" data-test="session-day-years">
        <li v-for="year in years" :key="year" role="presentation">
          <button
            type="button"
            role="option"
            class="day-option"
            :aria-selected="year === browseYear ? 'true' : 'false'"
            :data-test="`session-day-year-${year}`"
            @click="pickYear(year)"
          >
            {{ year }}年
          </button>
        </li>
      </ul>

      <ul class="day-col" role="listbox" aria-label="月" data-test="session-day-months">
        <li v-for="month in months" :key="month" role="presentation">
          <button
            type="button"
            role="option"
            class="day-option"
            :aria-selected="month === browseMonth ? 'true' : 'false'"
            :data-test="`session-day-month-${month}`"
            @click="pickMonth(month)"
          >
            {{ month }}月
          </button>
        </li>
      </ul>

      <ul class="day-col" role="listbox" aria-label="日" data-test="session-day-dates">
        <li v-for="day in dates" :key="day" role="presentation">
          <button
            type="button"
            role="option"
            class="day-option"
            :aria-selected="day === modelValue ? 'true' : 'false'"
            :data-day="day"
            :data-test="`session-day-date-${day}`"
            @click="pickDay(day)"
          >
            <span>{{ parts(day).date }}日 周{{ weekday(day) }}</span>
            <span v-if="near(day) !== ''" class="day-near">{{ near(day) }}</span>
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.day-picker {
  position: relative;
  display: inline-flex;
}

.day-trigger {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  min-block-size: 2.25rem;
  padding: 0 var(--space-3);
  border-radius: var(--radius-sm);
  background: var(--glass-2);
  border: 1px solid var(--glass-edge);
  box-shadow: var(--glass-hi), var(--shadow-1);
  font-size: var(--fs-meta);
  white-space: nowrap;
}

.day-panel {
  position: absolute;
  inset-block-start: calc(100% + var(--space-2));
  inset-inline-end: 0;
  z-index: 30;
  display: flex;
  gap: var(--space-1);
  padding: var(--space-2);
  border-radius: var(--radius-md);
}

.day-col {
  list-style: none;
  margin: 0;
  padding: 0 var(--space-1);
  display: grid;
  align-content: start;
  gap: 2px;
  min-inline-size: 5.5rem;
  max-block-size: 16rem;
  overflow-y: auto;
}

.day-col + .day-col {
  border-inline-start: 1px solid var(--glass-edge);
}

.day-option {
  inline-size: 100%;
  min-block-size: 2rem;
  padding: 0 var(--space-3);
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--text-2);
  font-size: var(--fs-meta);
  text-align: start;
  white-space: nowrap;
}

.day-option:hover {
  background: var(--surface-2);
}

.day-option[aria-selected='true'] {
  background: var(--accent-soft);
  color: var(--accent-pressed);
  font-weight: 600;
}

.day-near {
  color: var(--text-3);
  font-weight: 400;
}
</style>
