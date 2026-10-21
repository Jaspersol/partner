import { t, tr } from './i18n'
/**
 * What a session event's `kind` says to a reader.
 *
 * <para>
 * The wire value is a C# enum name (`ToolCall`, `ThinkingStreaming`, `StepStart`…), which must not be rendered
 * verbatim. A word rather than a colour, because 已编辑 and 已失效 already own colour in this view.
 * </para>
 *
 * <para>
 * An unknown kind falls through to the raw value rather than to a placeholder: it is the one a reader cannot
 * guess. `toolAppearance.ts` follows the same rule for unknown tool names.
 * </para>
 */

/**
 * Wire value → what the row says it is.
 *
 * <para>
 * `EventList` folds `ToolCall`/`ToolResult` into one card and `Thinking` into `ThinkingRow`, so those never
 * reach the meta line here; the pairs are listed anyway so the mapping describes the enum, not the rendering.
 * </para>
 */
const KIND_LABEL: Record<string, string> = {
  User: tr('用户'),
  Assistant: tr('助手'),
  System: tr('系统'),
  Tool: tr('工具'),
  Schedule: tr('调度'),
  Summary: tr('摘要'),
  Edit: tr('编辑'),
  Retract: tr('撤回'),
}

/**
 * What a session event's `source` says to a reader.
 *
 * <para>
 * `Agent` gets no label: the server filters sub-agent reports out of the view, so the remaining `Agent` rows
 * are the main agent's ordinary replies. Unknown values fall through verbatim.
 * </para>
 */
const SOURCE_LABEL: Record<string, string> = {
  Voice: tr('语音'),
  Text: tr('文本'),
  Schedule: tr('调度'),
  Agent: '',
}

/** The source's word, or the source itself when it is not one this list knows. */
export function sourceLabel(source: string): string {
  const label = SOURCE_LABEL[source]
  return label === undefined ? source : t(label)
}

/**
 * Whether a row should carry a source chip at all.
 *
 * `Agent` is deliberately absent: the remaining `Agent` rows are ordinary assistant replies, so a chip would be
 * a label the reader can only misread.
 */
export function sourceHasChip(source: string): boolean {
  return source !== '' && SOURCE_LABEL[source] !== ''
}

/**
 * The kind's word, or the kind itself when it is not one this list knows.
 *
 * @param kind the wire value, exactly as the log carries it
 */
export function sessionKindLabel(kind: string): string {
  const label = KIND_LABEL[kind]
  return label === undefined ? kind : t(label)
}

/**
 * What kind of participant a row is, for the colour dot beside its name.
 *
 * <para>
 * The dot is a *second* channel, never the first: the role word is already in the row, so a reader who cannot
 * separate the tints loses nothing. It buys scanning speed.
 * </para>
 *
 * <para>
 * Keys are roles rather than kinds because several kinds share a role: a tool call and its result are both the
 * machine.
 * </para>
 */
export type SessionRole = 'user' | 'assistant' | 'system' | 'tool' | 'schedule' | 'summary' | 'edit'

const KIND_ROLE: Record<string, SessionRole> = {
  User: 'user',
  Assistant: 'assistant',
  System: 'system',
  Tool: 'tool',
  ToolCall: 'tool',
  ToolResult: 'tool',
  Schedule: 'schedule',
  Summary: 'summary',
  Edit: 'edit',
}

/** The dot's role, or null for an unknown kind — which then gets the neutral dot. */
export function sessionRole(kind: string): SessionRole | null {
  return KIND_ROLE[kind] ?? null
}
