/**
 * What one tool looks like: a glyph to scan by and a hue to sort by.
 *
 * <para>
 * Giving each family its own glyph and accent makes a list of tool names scannable at a glance.
 * </para>
 *
 * <para>
 * Families are matched by prefix, first match wins: `desktop_exec` and `desktop_clipboard_get` are different
 * things. A name nothing matches falls back rather than being hidden — an unrecognised tool is exactly the one
 * a reader needs to see.
 * </para>
 */

import type { IconName } from './icons'

export type ToolFamily =
  | 'shell'
  | 'read'
  | 'write'
  | 'search'
  | 'web'
  | 'agent'
  | 'record'
  | 'schedule'
  | 'control'

interface ToolAppearance {
  icon: IconName
  family: ToolFamily
  /** The word a reader scans for, used as the card's title attribute. */
  label: string
}

/** Prefix → appearance. Order matters: the first match wins, so specific names come before families. */
const RULES: readonly (readonly [string, ToolAppearance])[] = [
  ['desktop_screenshot', { icon: 'file', family: 'read', label: '截图' }],
  ['desktop_observe', { icon: 'file', family: 'read', label: '观察' }],
  ['desktop_clipboard', { icon: 'file', family: 'read', label: '剪贴板' }],
  ['desktop_act', { icon: 'crosshair', family: 'control', label: '桌面动作' }],
  ['desktop_launch', { icon: 'terminal', family: 'shell', label: '启动' }],
  ['desktop_exec', { icon: 'terminal', family: 'shell', label: '执行' }],

  ['memory_recall', { icon: 'database', family: 'record', label: '记忆检索' }],
  ['memory_write', { icon: 'database', family: 'record', label: '记忆写入' }],
  ['memory', { icon: 'database', family: 'record', label: '记忆' }],
  ['recall', { icon: 'database', family: 'record', label: '检索' }],

  ['skill', { icon: 'swap', family: 'agent', label: '技能' }],
  ['mcp', { icon: 'swap', family: 'agent', label: 'MCP' }],
  // Delegation tools, before the generic `agent` prefix so the specific verbs win.
  ['agent_delegate', { icon: 'swap', family: 'agent', label: '委派' }],
  ['agent_job_status', { icon: 'swap', family: 'agent', label: '任务进度' }],
  ['agent_job_cancel', { icon: 'swap', family: 'agent', label: '取消任务' }],
  ['agent', { icon: 'swap', family: 'agent', label: 'Agent' }],
  ['task_report', { icon: 'swap', family: 'agent', label: '汇报' }],
  ['task', { icon: 'swap', family: 'agent', label: '子任务' }],

  // File-read tools, before the generic read/list rules so they get their own word.
  ['file_list', { icon: 'file', family: 'read', label: '列目录' }],
  ['file_read', { icon: 'file', family: 'read', label: '读文件' }],

  // The assistant's own timed-task tools, before the generic prefix.
  ['schedule_create', { icon: 'clock', family: 'schedule', label: '创建定时' }],
  ['schedule_list', { icon: 'clock', family: 'schedule', label: '定时列表' }],
  ['schedule_delete', { icon: 'clock', family: 'schedule', label: '删除定时' }],
  ['schedule', { icon: 'flag', family: 'schedule', label: '调度' }],
  ['cron', { icon: 'flag', family: 'schedule', label: '调度' }],

  ['search', { icon: 'search', family: 'search', label: '搜索' }],
  ['glob', { icon: 'search', family: 'search', label: '查找文件' }],
  ['grep', { icon: 'search', family: 'search', label: '搜索' }],
  ['find', { icon: 'search', family: 'search', label: '查找' }],

  // The two web tools, before the generic `web` and `fetch` rules so they do not both read as 抓取.
  ['web_search', { icon: 'search', family: 'web', label: '联网搜索' }],
  ['web_fetch', { icon: 'download', family: 'web', label: '读网页' }],

  ['fetch', { icon: 'download', family: 'web', label: '抓取' }],
  ['web', { icon: 'download', family: 'web', label: '网页' }],
  ['http', { icon: 'download', family: 'web', label: '网络请求' }],
  ['browse', { icon: 'download', family: 'web', label: '浏览' }],

  ['read', { icon: 'file', family: 'read', label: '读取' }],
  ['list', { icon: 'file', family: 'read', label: '列出' }],
  ['view', { icon: 'file', family: 'read', label: '查看' }],

  ['write', { icon: 'pencil', family: 'write', label: '写入' }],
  ['edit', { icon: 'pencil', family: 'write', label: '编辑' }],
  ['patch', { icon: 'pencil', family: 'write', label: '修改' }],
  ['append', { icon: 'pencil', family: 'write', label: '追加' }],

  ['bash', { icon: 'terminal', family: 'shell', label: '命令' }],
  ['shell', { icon: 'terminal', family: 'shell', label: '命令' }],
  ['exec', { icon: 'terminal', family: 'shell', label: '执行' }],
  ['run', { icon: 'terminal', family: 'shell', label: '运行' }],
  ['process', { icon: 'terminal', family: 'shell', label: '进程' }],
]

/** An unrecognised tool gets the neutral one — visible, just not categorised. */
const FALLBACK: ToolAppearance = { icon: 'diamond', family: 'control', label: '工具' }

export function toolAppearance(name: string): ToolAppearance {
  const lower = name.toLowerCase()

  for (const [prefix, appearance] of RULES) {
    if (lower.startsWith(prefix)) {
      return appearance
    }
  }

  return FALLBACK
}
