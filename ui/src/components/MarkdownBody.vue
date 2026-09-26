<script setup lang="ts">
import { computed } from 'vue'

/**
 * A deliberately small Markdown renderer (ui.md §5.2): quotes, headings, lists, fenced code, tables and
 * inline code/emphasis — enough for the session log to read like a document.
 *
 * It renders through Vue's text interpolation, never `v-html`, so a body containing HTML is shown as text.
 * That matters here: session bodies are model output, and this view can drive the desktop.
 */

type Inline =
  | { kind: 'text'; value: string }
  | { kind: 'code'; value: string }
  | { kind: 'strong'; value: string }
  | { kind: 'em'; value: string }

type Block =
  | { kind: 'paragraph'; lines: Inline[][] }
  | { kind: 'heading'; level: number; lines: Inline[][] }
  | { kind: 'quote'; lines: Inline[][] }
  | { kind: 'code'; text: string; language: string }
  | { kind: 'list'; ordered: boolean; items: Inline[][] }
  | { kind: 'table'; header: string[]; rows: string[][] }
  | { kind: 'rule' }

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
      runs.push({ kind: 'code', value: code })
    } else if (strong !== undefined || strongAlt !== undefined) {
      runs.push({ kind: 'strong', value: strong ?? strongAlt ?? '' })
    } else {
      runs.push({ kind: 'em', value: em ?? emAlt ?? '' })
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
  const lines = text.split('\n')
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
      if (/^(```|#{1,6}\s|>|\s*[-*+]\s|\s*\d+[.)]\s)/.test(next) || /^(---|\*\*\*|___)$/.test(next.trim())) {
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
          <template v-for="(run, runIndex) in line" :key="runIndex">
            <code v-if="run.kind === 'code'">{{ run.value }}</code>
            <strong v-else-if="run.kind === 'strong'">{{ run.value }}</strong>
            <em v-else-if="run.kind === 'em'">{{ run.value }}</em>
            <template v-else>{{ run.value }}</template>
          </template>
        </p>
      </blockquote>

      <ol v-else-if="block.kind === 'list' && block.ordered">
        <li v-for="(item, itemIndex) in block.items" :key="itemIndex">
          <template v-for="(run, runIndex) in item" :key="runIndex">
            <code v-if="run.kind === 'code'">{{ run.value }}</code>
            <strong v-else-if="run.kind === 'strong'">{{ run.value }}</strong>
            <em v-else-if="run.kind === 'em'">{{ run.value }}</em>
            <template v-else>{{ run.value }}</template>
          </template>
        </li>
      </ol>

      <ul v-else-if="block.kind === 'list'">
        <li v-for="(item, itemIndex) in block.items" :key="itemIndex">
          <template v-for="(run, runIndex) in item" :key="runIndex">
            <code v-if="run.kind === 'code'">{{ run.value }}</code>
            <strong v-else-if="run.kind === 'strong'">{{ run.value }}</strong>
            <em v-else-if="run.kind === 'em'">{{ run.value }}</em>
            <template v-else>{{ run.value }}</template>
          </template>
        </li>
      </ul>

      <component v-else-if="block.kind === 'heading'" :is="`h${Math.min(block.level + 2, 6)}`">
        <template v-for="(run, runIndex) in block.lines[0] ?? []" :key="runIndex">
          <code v-if="run.kind === 'code'">{{ run.value }}</code>
          <strong v-else-if="run.kind === 'strong'">{{ run.value }}</strong>
          <em v-else-if="run.kind === 'em'">{{ run.value }}</em>
          <template v-else>{{ run.value }}</template>
        </template>
      </component>

      <p v-else v-for="(line, lineIndex) in block.kind === 'paragraph' ? block.lines : []" :key="lineIndex">
        <template v-for="(run, runIndex) in line" :key="runIndex">
          <code v-if="run.kind === 'code'">{{ run.value }}</code>
          <strong v-else-if="run.kind === 'strong'">{{ run.value }}</strong>
          <em v-else-if="run.kind === 'em'">{{ run.value }}</em>
          <template v-else>{{ run.value }}</template>
        </template>
      </p>
    </template>
  </div>
</template>

<style scoped>
.markdown {
  overflow-wrap: anywhere;
}

.markdown p {
  margin: 0 0 var(--space-2);
}

.markdown blockquote {
  margin: 0 0 var(--space-2);
  padding: var(--space-1) var(--space-3);
  border-inline-start: 0.2em solid var(--line);
  border-radius: var(--radius-sm);
  background: var(--bg);
  color: var(--muted);
}

.markdown blockquote p:last-child {
  margin-block-end: 0;
}

.markdown code {
  padding: 0.1em 0.35em;
  border-radius: var(--radius-sm);
  background: var(--bg);
  border: 1px solid var(--line);
  font-family: 'Cascadia Mono', Consolas, monospace;
  font-size: 0.92em;
}

.markdown pre {
  margin: 0 0 var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  background: var(--bg);
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
  background: var(--bg);
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
