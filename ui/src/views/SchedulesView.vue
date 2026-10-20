<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ApiError, type PartnerApiSurface, type ScheduleInput, type ScheduleKind, type ScheduleTaskView } from '../api'
import DateTimePicker from '../components/DateTimePicker.vue'
import HintTip from '../components/HintTip.vue'
import Icon from '../components/Icon.vue'
import Segmented from '../components/Segmented.vue'
import SettingRow from '../components/SettingRow.vue'

/**
 * Timed tasks: a message, and a rule for when it is sent to the assistant.
 *
 * <para>
 * Unlike the other pages this one writes straight to its own endpoint instead of going through the settings save
 * bar. A task is not configuration of this installation — it is a thing that will happen — so there is no draft
 * to discard, and what is on screen is what the server holds. Nothing is optimistic: the list is re-read after
 * every change.
 * </para>
 *
 * <para>
 * There is no edit. A task's identity is its rule plus its message, so changing either is a new task (the server
 * says why); the row offers pause and delete, and the form above it is how you make the replacement.
 * </para>
 */
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ changed: [] }>()

const tasks = ref<ScheduleTaskView[]>([])
/** False until the first read answers, so "nothing here" is never shown for "not known yet". */
const loaded = ref(false)
const error = ref('')

const kind = ref<ScheduleKind>('cron')
const cron = ref('')
const intervalValue = ref(30)
const intervalUnit = ref<'minutes' | 'hours' | 'days'>('minutes')
const at = ref('')
const payload = ref('')
const adding = ref(false)
const formError = ref('')

/** The task whose delete is armed: the first click arms, the second deletes. */
const armed = ref<string | null>(null)

const KINDS = [
  { value: 'cron', label: '按时刻', title: '例如每天 9 点、每周一 8 点' },
  { value: 'interval', label: '每隔一段时间', title: '例如每 30 分钟' },
  { value: 'once', label: '只一次', title: '到点执行一次就结束' },
] as const

const UNIT_MINUTES = { minutes: 1, hours: 60, days: 1440 } as const

const CRON_EXAMPLES = [
  { text: '每天 9:00', value: '0 9 * * *' },
  { text: '工作日 18:30', value: '30 18 * * 1-5' },
  { text: '每小时整点', value: '0 * * * *' },
  { text: '每周一 8:00', value: '0 8 * * 1' },
]

const activeCount = computed(() => tasks.value.filter((task) => task.status !== 'inactive').length)

/** Reads the API's own reason without the status prefix the error class carries for logs. */
function reasonOf(failure: unknown): string {
  return failure instanceof ApiError ? failure.message.replace(/^\d+:\s*/, '') : String(failure)
}

function format(iso: string | null): string {
  if (iso === null) return '—'
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleString('zh-CN', { hour12: false, month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

/** The rule in words. Cron stays as the expression: translating it would be a second source of truth. */
function rule(task: ScheduleTaskView): string {
  if (task.kind === 'interval' && task.intervalMinutes !== null) {
    const minutes = task.intervalMinutes
    if (minutes % 1440 === 0) return `每 ${minutes / 1440} 天`
    if (minutes % 60 === 0) return `每 ${minutes / 60} 小时`
    return `每 ${minutes} 分钟`
  }
  if (task.kind === 'once') return `只一次 · ${format(task.at)}`
  if (task.kind === 'cron' && task.cron !== null) return `cron ${task.cron}`
  return task.describe
}

function stateOf(task: ScheduleTaskView): { text: string; tone: 'on' | 'off' | 'bad' } {
  if (task.status === 'failed') return { text: '投递失败，会重试', tone: 'bad' }
  if (task.status === 'inactive') {
    return { text: task.kind === 'once' && task.lastAt !== null ? '已执行' : '已暂停', tone: 'off' }
  }
  return { text: '运行中', tone: 'on' }
}

function canToggle(task: ScheduleTaskView): boolean {
  // A one-off that already ran has nothing left to pause or resume.
  return !(task.kind === 'once' && task.status === 'inactive' && task.lastAt !== null)
}

async function refresh() {
  try {
    tasks.value = await props.api.schedules()
    error.value = ''
  } catch (failure) {
    error.value = reasonOf(failure)
  } finally {
    loaded.value = true
  }
}

function buildInput(): ScheduleInput | string {
  const text = payload.value.trim()
  if (text === '') return '请填写到点要发给助手的内容'

  if (kind.value === 'cron') {
    return cron.value.trim() === '' ? '请填写时刻规则，或点一个常用的' : { kind: 'cron', cron: cron.value.trim(), payload: text }
  }

  if (kind.value === 'interval') {
    const minutes = Math.round(Number(intervalValue.value) * UNIT_MINUTES[intervalUnit.value])
    return Number.isFinite(minutes) && minutes >= 1
      ? { kind: 'interval', intervalMinutes: minutes, payload: text }
      : '间隔至少 1 分钟'
  }

  const when = new Date(at.value)
  return at.value === '' || Number.isNaN(when.getTime())
    ? '请选择执行的日期和时间'
    : { kind: 'once', at: when.toISOString(), payload: text }
}

async function add() {
  const input = buildInput()
  if (typeof input === 'string') {
    formError.value = input
    return
  }

  formError.value = ''
  adding.value = true
  try {
    await props.api.createSchedule(input)
    payload.value = ''
    cron.value = ''
    at.value = ''
    await refresh()
    emit('changed')
  } catch (failure) {
    // The server's reason, verbatim: it knows why a cron expression does not parse and the form does not.
    formError.value = reasonOf(failure)
  } finally {
    adding.value = false
  }
}

async function toggle(task: ScheduleTaskView) {
  error.value = ''
  try {
    if (task.status === 'inactive') await props.api.resumeSchedule(task.id)
    else await props.api.pauseSchedule(task.id)
    await refresh()
  } catch (failure) {
    error.value = reasonOf(failure)
  }
}

async function remove(task: ScheduleTaskView) {
  if (armed.value !== task.id) {
    armed.value = task.id
    return
  }

  armed.value = null
  error.value = ''
  try {
    await props.api.deleteSchedule(task.id)
    await refresh()
    emit('changed')
  } catch (failure) {
    error.value = reasonOf(failure)
  }
}

// Tasks fire in the background, so "next run" moves on its own; a page that only read once would show a time
// that has already passed.
const REFRESH_MS = 15000
let timer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  void refresh()
  timer = setInterval(() => void refresh(), REFRESH_MS)
})

onBeforeUnmount(() => {
  if (timer !== null) clearInterval(timer)
})

defineExpose({ refresh })
</script>

<template>
  <div class="view">
    <div class="scroll schedules-scroll">
      <div class="g-page">
        <section class="compose g-glass" data-test="schedule-form">
          <header class="compose-head">
            <span class="compose-title">
              新建定时任务
              <HintTip>到点后，下面这段话会作为一条消息发给助手，和你自己打字发送一样，排在已有的消息后面。</HintTip>
            </span>
          </header>

          <SettingRow label="什么时候">
            <Segmented v-model="kind" :options="KINDS" label="触发方式" />
          </SettingRow>

          <template v-if="kind === 'cron'">
            <SettingRow label="时刻规则" stack>
              <template #hint>
                五段：分 时 日 月 星期，例如 <code>0 9 * * *</code> 是每天 9 点。按这台电脑的本地时间计算。
              </template>
              <input v-model="cron" placeholder="0 9 * * *" data-test="schedule-cron" spellcheck="false" />
            </SettingRow>
            <div class="examples" role="group" aria-label="常用时刻">
              <button
                v-for="example in CRON_EXAMPLES"
                :key="example.value"
                type="button"
                class="ghost example"
                data-test="schedule-example"
                @click="cron = example.value"
              >
                {{ example.text }}
              </button>
            </div>
          </template>

          <SettingRow v-else-if="kind === 'interval'" label="每隔" hint="从创建的那一刻算起，按这个间隔一直重复。">
            <span class="interval">
              <input
                v-model.number="intervalValue"
                type="number"
                min="1"
                data-test="schedule-interval"
                aria-label="间隔数值"
              />
              <select v-model="intervalUnit" data-test="schedule-unit" aria-label="间隔单位">
                <option value="minutes">分钟</option>
                <option value="hours">小时</option>
                <option value="days">天</option>
              </select>
            </span>
          </SettingRow>

          <SettingRow v-else label="执行时间" hint="按这台电脑的本地时间。过去的日期和时间不能选。">
            <DateTimePicker v-model="at" data-test="schedule-at" />
          </SettingRow>

          <SettingRow label="发给助手的内容" stack>
            <textarea
              v-model="payload"
              rows="3"
              maxlength="2000"
              placeholder="例如：总结一下昨天的待办，告诉我今天先做什么"
              data-test="schedule-payload"
            />
          </SettingRow>

          <footer class="compose-foot">
            <p v-if="formError" class="field-error" role="alert" data-test="schedule-form-error">{{ formError }}</p>
            <button class="primary" :disabled="adding" data-test="schedule-add" @click="add">
              <Icon name="plus" :size="14" /> 添加
            </button>
          </footer>
        </section>

        <p class="summary-line">
          <span data-test="schedule-summary">{{ tasks.length }} 个任务 · {{ activeCount }} 个在运行</span>
          <button class="ghost refresh" aria-label="刷新" title="刷新" data-test="schedule-refresh" @click="refresh">
            <Icon name="refresh" :size="14" />
          </button>
        </p>

        <p v-if="error" class="field-error list-error" role="alert" data-test="schedule-error">{{ error }}</p>

        <div class="tasks">
          <p v-if="!loaded" class="pending" data-test="schedule-pending">读取中…</p>
          <p v-else-if="tasks.length === 0" class="empty" data-test="schedule-empty">
            还没有定时任务。在上面添加一个，到点助手会自己收到这条消息。
          </p>

          <section
            v-for="task in tasks"
            :key="task.id"
            class="task g-glass"
            :class="{ 'is-off': task.status === 'inactive' }"
            data-test="schedule-item"
          >
            <div class="task-main">
              <p class="task-payload break" data-test="schedule-payload-text">{{ task.payload }}</p>
              <p class="task-meta">
                <Icon name="clock" :size="12" />
                <span data-test="schedule-rule">{{ rule(task) }}</span>
                <span class="state" :class="`is-${stateOf(task).tone}`" data-test="schedule-state">{{ stateOf(task).text }}</span>
              </p>
              <p class="task-meta times">
                <span v-if="task.nextAt !== null" data-test="schedule-next">下次 {{ format(task.nextAt) }}</span>
                <span v-if="task.lastAt !== null" data-test="schedule-last">上次 {{ format(task.lastAt) }}</span>
              </p>
              <p v-if="task.lastError" class="field-error break" data-test="schedule-task-error">{{ task.lastError }}</p>
            </div>

            <div class="task-actions">
              <button v-if="canToggle(task)" class="ghost" data-test="schedule-toggle" @click="toggle(task)">
                {{ task.status === 'inactive' ? '恢复' : '暂停' }}
              </button>
              <button
                class="ghost danger"
                :class="{ 'is-armed': armed === task.id }"
                data-test="schedule-delete"
                @click="remove(task)"
                @blur="armed = null"
              >
                <Icon name="trash" :size="14" /> {{ armed === task.id ? '确认删除' : '删除' }}
              </button>
            </div>
          </section>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.schedules-scroll {
  flex: 1;
  min-block-size: 0;
}

.compose {
  padding: var(--space-2) var(--space-5) var(--space-4);
}

.compose-head {
  display: flex;
  align-items: center;
  min-block-size: 3rem;
}

.compose-title {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  font-size: 1rem;
  font-weight: 600;
}

.examples {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  padding-block-end: var(--space-2);
}

.example {
  min-block-size: 1.75rem;
  padding-inline: var(--space-3);
  font-size: var(--fs-meta);
}

.interval {
  display: inline-flex;
  gap: var(--space-2);
}

.interval input {
  inline-size: 5rem;
}

.compose-foot {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-4);
  padding-block-start: var(--space-3);
}

.compose-foot .field-error {
  flex: 1;
  margin: 0;
  text-align: start;
}

.summary-line {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin: calc(var(--space-2) * -1) 0 0;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.refresh {
  min-block-size: 1.75rem;
  inline-size: 1.75rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.field-error {
  margin: 0;
  color: var(--error);
  font-size: var(--fs-meta);
}

.list-error {
  margin-block-start: calc(var(--space-2) * -1);
}

.tasks {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
}

.task {
  display: flex;
  align-items: center;
  gap: var(--space-4);
  padding: var(--space-4) var(--space-5);
  transition: opacity var(--motion-base) var(--ease);
}

/* Paused or finished: still readable, but plainly not doing anything. */
.task.is-off {
  opacity: 0.62;
}

.task-main {
  flex: 1;
  min-inline-size: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-1);
}

.task-payload {
  margin: 0;
  font-weight: 500;
  display: -webkit-box;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
  white-space: pre-wrap;
}

.task-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin: 0;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.task-meta.times {
  gap: var(--space-4);
}

.state {
  padding-inline: var(--space-2);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  color: var(--text-2);
}

.state.is-on {
  color: var(--accent);
}

.state.is-bad {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}

.task-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: var(--space-2);
}

/* A quiet delete: red only once armed or hovered, so a column of tasks is not a column of red buttons. */
.task-actions .danger {
  background: transparent;
  color: var(--text-3);
}

.task-actions .danger:hover:not(:disabled),
.task-actions .danger.is-armed {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}
</style>
