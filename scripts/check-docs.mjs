/**
 * Doc reference check.
 *
 *   node scripts/check-docs.mjs
 *
 * The repo pins its design docs by path and section from inside source comments — `ui.md §2` appears in
 * `.vue` files and `README.md` alike. That is deliberate: a comment that names the rule it obeys
 * outlives the person who wrote it. But nothing kept those pins honest, and they rot two ways:
 *
 *   1. the referenced file stops existing (a doc is deleted, moved, or was never created);
 *   2. the section number stops existing (the doc is rewritten and renumbered).
 *
 * Both fail silently. A comment citing `ui.md §14` reads exactly like one citing `ui.md §8`, and the reader
 * has no way to tell. This is what surfaced the missing `ui.md` and the dangling
 * `agent-harness-architecture-proposal.md` on 2026-10-01.
 *
 * What it deliberately does NOT check:
 *   - a name qualified by a path (`D:/code/Clone/x.md`, `docs/x.md`) — that is external, not a repo file;
 *   - the names in EXEMPT below, each with the reason it is exempt;
 *   - this file, whose own doc comment quotes broken names as examples.
 *
 * Exits non-zero if anything is left.
 */

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative, sep } from 'node:path'

const ROOT = join(import.meta.dirname, '..')
const SELF = 'scripts/check-docs.mjs'

/** Directories never scanned: build output, dependencies, VCS, IDE state. */
const SKIP_DIRS = new Set(['.git', '.idea', 'node_modules', 'dist', 'bin', 'obj', 'target', 'TestResults'])

/** Extensions that can carry a reference. Source comments are the point, so .ts/.vue/.css all count. */
const SCAN_EXTENSIONS = new Set(['.md', '.ts', '.vue', '.css', '.cs', '.json', '.mjs', '.sh'])

/**
 * Names that look like repo docs but are not, or are not in the repo.
 *
 * Each entry states why. An allowlist with reasons is reviewable; a heuristic that happens to pass today is
 * not — which is how the first version of this script ended up reporting 20 false `SKILL.md`s.
 */
const EXEMPT = new Map([
  ['SKILL.md', 'a workspace artifact read at runtime: <workspace>/skills/<id>/SKILL.md, never a repo file'],
  // Upload fixtures: file names the skill-upload tests send, not documents anyone reads.
  ['my-skill.md', 'skill-upload fixture name in tests/Partner.Api.Tests/SkillEndpointsTests.cs'],
  ['bad.md', 'skill-upload fixture name in SkillEndpointsTests.cs and ui/tests/views.test.ts'],
  ['taken.md', 'skill-upload fixture name in tests/Partner.Api.Tests/SkillEndpointsTests.cs'],
  ['more.md', 'skill-upload fixture name in ui/tests/views.test.ts'],
])

/** Heading shapes a numbered section can take: `## 8.` / `### 5.1` — and the circled region markers. */
const HEADING = /^#{2,3}\s+(\d+)(?:\.(\d+))?/

/** A reference: an optional backtick, a `.md` filename, an optional `§N`. */
const REFERENCE = /`?([\w.-]+\.md)`?(?:\s*§\s*(\d+(?:\.\d+)*))?/g

/** Section numbers a document actually defines, as "1", "5.2", "12". */
function sectionsOf(markdown) {
  const out = new Set()
  for (const line of markdown.split('\n')) {
    const match = HEADING.exec(line)
    if (match?.[1] !== undefined) {
      out.add(match[2] !== undefined ? `${match[1]}.${match[2]}` : match[1])
    }
  }
  return out
}

function walk(dir) {
  const found = []
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) found.push(...walk(full))
    else if (SCAN_EXTENSIONS.has(full.slice(full.lastIndexOf('.')))) found.push(full)
  }
  return found
}

const problems = []
const caches = new Map()
const read = (file) => {
  if (!caches.has(file)) caches.set(file, readFileSync(file, 'utf8'))
  return caches.get(file)
}

const files = walk(ROOT)
  .map((full) => relative(ROOT, full).split(sep).join('/'))
  .filter((file) => file !== SELF)

for (const file of files) {
  const lines = read(join(ROOT, file)).split('\n')

  lines.forEach((line, index) => {
    for (const match of line.matchAll(REFERENCE)) {
      const [whole, name, section] = match
      if (EXEMPT.has(name)) continue

      // A name qualified by a path is external. `D:/code/Clone/x.md` and `docs/x.md` are not repo files,
      // and this script has no business asserting anything about what lives outside the tree.
      const before = line.slice(0, match.index)
      if (/[\\/]\s*$/.test(before) || /[A-Za-z]:$/.test(before)) continue

      if (!existsSync(join(ROOT, name))) {
        problems.push(`${file}:${index + 1}  ${name} does not exist  — ${whole}`)
        continue
      }

      if (section === undefined) continue
      if (!name.endsWith('.md')) continue

      // Citing the parent of a section that exists is not a broken pin: `ui.md §5` is satisfied by `§5.2`.
      const declared = sectionsOf(read(join(ROOT, name)))
      const satisfied =
        declared.has(section) || [...declared].some((candidate) => candidate.startsWith(`${section}.`))
      if (!satisfied) {
        problems.push(`${file}:${index + 1}  ${name} §${section} is not a section that document defines`)
      }
    }
  })
}

console.log(`== doc references: ${files.length} files scanned ==`)

if (problems.length > 0) {
  for (const problem of problems) console.error(`  ${problem}`)
  console.error(`\n${problems.length} broken document reference(s).`)
  process.exit(1)
}

console.log(`doc references OK (${EXEMPT.size} exempt names)`)
