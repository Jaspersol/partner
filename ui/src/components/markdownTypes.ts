/**
 * The shapes the Markdown renderer works in. Kept apart from the parser so the recursive run renderer can
 * use the same types without importing a component.
 */

/**
 * One inline run. Emphasis carries parsed runs rather than raw text: a `**` span can contain a code span,
 * and carrying the inner text as a string would hand the reader the raw backticks.
 */
export type Inline =
  | { kind: 'text'; value: string }
  | { kind: 'code'; value: string }
  | { kind: 'strong'; runs: Inline[] }
  | { kind: 'em'; runs: Inline[] }

export type Block =
  | { kind: 'paragraph'; lines: Inline[][] }
  | { kind: 'heading'; level: number; lines: Inline[][] }
  | { kind: 'quote'; lines: Inline[][] }
  | { kind: 'code'; text: string; language: string }
  | { kind: 'list'; ordered: boolean; items: Inline[][] }
  | { kind: 'table'; header: string[]; rows: string[][] }
  | { kind: 'rule' }
