<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { McpSettings, McpServerSettings, PartnerApiSurface, RuntimeSettings } from '../api'
import HintTip from '../components/HintTip.vue'
import Icon from '../components/Icon.vue'
import SaveBar from '../components/SaveBar.vue'
import SettingRow from '../components/SettingRow.vue'

/**
 * The MCP servers this installation starts, and how the model is allowed to call them.
 *
 * <para>
 * Editing is a settings save like any other (ui.md): nothing here is optimistic, and the list on screen is
 * what the server kept. The alternative — a separate store — would mean two places where "which servers are
 * running" is answered.
 * </para>
 *
 * <para>
 * The 「免审批」 switch that used to live in a separate list at the top of the page is now on the server it
 * belongs to. The stored shape is unchanged — it is still the `mcp_<name>_*` entry in `allowedTools` — only the
 * place a person finds it moved.
 * </para>
 */
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ changed: [] }>()

const form = ref<McpSettings>({ enabled: false, servers: [], allowedTools: [] })
const baseline = ref<RuntimeSettings | null>(null)
const error = ref('')
const notice = ref('')

/** The form as last stored, so "changed" is a comparison and not a flag some handler remembered to set. */
const stored = ref<string | null>(null)
const dirty = computed(() => stored.value !== null && JSON.stringify(form.value) !== stored.value)

/** Which server cards are unfolded, by position. A new server opens itself: it is empty and about to be filled. */
const open = ref<boolean[]>([])
/** Set once a save has been refused for an incomplete server, so the empty fields can say which ones. */
const attempted = ref(false)

function blankServer(): McpServerSettings {
  return { name: '', command: '', arguments: [], workingDirectory: null, callTimeoutSeconds: 30 }
}

function addServer() {
  form.value = { ...form.value, servers: [...form.value.servers, blankServer()] }
  open.value = [...open.value, true]
}

function removeServer(index: number) {
  const gone = form.value.servers[index]
  form.value = {
    ...form.value,
    servers: form.value.servers.filter((_, at) => at !== index),
    // Its direct-run entry goes with it: a rule for a server that no longer exists would silently apply to the
    // next one given the same name.
    allowedTools: gone ? form.value.allowedTools.filter((tool) => tool !== toolKey(gone.name)) : form.value.allowedTools,
  }
  open.value = open.value.filter((_, at) => at !== index)
}

function updateServer(index: number, patch: Partial<McpServerSettings>) {
  const before = form.value.servers[index]
  const renamed = patch.name !== undefined && before !== undefined && patch.name !== before.name

  form.value = {
    ...form.value,
    servers: form.value.servers.map((server, at) => (at === index ? { ...server, ...patch } : server)),
    // The direct-run entry is keyed by name, so renaming a server carries its switch along.
    allowedTools:
      renamed && before && form.value.allowedTools.includes(toolKey(before.name))
        ? form.value.allowedTools.map((tool) => (tool === toolKey(before.name) ? toolKey(patch.name ?? '') : tool))
        : form.value.allowedTools,
  }
}

/** One argument per line: a command line pasted as a string is not a list the process can take. */
function updateArguments(index: number, text: string) {
  updateServer(index, {
    arguments: text
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0),
  })
}

function toolKey(name: string): string {
  return `mcp_${name}_*`
}

function isDirect(server: McpServerSettings): boolean {
  return form.value.allowedTools.includes(toolKey(server.name))
}

function toggleDirect(server: McpServerSettings) {
  const key = toolKey(server.name)
  form.value = {
    ...form.value,
    allowedTools: isDirect(server) ? form.value.allowedTools.filter((tool) => tool !== key) : [...form.value.allowedTools, key],
  }
}

function toggleOpen(index: number) {
  open.value = open.value.map((value, at) => (at === index ? !value : value))
}

/** A one-line description of what a server runs, for the folded card. */
function commandLine(server: McpServerSettings): string {
  return [server.command, ...server.arguments].join(' ').trim()
}

function missing(server: McpServerSettings, field: 'name' | 'command'): boolean {
  return attempted.value && server[field].trim() === ''
}

function adopt(settings: RuntimeSettings) {
  baseline.value = settings
  form.value = { ...settings.mcp, servers: [...settings.mcp.servers], allowedTools: [...settings.mcp.allowedTools] }
  open.value = form.value.servers.map(() => false)
  stored.value = JSON.stringify(form.value)
  attempted.value = false
}

async function refresh() {
  error.value = ''
  try {
    adopt(await props.api.settings())
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save() {
  if (!baseline.value) {
    error.value = '先读取设置'
    return
  }

  // A server with no name or command would start a process the operator cannot identify, so it is refused
  // here rather than at start-up where the failure would be one line in a log.
  const incomplete = form.value.servers.findIndex((server) => server.name.trim() === '' || server.command.trim() === '')
  if (incomplete >= 0) {
    attempted.value = true
    open.value = open.value.map((value, at) => (at === incomplete ? true : value))
    error.value = '每个服务都要有名称和启动命令'
    return
  }

  error.value = ''
  notice.value = ''
  try {
    const saved = await props.api.saveSettings({ ...baseline.value, mcp: form.value })
    adopt(saved.settings)
    notice.value = '已保存，助手已重新加载'
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)

/** Asked by the shell before it switches away: the views are `v-if`, so leaving discards every edit. */
function hasUnsavedChanges(): boolean {
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="scroll mcp-scroll">
      <div class="g-page">
        <!--
          Off by default and said plainly, because enabling is not "let the model ask a tool" — it is "let a child
          process run". A switch that reads like a feature toggle and starts processes is how someone ends up
          surprised by what is on their machine.
        -->
        <label class="master g-glass">
          <span class="master-text">
            <span class="master-title">
              启用 MCP
              <HintTip>开启后，助手启动时会运行下面配置的程序，并使用它们提供的工具。默认关闭。</HintTip>
            </span>
            <span class="master-sub">让助手使用其他程序提供的工具</span>
          </span>
          <input v-model="form.enabled" type="checkbox" class="g-switch" role="switch" data-test="mcp-enabled" />
        </label>

        <p class="summary-line">
          <span data-test="mcp-summary">{{ form.servers.length }} 个服务 · {{ form.enabled ? '已启用' : '未启用' }}</span>
          <button class="ghost refresh" aria-label="刷新" title="刷新" data-test="mcp-refresh" @click="refresh">
            <Icon name="refresh" :size="14" />
          </button>
        </p>

        <div class="servers" :class="{ 'is-off': !form.enabled }">
          <p v-if="form.servers.length === 0" class="empty" data-test="mcp-empty">还没有服务。添加一个，助手才有东西可用。</p>

          <section v-for="(server, index) in form.servers" :key="index" class="server g-glass" data-test="mcp-server">
            <header class="server-head">
              <button class="server-toggle" :aria-expanded="open[index] === true" @click="toggleOpen(index)">
                <Icon name="chevron-down" :size="14" class="chevron" :class="{ 'is-open': open[index] === true }" />
                <span class="server-name">{{ server.name || '（未命名）' }}</span>
                <span class="server-command break">{{ commandLine(server) }}</span>
              </button>
              <label class="direct">
                免审批
                <HintTip align="end">开启后，这个服务的工具直接执行，不再逐次询问你。关闭则每次都要批准。</HintTip>
                <input
                  type="checkbox"
                  class="g-switch"
                  role="switch"
                  :checked="isDirect(server)"
                  data-test="mcp-server-direct"
                  @change="toggleDirect(server)"
                />
              </label>
              <button class="ghost danger" aria-label="移除这个服务" data-test="mcp-server-remove" @click="removeServer(index)">
                <Icon name="trash" :size="14" /> 移除
              </button>
            </header>

            <div v-show="open[index] === true" class="server-body">
              <SettingRow label="名称" hint="给这个服务起个名字，如 files。">
                <input
                  :value="server.name"
                  data-test="mcp-server-name"
                  placeholder="files"
                  :aria-invalid="missing(server, 'name') || undefined"
                  @input="updateServer(index, { name: ($event.target as HTMLInputElement).value })"
                />
              </SettingRow>
              <p v-if="missing(server, 'name')" class="field-error">请填写名称</p>

              <SettingRow label="启动命令">
                <template #hint>
                  要运行的程序。只写名字时，只会在系统 PATH 里查找；用了别名或自定义路径的请写完整路径。
                </template>
                <input
                  :value="server.command"
                  data-test="mcp-server-command"
                  placeholder="node"
                  :aria-invalid="missing(server, 'command') || undefined"
                  @input="updateServer(index, { command: ($event.target as HTMLInputElement).value })"
                />
              </SettingRow>
              <p v-if="missing(server, 'command')" class="field-error">请填写启动命令</p>

              <SettingRow label="启动参数" hint="每行一个参数，不要把整条命令写在一行里。" stack>
                <textarea
                  :value="server.arguments.join('\n')"
                  data-test="mcp-server-args"
                  rows="3"
                  @input="updateArguments(index, ($event.target as HTMLTextAreaElement).value)"
                />
              </SettingRow>

              <SettingRow label="运行目录" hint="程序启动时所在的文件夹，可以留空。">
                <input
                  :value="server.workingDirectory ?? ''"
                  data-test="mcp-server-cwd"
                  @input="updateServer(index, { workingDirectory: ($event.target as HTMLInputElement).value || null })"
                />
              </SettingRow>

              <SettingRow label="最长等待" unit="秒" hint="一次工具调用超过这个时间还没结果就放弃。默认 30 秒。">
                <input
                  :value="server.callTimeoutSeconds"
                  type="number"
                  min="1"
                  data-test="mcp-server-timeout"
                  @input="updateServer(index, { callTimeoutSeconds: Math.max(1, Number(($event.target as HTMLInputElement).value) || 30) })"
                />
              </SettingRow>
            </div>
          </section>

          <button class="ghost add" data-test="mcp-server-add" @click="addServer">
            <Icon name="plus" :size="14" /> 添加服务
          </button>
        </div>
      </div>
    </div>

    <SaveBar
      prefix="mcp"
      status-test="mcp-notice"
      :dirty="dirty"
      :status="notice"
      :error="error"
      can-discard
      @save="save"
      @discard="refresh"
    />
  </div>
</template>

<style scoped>
.mcp-scroll {
  flex: 1;
  min-block-size: 0;
}

.master {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  padding: var(--space-4) var(--space-5);
  cursor: pointer;
}

.master-text {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 2px;
}

.master-title {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  font-size: 1rem;
  font-weight: 600;
}

.master-sub {
  color: var(--text-2);
  font-size: var(--fs-ui);
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

.servers {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-3);
  transition: opacity var(--motion-base) var(--ease);
}

/* Off is still editable — the list is configuration — but it should look like it is not doing anything yet. */
.servers.is-off {
  opacity: 0.6;
}

.server {
  padding: var(--space-2) var(--space-5);
}

.server-head {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-block-size: 3.5rem;
}

.server-toggle {
  flex: 1;
  min-inline-size: 0;
  justify-content: flex-start;
  gap: var(--space-3);
  padding: 0;
  border: none;
  background: transparent;
  box-shadow: none;
  text-align: start;
}

.server-toggle:hover:not(:disabled) {
  background: transparent;
}

.server-name {
  flex: none;
  font-weight: 600;
}

.server-command {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-3);
  font-family: var(--font-mono);
  font-size: var(--fs-meta);
}

.chevron {
  flex: none;
  transition: transform var(--motion-fast) var(--ease);
  transform: rotate(-90deg);
  color: var(--text-3);
}

.chevron.is-open {
  transform: none;
}

.direct {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  flex: none;
  color: var(--text-2);
  font-size: var(--fs-ui);
  cursor: pointer;
}

/* A quiet remove: red only once the pointer is on it, so a column of servers is not a column of red buttons. */
.server-head .danger {
  background: transparent;
  color: var(--text-3);
}

.server-head .danger:hover:not(:disabled) {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}

.server-body {
  padding-block-end: var(--space-3);
}

.field-error {
  margin: 0;
  padding-block-end: var(--space-2);
  color: var(--error);
  font-size: var(--fs-meta);
  text-align: end;
}

.add {
  justify-self: start;
  color: var(--text-2);
}

input[aria-invalid='true'] {
  border-color: var(--error);
}
</style>
