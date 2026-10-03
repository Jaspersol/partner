/**
 * What a session event's `kind` says to a reader (ui.md §5.2).
 *
 * <para>
 * The wire value is a C# enum name (`ToolCall`, `ThinkingStreaming`, `StepStart`…), and rendering it verbatim
 * puts an internal spelling in front of whoever is reading the log. `ui.md §5.2` asks for a *word* — and it
 * asks for a word rather than a colour because colour is already spoken for in this view: 已编辑 and 已失效 own
 * it, so a third meaning on the same channel would have nothing left to say.
 * </para>
 *
 * <para>
 * A kind nothing matches falls through to the raw value rather than to a placeholder. An unrecognised kind is
 * exactly the one a reader needs to see, because it is the one they cannot guess — the same rule
 * `toolAppearance.ts` follows for an unrecognised tool name.
 * </para>
 */

/**
 * Wire value → what the row says it is.
 *
 * <para>
 * Only the kinds a reader can actually encounter as a row are listed. `EventList` folds `ToolCall`/`ToolResult`
 * into one card and `Thinking` into `ThinkingRow`, so those never reach the meta line here; the pairs are here
 * anyway so the mapping stays a description of the enum rather than of the current rendering.
 * </para>
 */
const KIND_LABEL: Record<string, string> = {
  User: '用户',
  Assistant: '助手',
  System: '系统',
  Tool: '工具',
  Schedule: '调度',
  Summary: '摘要',
  Edit: '编辑',
  Retract: '撤回',
}

/**
 * The kind's word, or the kind itself when it is not one this list knows.
 *
 * @param kind the wire value, exactly as the log carries it
 */
export function sessionKindLabel(kind: string): string {
  return KIND_LABEL[kind] ?? kind
}

/**
 * What kind of participant a row is, for the colour dot beside its name.
 *
 * <para>
 * A dot is a *second* channel, never the first: the role word is already in the row in text, and a reader who
 * cannot separate the tints loses nothing (ui.md §5.2 — colour cannot be the only thing saying what a row is).
 * What the dot buys is scanning — a turn of twenty tool calls can be found by looking for the shape rather
 * than reading twenty role words.
 * </para>
 *
 * <para>
 * The keys are roles rather than kinds because several kinds share a role, and the reader only cares about
 * "who said this": a tool call and its result are both the machine.
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

/** The dot's role, or null for a kind this list has never heard of — which then gets the neutral dot. */
export function sessionRole(kind: string): SessionRole | null {
  return KIND_ROLE[kind] ?? null
}
