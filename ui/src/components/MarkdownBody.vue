<script setup lang="ts">
import { computed } from 'vue'

/**
 * A deliberately small Markdown renderer (ui.md §5.2): quotes, headings, lists, fenced code, tables and
 * inline code/emphasis — enough for the session log to read like a document.
 *
 * It renders through Vue's text interpolation, never `v-html`, so a body containing HTML is shown as text.
 * That matters here: session bodies are model output, and this view can drive the desktop.
 */

import type { Block, Inline } from './markdownTypes'
import MarkdownInline from './MarkdownInline.vue'

const props = defineProps<{ body: string }>()

/** Splits one line into inline runs. Order matters: code spans win over emphasis. */
function inline(line: string): Inline[] {
  const runs: Inline[] = []
  const pattern = /`([^`]+)`|\*\*([^*]+)\*\*|__([^_]+)__|\*([^*]+)\*|_([^_]+)_/g
  let cursor = 0

  for (const match of line.matchAll(pattern)) {
    const at = match.index ?? 0
    if (at > cursor) {
      runs.push({ kind: 'text', value: line.slice(cursor, at) })
    }

    const [, code, strong, strongAlt, em, emAlt] = match
    if (code !== undefined) {
      // A code span is literal: its contents are never re-parsed, so backticks inside stay as text.
      runs.push({ kind: 'code', value: code })
    } else if (strong !== undefined || strongAlt !== undefined) {
      // Emphasis wraps what is inside it, so `**` around `` `x` `` has to parse the inner code span
      // rather than carry it as raw text — otherwise the backticks reach the reader verbatim.
      runs.push({ kind: 'strong', runs: inline(strong ?? strongAlt ?? '') })
    } else {
      runs.push({ kind: 'em', runs: inline(em ?? emAlt ?? '') })
    }

    cursor = at + match[0].length
  }

  if (cursor < line.length) {
    runs.push({ kind: 'text', value: line.slice(cursor) })
  }

  return runs.length > 0 ? runs : [{ kind: 'text', value: line }]
}

function splitRow(line: string): string[] {
  return line
    .replace(/^\||\|$/g, '')
    .split('|')
    .map((cell) => cell.trim())
}

function parse(text: string): Block[] {
  const blocks: Block[] = []

  // Carriage returns are normalised here rather than tolerated pattern by pattern, because they are not
  // whitespace to a regular expression: in JavaScript `.` does not match `\r` — it is a line terminator, like
  // `\n` — so every pattern whose tail is `(.*)$` stops matching the moment a line ends in CR. A body that
  // came from a Windows tool (a `desktop_exec` result carrying PowerShell's stderr, for one) is CRLF
  // throughout, and the damage is silent: the line renders, just as the wrong thing.
  //
  // It was also not merely cosmetic. The list branch is what advances the cursor past a bullet line, so when
  // it stopped matching, *no* branch consumed that line — the paragraph branch below breaks immediately on a
  // line that "starts a new block", pushes nothing and leaves `index` alone. The loop came back to the same
  // index with the same line and spun there at full speed, which is a session view that renders its first
  // screenful and then stops answering anything at all.
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  let index = 0

  while (index < lines.length) {
    const line = lines[index] ?? ''

    if (line.trim() === '') {
      index++
      continue
    }

    if (/^```/.test(line.trim())) {
      const language = line.trim().slice(3).trim()
      const body: string[] = []
      index++
      while (index < lines.length && !/^```/.test((lines[index] ?? '').trim())) {
        body.push(lines[index] ?? '')
        index++
      }
      index++ // closing fence
      blocks.push({ kind: 'code', text: body.join('\n'), language })
      continue
    }

    if (/^(---|\*\*\*|___)$/.test(line.trim())) {
      blocks.push({ kind: 'rule' })
      index++
      continue
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line)
    if (heading) {
      blocks.push({ kind: 'heading', level: (heading[1] ?? '#').length, lines: [inline(heading[2] ?? '')] })
      index++
      continue
    }

    // A table needs its separator row to be recognisable as one.
    if (line.includes('|') && /^\s*\|?[\s:|-]+\|[\s:|-]*$/.test(lines[index + 1] ?? '')) {
      const header = splitRow(line)
      const rows: string[][] = []
      index += 2
      while (index < lines.length && (lines[index] ?? '').includes('|')) {
        rows.push(splitRow(lines[index] ?? ''))
        index++
      }
      blocks.push({ kind: 'table', header, rows })
      continue
    }

    if (/^\s*>\s?/.test(line)) {
      const quoted: Inline[][] = []
      while (index < lines.length && /^\s*>\s?/.test(lines[index] ?? '')) {
        quoted.push(inline((lines[index] ?? '').replace(/^\s*>\s?/, '')))
        index++
      }
      blocks.push({ kind: 'quote', lines: quoted })
      continue
    }

    const listItem = /^\s*(?:([-*+])|(\d+)[.)])\s+(.*)$/.exec(line)
    if (listItem) {
      const ordered = listItem[2] !== undefined
      const items: Inline[][] = []
      while (index < lines.length) {
        const item = /^\s*(?:([-*+])|(\d+)[.)])\s+(.*)$/.exec(lines[index] ?? '')
        if (!item || (item[2] !== undefined) !== ordered) {
          break
        }
        items.push(inline(item[3] ?? ''))
        index++
      }
      blocks.push({ kind: 'list', ordered, items })
      continue
    }

    const paragraph: Inline[][] = []
    while (index < lines.length && (lines[index] ?? '').trim() !== '') {
      const next = lines[index] ?? ''

      // Only a line that *follows* something may end the paragraph. A line that merely looks like the start of
      // another block is what makes this loop consume nothing — and a loop that consumes nothing is the whole
      // failure above: the branches above have all just declined this line, so if this one declines it too,
      // the outer `while` returns to an unchanged `index` and never leaves. Taking the first line
      // unconditionally is also the better reading: a line no branch claimed is a paragraph, not nothing.
      if (
        paragraph.length > 0 &&
        (/^(```|#{1,6}\s|>|\s*[-*+]\s|\s*\d+[.)]\s)/.test(next) || /^(---|\*\*\*|___)$/.test(next.trim()))
      ) {
        break
      }

      paragraph.push(inline(next))
      index++
    }

    if (paragraph.length > 0) {
      blocks.push({ kind: 'paragraph', lines: paragraph })
    }
  }

  return blocks
}

const blocks = computed(() => parse(props.body))
</script>

<template>
  <div class="markdown">
    <template v-for="(block, index) in blocks" :key="index">
      <hr v-if="block.kind === 'rule'" />

      <pre v-else-if="block.kind === 'code'" :data-language="block.language"><code>{{ block.text }}</code></pre>

      <table v-else-if="block.kind === 'table'">
        <thead>
          <tr>
            <th v-for="(cell, cellIndex) in block.header" :key="cellIndex">{{ cell }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, rowIndex) in block.rows" :key="rowIndex">
            <td v-for="(cell, cellIndex) in row" :key="cellIndex">{{ cell }}</td>
          </tr>
        </tbody>
      </table>

      <blockquote v-else-if="block.kind === 'quote'">
        <p v-for="(line, lineIndex) in block.lines" :key="lineIndex">
          <MarkdownInline v-for="(run, runIndex) in line" :key="runIndex" :run="run" />
        </p>
      </blockquote>

      <ol v-else-if="block.kind === 'list' && block.ordered">
        <li v-for="(item, itemIndex) in block.items" :key="itemIndex">
          <MarkdownInline v-for="(run, runIndex) in item" :key="runIndex" :run="run" />
        </li>
      </ol>

      <ul v-else-if="block.kind === 'list'">
        <li v-for="(item, itemIndex) in block.items" :key="itemIndex">
          <MarkdownInline v-for="(run, runIndex) in item" :key="runIndex" :run="run" />
        </li>
      </ul>

      <component v-else-if="block.kind === 'heading'" :is="`h${block.level}`">
        <MarkdownInline v-for="(run, runIndex) in block.lines[0] ?? []" :key="runIndex" :run="run" />
      </component>

      <p v-else v-for="(line, lineIndex) in block.kind === 'paragraph' ? block.lines : []" :key="lineIndex">
        <MarkdownInline v-for="(run, runIndex) in line" :key="runIndex" :run="run" />
      </p>
    </template>
  </div>
</template>

<style scoped>
/* Long identifiers must wrap in prose, but never mid-token inside code or a table cell: a wrapped
   identifier reads as two different ones. */
.markdown {
  overflow-wrap: anywhere;
}

.markdown pre,
.markdown code,
.markdown td,
.markdown th {
  overflow-wrap: normal;
}

.markdown p {
  margin: 0 0 var(--space-2);
}

.markdown blockquote {
  margin: 0 0 var(--space-2);
  padding: var(--space-1) var(--space-3);
  border-inline-start: 0.2em solid var(--line);
  border-radius: var(--radius-sm);
  background: var(--surface-0);
  color: var(--text-2);
}

.markdown blockquote p:last-child {
  margin-block-end: 0;
}

.markdown code {
  padding: 0.1em 0.35em;
  border-radius: var(--radius-sm);
  background: var(--surface-0);
  border: 1px solid var(--line);
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
}

.markdown pre {
  margin: 0 0 var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  background: var(--surface-0);
  border: 1px solid var(--line);
  overflow: auto;
}

.markdown pre code {
  padding: 0;
  border: none;
  background: none;
}

.markdown table {
  border-collapse: collapse;
  margin: 0 0 var(--space-2);
  inline-size: 100%;
}

.markdown th,
.markdown td {
  border: 1px solid var(--line);
  padding: var(--space-1) var(--space-2);
  text-align: start;
}

.markdown th {
  background: var(--surface-0);
  font-weight: 600;
}

.markdown hr {
  border: none;
  border-block-start: 1px solid var(--line);
  margin: var(--space-3) 0;
}

.markdown ul,
.markdown ol {
  margin: 0 0 var(--space-2);
  padding-inline-start: var(--space-5);
}
</style>
