<script setup lang="ts">
import { onMounted, ref } from 'vue'
import type { McpSettings, McpServerSettings, PartnerApiSurface, RuntimeSettings } from '../api'

/**
 * The MCP servers this installation starts, and how the model is allowed to call them.
 *
 * <para>
 * Editing is a settings save like any other (ui.md): nothing here is optimistic, and the list on screen is
 * what the server kept. The alternative — a separate store — would mean two places where "which servers are
 * running" is answered.
 * </para>
 */
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string]; changed: [] }>()

const form = ref<McpSettings>({ enabled: false, servers: [], allowedTools: [] })
const baseline = ref<RuntimeSettings | null>(null)
const error = ref('')
const notice = ref('')

function blankServer(): McpServerSettings {
  return { name: '', command: '', arguments: [], workingDirectory: null, callTimeoutSeconds: 30 }
}

function addServer() {
  form.value = { ...form.value, servers: [...form.value.servers, blankServer()] }
}

function removeServer(index: number) {
  form.value = { ...form.value, servers: form.value.servers.filter((_, at) => at !== index) }
}

function updateServer(index: number, patch: Partial<McpServerSettings>) {
  form.value = {
    ...form.value,
    servers: form.value.servers.map((server, at) => (at === index ? { ...server, ...patch } : server)),
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

function toggleAllowed(tool: string) {
  const has = form.value.allowedTools.includes(tool)
  form.value = {
    ...form.value,
    allowedTools: has ? form.value.allowedTools.filter((t) => t !== tool) : [...form.value.allowedTools, tool],
  }
}

async function refresh() {
  error.value = ''
  try {
    const settings = await props.api.settings()
    baseline.value = settings
    form.value = settings.mcp
    emit('context', `MCP · ${settings.mcp.enabled ? '已启用' : '未启用'}`)
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
  const incomplete = form.value.servers.find((server) => server.name.trim() === '' || server.command.trim() === '')
  if (incomplete) {
    error.value = '每个服务器都要有名字和命令'
    return
  }

  error.value = ''
  notice.value = ''
  try {
    const saved = await props.api.saveSettings({ ...baseline.value, mcp: form.value })
    baseline.value = saved.settings
    form.value = saved.settings.mcp
    notice.value = '已保存 · 消费者已重启'
    emit('context', `MCP · ${form.value.enabled ? '已启用' : '未启用'}`)
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)
defineExpose({ refresh })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="mcp-refresh" @click="refresh">刷新</button>
      <span class="spacer" />
      <span class="muted" data-test="mcp-summary">
        {{ form.servers.length }} 个服务器 · {{ form.enabled ? '已启用' : '未启用' }}
      </span>
    </div>

    <div class="scroll">
      <section class="card">
        <h2>MCP</h2>
        <!--
          Off by default and said plainly, because enabling is not "let the model ask a tool" — it is "let a
          child process run". A switch that reads like a feature toggle and starts processes is how someone
          ends up surprised by what is on their machine.
        -->
        <label class="mcp-field">
          <input
            v-model="form.enabled"
            type="checkbox"
            data-test="mcp-enabled"
          />
          <span>
            启用 MCP
            <small class="muted">
              启用即在消费者启动时拉起下面配置的子进程，并把它们的工具交给模型。默认关闭。
            </small>
          </span>
        </label>

        <div class="mcp-field">
          <span>直接执行、不必人工批准的工具</span>
          <small class="muted">
            留空表示全部工具都要批准。启用只决定「提供哪些工具」，不决定「哪些可以直接动」。
          </small>
          <div v-if="form.servers.length > 0" class="tools">
            <label v-for="server in form.servers" :key="server.name" class="tool">
              <input
                type="checkbox"
                :checked="form.allowedTools.includes(`mcp_${server.name}_*`)"
                @change="toggleAllowed(`mcp_${server.name}_*`)"
              />
              <span>{{ server.name || '(未命名)' }}</span>
            </label>
          </div>
          <p v-else class="muted">（还没有服务器）</p>
        </div>
      </section>

      <section class="card">
        <h2>服务器</h2>
        <p v-if="form.servers.length === 0" class="muted" data-test="mcp-empty">
          （未配置。启用也不会启动任何东西。）
        </p>

        <div v-for="(server, index) in form.servers" :key="index" class="server" :data-test="'mcp-server'">
          <div class="server-row">
            <label>
              <span>名字</span>
              <input
                :value="server.name"
                data-test="mcp-server-name"
                placeholder="files"
                @input="updateServer(index, { name: ($event.target as HTMLInputElement).value })"
              />
            </label>
            <label>
              <span>命令</span>
              <input
                :value="server.command"
                data-test="mcp-server-command"
                placeholder="node"
                @input="updateServer(index, { command: ($event.target as HTMLInputElement).value })"
              />
              <!-- How the process is started decides what this field accepts: a bare name is looked up on
                   PATH and nothing else. A path that only works because someone's shell sets an alias fails
                   here, at start-up, with the reason a few lines deep in a consumer log. -->
              <span class="source">可执行文件名或绝对路径；裸名字只按 PATH 查找</span>
            </label>
          </div>

          <div class="server-row">
            <label class="grow">
              <span>参数（每行一个）</span>
              <textarea
                :value="server.arguments.join('\n')"
                data-test="mcp-server-args"
                rows="2"
                @input="updateArguments(index, ($event.target as HTMLTextAreaElement).value)"
              />
            </label>
            <label>
              <span>工作目录</span>
              <input
                :value="server.workingDirectory ?? ''"
                data-test="mcp-server-cwd"
                @input="updateServer(index, { workingDirectory: ($event.target as HTMLInputElement).value || null })"
              />
            </label>
            <label>
              <span>超时（秒）</span>
              <input
                :value="server.callTimeoutSeconds"
                type="number"
                min="1"
                data-test="mcp-server-timeout"
                @input="updateServer(index, { callTimeoutSeconds: Math.max(1, Number(($event.target as HTMLInputElement).value) || 30) })"
              />
            </label>
          </div>

          <button class="ghost" data-test="mcp-server-remove" @click="removeServer(index)">移除</button>
        </div>

        <button class="ghost" data-test="mcp-server-add" @click="addServer">＋ 添加服务器</button>
      </section>

      <div class="actions">
        <button class="primary" data-test="mcp-save" @click="save">保存</button>
        <span v-if="notice" class="muted" data-test="mcp-notice">{{ notice }}</span>
      </div>

      <p v-if="error" class="error notice" data-test="error">{{ error }}</p>
    </div>
  </div>
</template>

<style scoped>
.scroll {
  flex: 1;
  min-block-size: 0;
  overflow-y: auto;
  padding: var(--space-3);
  display: grid;
  gap: var(--space-3);
  align-content: start;
}

/* Only the layout. Border, radius, background and padding come from styles.css — repeating them here is how
   this card ended up without the `background: var(--surface-1)` the settings cards have, so two views built with
   the same token set rendered as two different products. */
.card {
  display: grid;
  gap: var(--space-2);
}

h2 {
  font-size: var(--fs-title);
  margin: 0;
}

/* Not `.field`: styles.css has a global one that lays a settings row out as label + auto-sized control,
   and a scoped rule that only sets gap and font-size does not stop that template from applying here. */
.mcp-field {
  display: grid;
  gap: var(--space-1);
  font-size: var(--fs-ui);
}

.mcp-field > label {
  display: flex;
  gap: var(--space-2);
  align-items: baseline;
}

.tools {
  display: grid;
  gap: var(--space-1);
}

.tool {
  display: flex;
  gap: var(--space-2);
  align-items: baseline;
}

.server {
  border: 1px solid var(--line);
  border-radius: var(--radius-sm);
  padding: var(--space-2);
  display: grid;
  gap: var(--space-2);
  margin-block-end: var(--space-2);
}

.server-row {
  display: flex;
  gap: var(--space-2);
  flex-wrap: wrap;
}

.server-row label {
  display: grid;
  gap: 2px;
  font-size: var(--fs-meta);
  color: var(--text-3);
  min-inline-size: 0;
}

.server-row .grow {
  flex: 1;
  min-inline-size: 12rem;
}

.server-row input,
.server-row textarea {
  font-size: var(--fs-ui);
  color: var(--text);
}

/* The global rule is scoped to .field, which this row is not, so the hint needs its own. */
.server-row .source {
  font-size: var(--fs-meta);
  line-height: 1.4;
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
</style>
