/**
 * Slash commands typed into the composer.
 *
 * <para>
 * A command is a whole line that begins with a slash, and nothing else: a message that merely mentions
 * "/status" — "搜了一下 /status 的实现" — must still go to the model as text, because that is what the person
 * typing it meant. Matching is therefore anchored to the start of the trimmed line and requires the command to
 * be the entire line.
 * </para>
 *
 * <para>
 * Only one command exists. It is recognised here rather than in the component so the rule is testable on its
 * own, and so a second command is added by adding a case, not by editing a keydown handler.
 * </para>
 */

export interface ComposerCommand {
  /** What the composer does with the box: show the queue without sending anything. */
  kind: 'status'
}

/** Parses one line into a command, or null when it is an ordinary message. */
export function parseComposerCommand(line: string): ComposerCommand | null {
  const text = line.trim()

  if (!text.startsWith('/')) {
    return null
  }

  return text === '/status' ? { kind: 'status' } : null
}
