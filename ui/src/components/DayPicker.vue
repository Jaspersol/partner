<script setup lang="ts">
import { computed } from 'vue'
import type { SessionDay } from '../api'

/**
 * The day a reader is looking at, chosen from the days that have conversation in them.
 *
 * <para>
 * A native `<select>`, grouped by year and month: it keeps the platform's keyboard behaviour and accessible
 * name, which is the same reason the settings page uses one. The list is not maintained here. It is exactly the
 * days the server reports, so a day with no conversation never appears and one that gains its first message
 * appears by itself.
 * </para>
 *
 * <para>
 * Newest first, because the one most people want is at the top. The newest entry is labelled 今天 / 昨天 only when
 * it really is — after a quiet week it is just a date.
 * </para>
 */
const props = defineProps<{
  days: readonly SessionDay[]
  modelValue: string
}>()

const emit = defineEmits<{ 'update:modelValue': [day: string] }>()

const WEEKDAY = ['日', '一', '二', '三', '四', '五', '六']

function parts(day: string): { year: number; month: number; date: number; weekday: number } {
  const [year = 0, month = 0, date = 0] = day.split('-').map(Number)
  return { year, month, date, weekday: new Date(year, month - 1, date).getDay() }
}

function localKey(offsetDays: number): string {
  const when = new Date()
  when.setDate(when.getDate() + offsetDays)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${when.getFullYear()}-${pad(when.getMonth() + 1)}-${pad(when.getDate())}`
}

function label(day: SessionDay): string {
  const { month, date, weekday } = parts(day.day)
  const near = day.day === localKey(0) ? ' · 今天' : day.day === localKey(-1) ? ' · 昨天' : ''
  return `${month}月${date}日 周${WEEKDAY[weekday]}${near}`
}

const groups = computed(() => {
  const out: { title: string; days: SessionDay[] }[] = []
  for (const day of [...props.days].reverse()) {
    const { year, month } = parts(day.day)
    const title = `${year}年${month}月`
    const last = out[out.length - 1]
    if (last?.title === title) last.days.push(day)
    else out.push({ title, days: [day] })
  }
  return out
})
</script>

<template>
  <label v-if="days.length > 0" class="day-picker">
    <span class="sr-only">选择日期</span>
    <select
      :value="modelValue"
      aria-label="选择日期"
      data-test="session-day"
      @change="emit('update:modelValue', ($event.target as HTMLSelectElement).value)"
    >
      <optgroup v-for="group in groups" :key="group.title" :label="group.title">
        <option v-for="day in group.days" :key="day.day" :value="day.day">{{ label(day) }}</option>
      </optgroup>
    </select>
  </label>
</template>

<style scoped>
.day-picker select {
  min-block-size: 2.25rem;
  max-inline-size: 12rem;
  font-size: var(--fs-meta);
}
</style>
