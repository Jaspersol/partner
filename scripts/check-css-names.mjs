/**
 * Scoped/global class collision check.
 *
 *   node scripts/check-css-names.mjs
 *
 * Vue's scoped styles are additive, not isolating. A component's `<style scoped>` rule only wins on the
 * properties it actually declares — everything else on that selector still comes from the unscoped
 * stylesheet. So a component that reuses a class name the global sheet already gives a *box model* silently
 * inherits that box model, and the result is a component laid out as something else entirely.
 *
 * It cost two bugs on 2026-10-01, both found by looking at the screen rather than by reading the diff:
 *
 *   - `ThinkingRow`'s `.body` picked up the shell's `.body { display: grid; grid-template-columns:
 *     minmax(0, var(--sidebar)) minmax(0, 1fr) }`, so every line of an expanded thought wrapped at the
 *     sidebar's width. The scoped rule set colour and font-size and stopped there.
 *   - `McpView`'s `.field` picked up the settings form's `.field { grid-template-columns: minmax(0,1fr) auto }`,
 *     putting a checkbox and its label in two columns it never asked for.
 *
 * Neither is visible in a component test: happy-dom has no layout engine, so both look correct until the app
 * is rendered. `scripts/e2e-shell.mjs` catches the symptom on a real WebView2; this catches the cause.
 *
 * What counts as a collision:
 *   - a `<style scoped>` rule names a class that the unscoped sheet also names, in a DIFFERENT file, and
 *   - the global rule for that class declares a layout or box property, and
 *   - the scoped rule does not declare that same property.
 *
 * The last condition is what keeps this quiet: a component styling its own root element (`.card`, `.split`,
 * `.composer`) is normal and fine, because it is the same element the global rule was written for. The bug is
 * always the same class name meaning two different elements.
 *
 * Exits non-zero if any real collision survives.
 */

import { readdirSync, readFileSync, readFileSync as read, statSync } from 'node:fs'
import { basename, extname, join } from 'node:path'

const ROOT = join(import.meta.dirname, '..', 'ui')
const SRC = join(ROOT, 'src')

/** Properties that decide what a box *is*. Colour and type do not; these do. */
const BOX_PROPERTIES = [
  'display',
  'position',
  'grid-template',
  'grid-template-columns',
  'grid-template-rows',
  'grid',
  'flex',
  'flex-direction',
  'flex-basis',
  'align-items',
  'justify-content',
  'inline-size',
  'block-size',
  'width',
  'height',
  'min-inline-size',
  'min-block-size',
  'max-inline-size',
  'max-block-size',
  'overflow',
  'overflow-x',
  'overflow-y',
  'container-type',
  'container-name',
  'padding',
  'padding-block',
  'margin',
]

/**
 * Class names that are *meant* to be shared with the global sheet. Each one states why, because a list of
 * bare names is a list nobody can review six months from now.
 *
 * These fall into two kinds: shared utilities the whole app is built on (`.row`, `.spacer`), and the root
 * element of the component that owns the global rule in the first place (`.card`, `.split`, `.composer`).
 */
const SHARED_BY_DESIGN = new Map([
  ['row', '共享工具类：styles.css 定的是 flex，组件用 <div class="row"> 就是为了拿这个 flex'],
  ['spacer', '共享工具类：工具条里的弹性占位'],
  ['meta', '共享工具类：元信息行的字号与颜色'],
  ['muted', '共享工具类：次级文字'],
  ['error', '共享工具类：错误色'],
  ['edited', '共享工具类：已编辑标记'],
  ['validity', '共享工具类：记忆有效性行'],
  ['sr-only', '共享工具类：只给读屏的节点'],
  ['break', '共享工具类：长 id 换行'],
  ['notice', '共享工具类：结果行'],
  ['empty', '共享工具类：空态'],
  ['list', '共享工具类：三种列表共用'],
  ['toolbar', '共享工具类：视图工具条'],
  ['card', '拥有它的就是全局那条规则'],
  ['split', '拥有它的就是全局那条规则'],
  ['scroll', '拥有它的就是全局那条规则'],
  ['composer', '拥有它的就是全局那条规则'],
  ['composer-strip', '拥有它的就是全局那条规则'],
  ['composer-input', '拥有它的就是全局那条规则'],
  ['composer-tools', '拥有它的就是全局那条规则'],
  ['nav', '拥有它的就是全局那条规则'],
  ['inspector', '拥有它的就是全局那条规则'],
  ['inspector-head', '拥有它的就是全局那条规则'],
  ['inspector-body', '拥有它的就是全局那条规则'],
  ['inspector-actions', '拥有它的就是全局那条规则'],
  ['markdown', 'styles.css 的 `--measure` 规则就是靠这个类给事件正文限行宽的'],
])

/** `class="a b"` and `:class` bindings are both fine; only the literal class list matters here. */
const CLASS_IN_MARKUP = /class="([^"]*)"/g

function walk(dir) {
  const out = []
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) out.push(...walk(full))
    else if (['.vue', '.css'].includes(extname(full))) out.push(full)
  }
  return out
}

/**
 * Rules that apply unconditionally.
 *
 * Rules inside `@media` / `@container` are deliberately skipped. `.sidebar .label { display: none }` under a
 * narrow-container query does collide by name, but it only fires in one condition and the component's own
 * rule still governs the rest — so counting it would bury the real bugs under the conditional ones. The
 * collision that matters is the one that applies in every state.
 */
function unconditionalRules(css) {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '')
  const atRules = new Set()
  for (const m of withoutComments.matchAll(/@(?:media|container|supports)[^{]*\{/g)) atRules.add(m.index)

  // Everything an at-rule block spans, so its contents can be removed before the top level is parsed.
  let topLevel = withoutComments
  for (const start of [...atRules].sort((a, b) => b - a)) {
    let depth = 0
    let i = start
    while (i < topLevel.length) {
      if (topLevel[i] === '{') depth++
      else if (topLevel[i] === '}') {
        depth--
        if (depth === 0) break
      }
      i++
    }
    topLevel = topLevel.slice(0, start) + topLevel.slice(i + 1)
  }

  const out = new Map()
  for (const m of topLevel.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const declarations = new Set()
    for (const d of m[2].split(';')) {
      const prop = d.split(':')[0]?.trim()
      if (prop) declarations.add(prop)
    }
    for (const sel of m[1].split(',')) {
      for (const cls of sel.matchAll(/\.([a-zA-Z][\w-]*)/g)) {
        if (!out.has(cls[1])) out.set(cls[1], { declarations: new Set(), selectors: new Set() })
        const entry = out.get(cls[1])
        for (const prop of declarations) entry.declarations.add(prop)
        entry.selectors.add(sel.trim())
      }
    }
  }
  return out
}

/** Same parse, but for a component's scoped block — at-rules there are its own business. */
function scopedRules(css) {
  return unconditionalRules(css)
}

const files = walk(SRC)
const globalSheet = read(join(SRC, 'styles.css'), 'utf8')
const globalRules = unconditionalRules(globalSheet)

const problems = []

for (const file of files.filter((f) => extname(f) === '.vue')) {
  const source = read(file, 'utf8')
  const scopedBlock = /<style scoped>([\s\S]*?)<\/style>/.exec(source)
  if (!scopedBlock) continue

  const componentRules = scopedRules(scopedBlock[1])
  const name = file.slice(SRC.length + 1)

  // Which of these class names does this component actually put in its own markup?
  const used = new Set()
  const template = source.slice(0, source.indexOf('<style'))
  for (const m of template.matchAll(CLASS_IN_MARKUP)) {
    for (const cls of m[1].split(/\s+/)) if (cls) used.add(cls)
  }

  for (const [cls, entry] of componentRules) {
    const global = globalRules.get(cls)
    if (!global) continue
    if (!used.has(cls)) continue
    if (SHARED_BY_DESIGN.has(cls)) continue

    // The bug: a box property the global sets and the scoped rule never mentions.
    const inherited = [...global.declarations].filter(
      (prop) => BOX_PROPERTIES.includes(prop) && !entry.declarations.has(prop),
    )
    if (inherited.length === 0) continue

    problems.push(
      `${name}: scoped .${cls} inherits ${inherited.join(', ')} from the global .${cls} ` +
        `(${[...global.selectors].slice(0, 2).join(' | ')})`,
    )
  }
}

console.log(`== css class names: ${files.filter((f) => extname(f) === '.vue').length} components scanned ==`)

if (problems.length > 0) {
  for (const problem of problems) console.error(`  ${problem}`)
  console.error(
    `\n${problems.length} scoped class(es) inheriting a box model from the global sheet.\n` +
      `Rename the component's class so it means one thing, or declare the properties you mean.`,
  )
  process.exit(1)
}

console.log(
  `css class names OK (${globalRules.size} global classes, ${SHARED_BY_DESIGN.size} shared-by-design exemptions)`,
)
