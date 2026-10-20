<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import Icon from './Icon.vue'

/**
 * A future moment, picked from a month grid and two time columns.
 *
 * <para>
 * Replaces the browser's own `datetime-local` field, which is a row of spin boxes that differ between browsers
 * and webviews, cannot say "the past is not allowed" in a way a reader can see, and gives no way to say "tomorrow
 * morning" without doing the arithmetic. Here the past is simply not offered: earlier days and earlier times are
 * disabled, and picking today clamps the time to the first one that is still ahead — so the server's rule that
 * the time must be after now cannot be broken from this control.
 * </para>
 *
 * <para>
 * The value is the same `yyyy-MM-ddTHH:mm` local wall-clock string a `datetime-local` input holds (empty when
 * nothing is chosen), so the form around it did not have to change. Choosing a day with no time yet starts at
 * 09:00; choosing a time with no day starts today.
 * </para>
 */
defineOptions({ inheritAttrs: false })

const props = defineProps<{ modelValue: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const WEEKDAYS = ['一', '二', '三', '四', '五', '六', '日']
const DEFAULT_HOUR = 9
const MINUTE_STEP = 5

interface Stamp {
  year: number
  /** 0-based, as `Date` has it. */
  month: number
  date: number
  hour: number
  minute: number
}

const pad = (value: number) => String(value).padStart(2, '0')
const dayKey = (year: number, month: number, date: number) => `${year}-${pad(month + 1)}-${pad(date)}`

function parse(value: string): Stamp | null {
  const found = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value)
  if (found === null) return null
  const [, year, month, date, hour, minute] = found.map(Number) as [number, number, number, number, number, number]
  return { year, month: month - 1, date, hour, minute }
}

function format(stamp: Stamp): string {
  return `${dayKey(stamp.year, stamp.month, stamp.date)}T${pad(stamp.hour)}:${pad(stamp.minute)}`
}

const selected = computed(() => parse(props.modelValue))

const open = ref(false)
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const panel = ref<HTMLElement | null>(null)

/** Read when the panel opens, not on every render: a clock that ticks under a picker moves the ground. */
const now = ref(new Date())
const viewYear = ref(now.value.getFullYear())
const viewMonth = ref(now.value.getMonth())

const todayKey = computed(() => dayKey(now.value.getFullYear(), now.value.getMonth(), now.value.getDate()))

/** The first minute that is still ahead of now. */
const earliest = computed(() => {
  const moment = new Date(now.value)
  moment.setSeconds(0, 0)
  moment.setMinutes(moment.getMinutes() + 1)
  return moment
})

function isToday(year: number, month: number, date: number): boolean {
  return dayKey(year, month, date) === todayKey.value
}

function isPastDay(year: number, month: number, date: number): boolean {
  return new Date(year, month, date, 23, 59, 59, 999).getTime() <= now.value.getTime()
}

/** The earliest hour:minute a day can be picked at: midnight, except today. */
function floorOn(year: number, month: number, date: number): { hour: number; minute: number } {
  return isToday(year, month, date)
    ? { hour: earliest.value.getHours(), minute: earliest.value.getMinutes() }
    : { hour: 0, minute: 0 }
}

/** Keeps a choice inside the future: a time that is already behind us becomes the first one that is not. */
function clamp(stamp: Stamp): Stamp {
  const at = new Date(stamp.year, stamp.month, stamp.date, stamp.hour, stamp.minute)
  if (at.getTime() > now.value.getTime()) return stamp

  const floor = floorOn(stamp.year, stamp.month, stamp.date)
  return { ...stamp, hour: floor.hour, minute: floor.minute }
}

function commit(stamp: Stamp) {
  emit('update:modelValue', format(clamp(stamp)))
}

function pickDay(year: number, month: number, date: number) {
  const base = selected.value
  commit({ year, month, date, hour: base?.hour ?? DEFAULT_HOUR, minute: base?.minute ?? 0 })
}

function pickHour(hour: number) {
  const base = selected.value ?? { year: now.value.getFullYear(), month: now.value.getMonth(), date: now.value.getDate(), hour: 0, minute: 0 }
  commit({ ...base, hour })
}

function pickMinute(minute: number) {
  const base = selected.value ?? {
    year: now.value.getFullYear(),
    month: now.value.getMonth(),
    date: now.value.getDate(),
    hour: earliest.value.getHours(),
    minute: 0,
  }
  commit({ ...base, minute })
}

// ---- the month grid ------------------------------------------------------------------------------------

/** Blank cells before the 1st (Monday first), then the days. Blanks are `null`, not disabled days. */
const cells = computed<(number | null)[]>(() => {
  const lead = (new Date(viewYear.value, viewMonth.value, 1).getDay() + 6) % 7
  const count = new Date(viewYear.value, viewMonth.value + 1, 0).getDate()
  return [...Array.from({ length: lead }, () => null), ...Array.from({ length: count }, (_, at) => at + 1)]
})

const monthLabel = computed(() => `${viewYear.value}年${viewMonth.value + 1}月`)

const atCurrentMonth = computed(
  () => viewYear.value === now.value.getFullYear() && viewMonth.value === now.value.getMonth(),
)

function shiftMonth(by: number) {
  const moved = new Date(viewYear.value, viewMonth.value + by, 1)
  viewYear.value = moved.getFullYear()
  viewMonth.value = moved.getMonth()
}

function isSelectedDay(date: number): boolean {
  const stamp = selected.value
  return stamp !== null && stamp.year === viewYear.value && stamp.month === viewMonth.value && stamp.date === date
}

/** One tab stop for the whole grid: the chosen day, else today, else the first day that is not in the past. */
function focusable(date: number): boolean {
  const stamp = selected.value
  if (stamp !== null && stamp.year === viewYear.value && stamp.month === viewMonth.value) return stamp.date === date
  if (atCurrentMonth.value) return date === now.value.getDate()
  return date === 1
}

function onGridKeydown(event: KeyboardEvent, date: number) {
  const step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }[event.key]
  if (step === undefined) return

  event.preventDefault()
  const target = new Date(viewYear.value, viewMonth.value, date + step)
  if (isPastDay(target.getFullYear(), target.getMonth(), target.getDate())) return

  viewYear.value = target.getFullYear()
  viewMonth.value = target.getMonth()
  void nextTick(() => {
    panel.value
      ?.querySelector<HTMLElement>(`[data-day="${dayKey(target.getFullYear(), target.getMonth(), target.getDate())}"]`)
      ?.focus()
  })
}

// ---- the time columns ----------------------------------------------------------------------------------

const hours = Array.from({ length: 24 }, (_, hour) => hour)

/** Every fifth minute, plus the chosen one when it is not on the grid (a preset like "1 小时后" lands anywhere). */
const minutes = computed(() => {
  const grid = Array.from({ length: 60 / MINUTE_STEP }, (_, at) => at * MINUTE_STEP)
  const chosen = selected.value?.minute
  return chosen !== undefined && !grid.includes(chosen) ? [...grid, chosen].sort((a, b) => a - b) : grid
})

/** The day the time columns apply to: the chosen one, else today. */
const timeDay = computed(() => selected.value ?? { year: now.value.getFullYear(), month: now.value.getMonth(), date: now.value.getDate() })

function hourDisabled(hour: number): boolean {
  return hour < floorOn(timeDay.value.year, timeDay.value.month, timeDay.value.date).hour
}

function minuteDisabled(minute: number): boolean {
  const floor = floorOn(timeDay.value.year, timeDay.value.month, timeDay.value.date)
  const hour = selected.value?.hour ?? floor.hour
  return hour < floor.hour || (hour === floor.hour && minute < floor.minute)
}

// ---- shortcuts -----------------------------------------------------------------------------------------

interface Preset {
  key: string
  label: string
  at: Date
}

const presets = computed<Preset[]>(() => {
  const base = now.value
  const inAnHour = new Date(base.getTime() + 60 * 60 * 1000)
  const tomorrow = new Date(base.getFullYear(), base.getMonth(), base.getDate() + 1, DEFAULT_HOUR, 0)
  const toMonday = ((8 - base.getDay()) % 7) || 7
  const monday = new Date(base.getFullYear(), base.getMonth(), base.getDate() + toMonday, DEFAULT_HOUR, 0)

  return [
    { key: 'hour', label: '1 小时后', at: inAnHour },
    { key: 'tomorrow', label: `明天 ${DEFAULT_HOUR}:00`, at: tomorrow },
    { key: 'monday', label: `下周一 ${DEFAULT_HOUR}:00`, at: monday },
  ]
})

function usePreset(preset: Preset) {
  commit({
    year: preset.at.getFullYear(),
    month: preset.at.getMonth(),
    date: preset.at.getDate(),
    hour: preset.at.getHours(),
    minute: preset.at.getMinutes(),
  })
  viewYear.value = preset.at.getFullYear()
  viewMonth.value = preset.at.getMonth()
}

// ---- trigger text and open/close -----------------------------------------------------------------------

const label = computed(() => {
  const stamp = selected.value
  if (stamp === null) return '选择日期和时间'
  const weekday = WEEKDAYS[(new Date(stamp.year, stamp.month, stamp.date).getDay() + 6) % 7]
  return `${stamp.year}年${stamp.month + 1}月${stamp.date}日 周${weekday} ${pad(stamp.hour)}:${pad(stamp.minute)}`
})

function toggle() {
  if (open.value) {
    close()
    return
  }

  now.value = new Date()
  const stamp = selected.value
  viewYear.value = stamp?.year ?? now.value.getFullYear()
  viewMonth.value = stamp?.month ?? now.value.getMonth()
  open.value = true

  revealChosenTime()
}

/**
 * Brings the chosen hour and minute into view. A column of 24 would otherwise stay scrolled to midnight, both when
 * the panel opens on an existing choice and when a shortcut sets a time the reader cannot see from where the
 * column is.
 */
function revealChosenTime() {
  void nextTick(() => {
    for (const chosen of panel.value?.querySelectorAll<HTMLElement>('.dtp-time [aria-pressed="true"]') ?? []) {
      chosen.scrollIntoView?.({ block: 'nearest' })
    }
  })
}

watch(() => props.modelValue, () => {
  if (open.value) revealChosenTime()
})

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
  <div ref="root" class="dtp" @keydown.escape.stop="close()">
    <button
      ref="trigger"
      v-bind="$attrs"
      type="button"
      class="dtp-trigger"
      :class="{ 'is-empty': selected === null }"
      aria-haspopup="dialog"
      :aria-expanded="open ? 'true' : 'false'"
      @click="toggle"
    >
      <Icon name="clock" :size="14" />
      <span class="dtp-label">{{ label }}</span>
      <Icon name="chevron-down" :size="14" />
    </button>

    <div v-if="open" ref="panel" class="dtp-panel g-float" role="dialog" aria-label="选择日期和时间" data-test="datetime-panel">
      <div class="dtp-presets" role="group" aria-label="快捷选择">
        <button
          v-for="preset in presets"
          :key="preset.key"
          type="button"
          class="ghost dtp-preset"
          :data-test="`datetime-preset-${preset.key}`"
          @click="usePreset(preset)"
        >
          {{ preset.label }}
        </button>
      </div>

      <div class="dtp-body">
        <div class="dtp-month" role="group" aria-label="日期">
          <div class="dtp-month-head">
            <button
              type="button"
              class="ghost dtp-step"
              aria-label="上个月"
              :disabled="atCurrentMonth"
              data-test="datetime-prev"
              @click="shiftMonth(-1)"
            >
              <Icon name="chevron-down" :size="14" class="turn-left" />
            </button>
            <span class="dtp-month-name" data-test="datetime-month">{{ monthLabel }}</span>
            <button type="button" class="ghost dtp-step" aria-label="下个月" data-test="datetime-next" @click="shiftMonth(1)">
              <Icon name="chevron-down" :size="14" class="turn-right" />
            </button>
          </div>

          <div class="dtp-grid">
            <span v-for="name in WEEKDAYS" :key="name" class="dtp-weekday" aria-hidden="true">{{ name }}</span>
            <template v-for="(cell, at) in cells" :key="at">
              <span v-if="cell === null" class="dtp-blank" aria-hidden="true" />
              <button
                v-else
                type="button"
                class="dtp-day"
                :class="{ 'is-today': isToday(viewYear, viewMonth, cell) }"
                :disabled="isPastDay(viewYear, viewMonth, cell)"
                :tabindex="focusable(cell) ? 0 : -1"
                :aria-pressed="isSelectedDay(cell) ? 'true' : 'false'"
                :aria-current="isToday(viewYear, viewMonth, cell) ? 'date' : undefined"
                :aria-label="`${viewMonth + 1}月${cell}日`"
                :data-day="dayKey(viewYear, viewMonth, cell)"
                :data-test="`datetime-day-${dayKey(viewYear, viewMonth, cell)}`"
                @click="pickDay(viewYear, viewMonth, cell)"
                @keydown="onGridKeydown($event, cell)"
              >
                {{ cell }}
              </button>
            </template>
          </div>
        </div>

        <div class="dtp-time">
          <ul class="dtp-col" aria-label="时" data-test="datetime-hours">
            <li v-for="hour in hours" :key="hour">
              <button
                type="button"
                class="dtp-option"
                :disabled="hourDisabled(hour)"
                :aria-pressed="selected?.hour === hour ? 'true' : 'false'"
                :aria-label="`${hour} 时`"
                :data-test="`datetime-hour-${hour}`"
                @click="pickHour(hour)"
              >
                {{ pad(hour) }}
              </button>
            </li>
          </ul>
          <ul class="dtp-col" aria-label="分" data-test="datetime-minutes">
            <li v-for="minute in minutes" :key="minute">
              <button
                type="button"
                class="dtp-option"
                :disabled="minuteDisabled(minute)"
                :aria-pressed="selected?.minute === minute ? 'true' : 'false'"
                :aria-label="`${minute} 分`"
                :data-test="`datetime-minute-${minute}`"
                @click="pickMinute(minute)"
              >
                {{ pad(minute) }}
              </button>
            </li>
          </ul>
        </div>
      </div>

      <div class="dtp-foot">
        <span class="dtp-picked" data-test="datetime-picked">{{ selected === null ? '还没选' : label }}</span>
        <button type="button" class="primary dtp-done" :disabled="selected === null" data-test="datetime-done" @click="close()">
          确定
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.dtp {
  position: relative;
  display: inline-flex;
}

.dtp-trigger {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  min-block-size: 2.25rem;
  padding: 0 var(--space-3);
  white-space: nowrap;
}

.dtp-trigger.is-empty .dtp-label {
  color: var(--text-3);
}

.dtp-panel {
  position: absolute;
  inset-block-start: calc(100% + var(--space-2));
  inset-inline-end: 0;
  z-index: 30;
  display: grid;
  gap: var(--space-3);
  padding: var(--space-3);
  border-radius: var(--radius-md);
}

.dtp-presets {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
}

.dtp-preset {
  min-block-size: 1.75rem;
  padding-inline: var(--space-3);
  font-size: var(--fs-meta);
}

.dtp-body {
  display: flex;
  gap: var(--space-3);
}

.dtp-month {
  display: grid;
  gap: var(--space-2);
  align-content: start;
}

.dtp-month-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.dtp-month-name {
  font-weight: 600;
  font-size: var(--fs-ui);
}

.dtp-step {
  inline-size: 1.75rem;
  min-block-size: 1.75rem;
  padding: 0;
}

.turn-left {
  transform: rotate(90deg);
}

.turn-right {
  transform: rotate(-90deg);
}

.dtp-grid {
  display: grid;
  grid-template-columns: repeat(7, 2.25rem);
  gap: 2px;
  justify-items: center;
}

.dtp-weekday {
  color: var(--text-3);
  font-size: var(--fs-meta);
  line-height: 1.75rem;
}

.dtp-day {
  inline-size: 2.25rem;
  block-size: 2.25rem;
  padding: 0;
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  box-shadow: none;
  color: var(--text);
  font-size: var(--fs-ui);
}

.dtp-day:hover:not(:disabled) {
  background: var(--surface-2);
}

.dtp-day:disabled {
  color: var(--text-3);
  opacity: 0.4;
}

/* Today is ringed, not filled: filled means chosen, and the two can be the same day. */
.dtp-day.is-today {
  box-shadow: inset 0 0 0 1px var(--accent);
}

.dtp-day[aria-pressed='true'] {
  background: var(--accent-soft);
  color: var(--accent-pressed);
  font-weight: 600;
}

.dtp-time {
  display: flex;
  gap: var(--space-1);
  border-inline-start: 1px solid var(--glass-edge);
  padding-inline-start: var(--space-3);
}

.dtp-col {
  list-style: none;
  margin: 0;
  padding: 0 var(--space-1);
  display: grid;
  align-content: start;
  gap: 2px;
  max-block-size: 17.5rem;
  overflow-x: hidden;
  overflow-y: auto;
}

.dtp-col + .dtp-col {
  border-inline-start: 1px solid var(--glass-edge);
}

.dtp-option {
  /* Fills the column instead of being a fixed width inside it: a fixed width plus the column's padding is what
     made the column a few pixels too wide for its own content and grew a horizontal scrollbar. */
  inline-size: 100%;
  min-inline-size: 2.75rem;
  min-block-size: 2rem;
  padding: 0;
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  box-shadow: none;
  color: var(--text-2);
  font-size: var(--fs-ui);
  font-variant-numeric: tabular-nums;
}

.dtp-option:hover:not(:disabled) {
  background: var(--surface-2);
}

.dtp-option:disabled {
  opacity: 0.35;
}

.dtp-option[aria-pressed='true'] {
  background: var(--accent-soft);
  color: var(--accent-pressed);
  font-weight: 600;
}

.dtp-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  padding-block-start: var(--space-2);
  border-block-start: 1px solid var(--glass-edge);
}

.dtp-picked {
  color: var(--text-2);
  font-size: var(--fs-meta);
}

.dtp-done {
  min-block-size: 2rem;
}
</style>
