/**
 * Slash commands typed into the composer.
 *
 * <para>
 * A command must be the whole trimmed line: a message that merely mentions "/status" goes to the model as text.
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
