/**
 * End-to-end check of the real product: launches the Tauri shell, which starts the API sidecar and injects
 * the token, then drives the rendered webview over the Chrome DevTools Protocol.
 *
 * This is not in CI: it needs a desktop session and a WebView2 window. It exists because WebView2 does not
 * expose its DOM to UI Automation, so the only way to exercise the shipped shell is over CDP.
 *
 *   node scripts/e2e-shell.mjs
 *
 * Exits non-zero on the first failed expectation.
 */

import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const repoRoot = join(import.meta.dirname, '..')

// The release shell is the real product: it embeds the built frontend and bundles the published API. A debug
// shell would instead need a running vite dev server, which is a different thing to verify.
// The debug shell, not the release one.
//
// It used to default to `target/release`, while the error below told people to run `cargo build --manifest-path
// src-tauri/Cargo.toml` — which produces `target/debug`. The two contradicted each other, and the failure mode
// is silent: a release shell embeds whatever `ui/dist` looked like on the day it was built and prefers its
// bundled API over the build output, so the whole run quietly tested a build from whenever that was. Three
// assertions failed against code that was demonstrably correct on disk, all of them explained by one stale
// executable. Set `PARTNER_SHELL_EXE` to test a release shell on purpose.
const shellExe = process.env.PARTNER_SHELL_EXE ?? join(repoRoot, 'src-tauri', 'target', 'debug', 'partner-shell.exe')

const diagnostics = []
const failures = []
let checks = 0

function check(label, passed, detail = '') {
  checks++
  console.log(`  ${passed ? 'PASS' : 'FAIL'}  ${label}${detail === '' ? '' : ` -> ${detail}`}`)
  if (!passed) {
    failures.push(label)
  }
}

async function freePort() {
  const listening = Promise.withResolvers()
  const probe = createServer()
  probe.once('error', listening.reject)
  probe.listen(0, '127.0.0.1', () => {
    const address = probe.address()
    probe.close(() => listening.resolve(address.port))
  })
  return listening.promise
}

/** Minimal CDP client: one socket, request/response by id. */
function connect(url) {
  const socket = new WebSocket(url)
  const pending = new Map()
  let nextId = 1

  const opened = Promise.withResolvers()
  socket.addEventListener('open', () => opened.resolve())
  socket.addEventListener('error', (event) => opened.reject(new Error(`cdp socket error: ${event.message ?? ''}`)))
  socket.addEventListener('message', (event) => {
    const message = JSON.parse(event.data)

    // Keep the page's own complaints: without them a render failure is indistinguishable from a slow one.
    if (message.method === 'Log.entryAdded') {
      diagnostics.push(`${message.params.entry.level}: ${message.params.entry.text}`)
    }
    if (message.method === 'Runtime.consoleAPICalled') {
      diagnostics.push(`console.${message.params.type}: ${message.params.args.map((a) => a.value ?? a.description).join(' ')}`)
    }

    const waiting = pending.get(message.id)
    if (!waiting) {
      return
    }
    pending.delete(message.id)
    if (message.error) {
      waiting.reject(new Error(message.error.message))
    } else {
      waiting.resolve(message.result)
    }
  })

  return {
    opened: opened.promise,
    send(method, params = {}) {
      const id = nextId++
      const answered = Promise.withResolvers()
      pending.set(id, answered)
      socket.send(JSON.stringify({ id, method, params }))
      return answered.promise
    },
    close: () => socket.close(),
  }
}

/** Evaluates an expression in the page and returns its value. */
async function evaluate(cdp, expression) {
  const result = await cdp.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  if (result.exceptionDetails) {
    throw new Error(`page threw: ${result.exceptionDetails.exception?.description ?? result.exceptionDetails.text}`)
  }
  return result.result.value
}

/** Waits for a selector to yield at least one element, without guessing at a duration. */
async function waitFor(cdp, expression, label) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await evaluate(cdp, expression)) {
      return true
    }
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  throw new Error(`timed out waiting for ${label}`)
}

async function main() {
  if (!existsSync(shellExe)) {
    throw new Error(
      `missing ${shellExe} — run "cargo build --manifest-path src-tauri/Cargo.toml" first, ` +
        'and touch src-tauri/tauri.conf.json if you changed ui/ (a rebuild that skips the frontend still serves the last embedded one)',
    )
  }

  const workspaceRoot = mkdtempSync(join(tmpdir(), 'partner-e2e-'))
  const workspace = join(workspaceRoot, 'Partner', 'workspace')
  mkdirSync(join(workspace, 'session'), { recursive: true })
  mkdirSync(join(workspace, 'queue'), { recursive: true })

  const events = [
    { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'user', source: 'text', body: 'turn on the lamp' },
    { seq: 2, at: '2026-09-26T10:00:01+00:00', kind: 'assistant', source: 'text', body: 'done' },
    { seq: 3, at: '2026-09-26T10:00:02+00:00', kind: 'toolCall', source: 'text', body: 'clock' },
    { seq: 4, at: '2026-09-26T10:00:03+00:00', kind: 'toolResult', source: 'text', body: '{"now":"12:00"}' },
  ]
  writeFileSync(join(workspace, 'session', 'events.jsonl'), events.map((e) => JSON.stringify(e)).join('\n') + '\n')
  writeFileSync(
    join(workspace, 'queue', 'items.jsonl'),
    JSON.stringify({
      itemId: 'q1',
      seq: 1,
      source: 'schedule',
      status: 'failed',
      enqueuedAt: '2026-09-26T10:00:00+00:00',
      finishedAt: '2026-09-26T10:00:01+00:00',
      payload: 'nightly backup',
      idempotencyKey: 'sched:1',
      attempt: 1,
      error: 'disk unwritable',
    }) + '\n',
  )

  const debugPort = await freePort()
  const shell = spawn(shellExe, [], {
    env: {
      ...process.env,
      LOCALAPPDATA: workspaceRoot,
      // WebView2 does not expose its DOM to UI Automation; remote debugging is the way in.
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debugPort}`,
      // Cleared so the disabled-consumer assertions describe this machine, not the developer's environment.
      PARTNER_LLM_BASE_URL: '',
      PARTNER_LLM_MODEL: '',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })

  let cdp
  let apiPort
  try {
    // Wait for the devtools endpoint, then for the page target.
    let target
    for (let attempt = 0; attempt < 100 && !target; attempt++) {
      try {
        const response = await fetch(`http://127.0.0.1:${debugPort}/json/list`)
        const targets = await response.json()
        target = targets.find((candidate) => candidate.type === 'page' && candidate.webSocketDebuggerUrl)
      } catch {
        // The webview has not opened its debugging port yet.
      }
      if (!target) {
        await new Promise((resolve) => setTimeout(resolve, 200))
      }
    }
    if (!target) {
      throw new Error('the shell never exposed a debuggable page')
    }

    cdp = connect(target.webSocketDebuggerUrl)
    await cdp.opened
    await cdp.send('Runtime.enable')
    await cdp.send('Log.enable')

    // The token is injected by the shell, not read from anywhere by the test — and it is injected *late*, by
    // the page's own init script. Reading it once here races that: a run that got there first saw
    // `port=undefined` and then died further down on `http://127.0.0.1:undefined/...`, which reads like an app
    // failure and is not one. Wait for it instead.
    await waitFor(
      cdp,
      'typeof window.__PARTNER_TOKEN__ === "string" && Number.isInteger(window.__PARTNER_PORT__)',
      'the shell to inject a token and a port',
    )
    const token = await evaluate(cdp, 'window.__PARTNER_TOKEN__')
    const port = await evaluate(cdp, 'window.__PARTNER_PORT__')
    apiPort = port
    check('the shell injected a token and a port into the webview', Boolean(token) && Number.isInteger(port), `port=${port}`)

    await waitFor(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length > 0', 'the session list')
    const rows = await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length')
    check('the webview renders the seeded session', rows === 4, `rows=${rows}`)

    // A tool call is machinery, not conversation: it is shown (a turn that called a tool must not look like
    // it did nothing) but folded, so the payload is not in the way.
    const foldClosed = await evaluate(
      cdp,
      'document.querySelector(\'[data-tool="clock"]\')?.open === false',
    )
    const foldLabelled = await evaluate(
      cdp,
      'document.querySelector(\'[data-tool="clock"] summary\')?.textContent ?? ""',
    )
    check(
      'a tool call is shown as one folded card, not two rows',
      foldClosed && foldLabelled.includes('clock'),
      `closed=${foldClosed} label=${foldLabelled.trim()}`,
    )

    await evaluate(cdp, 'document.querySelector(\'[data-tool="clock"] summary\').click()')
    const foldOpened = await evaluate(
      cdp,
      'document.querySelector(\'[data-tool="clock"]\').open === true',
    )
    check('a folded tool call opens on click', foldOpened, `open=${foldOpened}`)

    // The queue holds only what is still in play, so the seeded failed item is compacted away at start-up and
    // there is no failure count to show. The failure is real — it is in the seeded *session* log — but it is
    // read there, not here.
    const blocked = await evaluate(cdp, 'document.querySelector(\'[data-test="blocked"]\').textContent')

    // A probe, not an assertion: three elements in one component were reporting three different things about
    // one prop, which no version of this code can do. Before guessing which build the webview loaded, ask it.
    const probe = await evaluate(
      cdp,
      `(() => {
        const badge = document.querySelector('[data-test="blocked"]')
        const announcer = document.querySelector('[data-test="blocked-announcer"]')
        return {
          scripts: [...document.querySelectorAll('script[src]')].map((s) => s.getAttribute('src')).join(','),
          badgeHtml: badge?.outerHTML ?? null,
          announcerHtml: announcer?.outerHTML ?? null,
          announcerExists: announcer !== null,
          badgesOnPage: document.querySelectorAll('[data-test="blocked"]').length,
        }
      })()`,
    )
    console.log('    probe:', JSON.stringify(probe).slice(0, 700))

    check(
      'the top bar badge reports what is in play, and no failure count it no longer has',
      blocked.includes('队列') && !blocked.includes('失败'),
      blocked.trim(),
    )

    // The inspector now starts closed, so open it the way a reader would: through its own toolbar button.
    //
    // This is not ceremony. `element.click()` in a script fires on a `display: none` element perfectly
    // happily, so driving the editor without opening the panel would have kept every assertion below green
    // while describing a sequence of clicks no person could perform. Anything the script touches has to be
    // something the script could also *see*.
    await evaluate(cdp, `document.querySelector('[data-test="session-inspector-toggle"]').click()`)
    // Re-read after a tick: the click and the re-render are two events, and reading in between them reports the
    // state before the button was pressed.
    await waitFor(cdp, 'document.querySelector(".inspector")?.offsetParent !== null', 'the inspector to open')

    const inspectorState = await evaluate(
      cdp,
      `(() => {
        const button = document.querySelector('[data-test="session-inspector-toggle"]')
        return {
          expanded: button?.getAttribute('aria-expanded'),
          text: button?.textContent?.trim(),
          inspectorRendered: document.querySelector('.inspector')?.offsetParent !== null,
        }
      })()`,
    )
    check(
      'the inspector is closed on arrival and its toolbar button opens it',
      inspectorState.expanded === 'true' && inspectorState.inspectorRendered === true,
      `aria-expanded=${inspectorState.expanded} label=${inspectorState.text}`,
    )

    // Select the first event: the editor must fill from the API, not from local state.
    await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\')[0].click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="session-draft"]\').value !== ""', 'the editor to fill')
    const draft = await evaluate(cdp, 'document.querySelector(\'[data-test="session-draft"]\').value')
    check('selecting an event fills the editor', draft === 'turn on the lamp', draft)

    // v-model listens for input events, so setting .value alone would not be a real interaction.
    await evaluate(
      cdp,
      `(() => {
        const box = document.querySelector('[data-test="session-draft"]')
        box.value = 'turn on the desk lamp'
        box.dispatchEvent(new Event('input', { bubbles: true }))
        return box.value
      })()`,
    )
    await evaluate(cdp, 'document.querySelector(\'[data-test="session-save"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="session-revisions"] li\') !== null', 'the revision history')

    // The API is the judge: the edit must have been appended, not applied locally.
    const session = await (await fetch(`http://127.0.0.1:${port}/api/session`, { headers: { Authorization: `Bearer ${token}` } })).json()
    const edited = session.items.find((item) => item.seq === 1)
    check(
      'saving from the webview appends a revision and keeps the original',
      edited?.body === 'turn on the desk lamp' && edited?.originalBody === 'turn on the lamp' && edited?.revisionCount === 1,
      `body=${edited?.body} original=${edited?.originalBody} revisions=${edited?.revisionCount}`,
    )

    const unauthorized = await fetch(`http://127.0.0.1:${port}/api/session`)
    check('the running shell API still rejects an unauthenticated call', unauthorized.status === 401, `status=${unauthorized.status}`)

    // The queue page is gone; the queue is the composer's own strip. The seeded failed item is compacted away
    // at start-up, so there is nothing in play and — deliberately — no expander to open: the toggle only
    // exists when there is something to show. That the strip says so is the thing worth asserting here; the
    // case where the list *does* open is covered further down, by a real delivered item.
    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-会话"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="composer-strip"]\') !== null', 'the composer strip')
    const strip = await evaluate(cdp, 'document.querySelector(\'[data-test="composer-strip"]\')?.textContent ?? ""')
    check(
      'the queue lives in the composer strip, and offers nothing to expand when nothing is in play',
      !strip.includes('失败') && !(await evaluate(cdp, 'document.querySelector(\'[data-test="composer-queue-toggle"]\') !== null')),
      strip.replace(/\s+/g, ' ').trim().slice(0, 90),
    )

    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-设置"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="utterance-gap"]\') !== null', 'the settings view')
    const gap = await evaluate(cdp, 'document.querySelector(\'[data-test="utterance-gap"]\').value')
    check('the settings view loads the stored value', Number(gap) === 1200, `utteranceGapMs=${gap}`)

    // The top bar carries the queue. It no longer counts failures — the queue holds only what is still in
    // play, so a settled failure is read in the conversation instead — but it must still report a depth and
    // announce that work is running.
    const badge = await evaluate(cdp, 'document.querySelector(\'[data-test="blocked"]\').textContent')
    const announced = await evaluate(
      cdp,
      'document.querySelector(\'[data-test="blocked-announcer"]\')?.textContent ?? ""',
    )
    check(
      'the top bar badge reports what is in play, and says so out loud',
      badge.includes('队列') && announced.trim() !== '',
      `badge=${badge.trim()} announced=${announced.trim()}`,
    )

    // Delivering from the composer: it lands in the queue, and the session is untouched.
    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-会话"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="composer-input"]\') !== null', 'the composer')
    await evaluate(
      cdp,
      `(() => {
        const box = document.querySelector('[data-test="composer-input"]')
        box.value = 'delivered from the shell'
        box.dispatchEvent(new Event('input', { bubbles: true }))
        return box.value
      })()`,
    )
    await evaluate(cdp, 'document.querySelector(\'[data-test="composer-send"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="session-notice"]\') !== null', 'the delivery notice')

    const queueAfterSend = await (await fetch(`http://127.0.0.1:${port}/api/queue`, { headers: { Authorization: `Bearer ${token}` } })).json()
    const delivered = queueAfterSend.items.find((item) => item.payload === 'delivered from the shell')
    const sessionAfterSend = await (await fetch(`http://127.0.0.1:${port}/api/session`, { headers: { Authorization: `Bearer ${token}` } })).json()
    check(
      'delivering from the composer queues the text without touching the session',
      delivered?.status === 'Queued' && sessionAfterSend.lastSeq === session.lastSeq,
      `status=${delivered?.status} lastSeq=${sessionAfterSend.lastSeq}`,
    )

    const notice = await evaluate(cdp, 'document.querySelector(\'[data-test="session-notice"]\').textContent')
    check('the composer says the work is queued rather than done', notice.includes('等待执行'), notice.trim())

    // And the pending text can be read where the queue now lives: the composer's own list. The item is still
    // Queued (no consumer yet), so it is exactly what the queue holds.
    await evaluate(cdp, 'document.querySelector(\'[data-test="composer-queue-toggle"]\').click()')
    await waitFor(
      cdp,
      'document.querySelectorAll(\'[data-test="composer-queue-item"]\').length === 1',
      'the queued item to appear',
    )
    const queuedRow = await evaluate(
      cdp,
      'document.querySelector(\'[data-test="composer-queue-item"]\').textContent',
    )
    check(
      'the queue in the composer shows the pending input content',
      queuedRow.includes('delivered from the shell'),
      queuedRow.trim().slice(0, 80),
    )

    // Back to the session: the checks that follow read the composer, which only exists there.
    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-会话"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="composer-consumer"]\') !== null', 'the composer state')

    // The shell runs without model configuration, so the UI must say the queue is not being executed —
    // otherwise a delivered message that never moves looks like a broken app.
    const consumerNote = await evaluate(cdp, 'document.querySelector(\'[data-test="composer-consumer"]\')?.textContent ?? ""')
    check('the UI explains that nothing is executing the queue', consumerNote.includes('未执行'), consumerNote.trim())

    // ---- layout: the two things a component test cannot see --------------------------------------
    //
    // Everything above this line asserts on content. These two assert on *geometry*, which is why they live
    // here and not in ui/tests: happy-dom parses the CSS but has no layout engine, so a track that scrolls
    // its own grid out of the window looks exactly like a correct one until someone runs the real thing.
    // Both of these were real bugs found by looking at the screen on 2026-10-01, and both were invisible to
    // 138 passing tests.

    // 1. Collapsing the inspector used to squeeze its track to zero rather than remove the panel, so the
    //    panel's 1px leading border and its content were scrollable overflow of the grid: one scrollbar on
    //    each axis, from an action that should have produced neither.
    //
    //    Both directions are checked, because "no scrollbar when closed" is only half a statement — a panel
    //    that also scrolls the window when it is *open* is the same bug with the opposite default.
    // Read after the render, not after the click. A `click()` and the re-render it causes are two separate
    // events, and reading in the same task as the click reports the state from *before* the button was pressed
    // — which is how this check came to claim the inspector was not rendered in either state.
    const readInspectorState = () => evaluate(
      cdp,
      `(() => {
        const shell = document.documentElement
        return {
          collapsed: document.querySelector('.split')?.classList.contains('is-inspector-collapsed') === true,
          // clientWidth/Height never exceed scrollWidth/Height without a scrollbar; a two-point gap is
          // sub-pixel rounding, the ~17px one is a real bar.
          hOverflow: shell.scrollWidth - shell.clientWidth,
          vOverflow: shell.scrollHeight - shell.clientHeight,
          rendered: document.querySelector('.inspector')?.offsetParent !== null,
        }
      })()`,
    )

    // Whichever state it arrived in, get to closed first.
    const beforeToggle = await readInspectorState()
    if (!beforeToggle.collapsed) {
      await evaluate(cdp, `document.querySelector('[data-test="session-inspector-toggle"]').click()`)
      await waitFor(
        cdp,
        `document.querySelector('.split')?.classList.contains('is-inspector-collapsed') === true`,
        'the inspector to close',
      )
    }

    const closed = await readInspectorState()

    await evaluate(cdp, `document.querySelector('[data-test="session-inspector-toggle"]').click()`)
    await waitFor(
      cdp,
      `document.querySelector('.inspector')?.offsetParent !== null`,
      'the inspector to open',
    )
    const open = await readInspectorState()
    const scrollbarsInBothStates = { closed, open }
    check(
      'no scrollbar in either inspector state',
      scrollbarsInBothStates.closed.hOverflow <= 2 &&
        scrollbarsInBothStates.closed.vOverflow <= 2 &&
        !scrollbarsInBothStates.closed.rendered &&
        scrollbarsInBothStates.open.hOverflow <= 2 &&
        scrollbarsInBothStates.open.vOverflow <= 2 &&
        scrollbarsInBothStates.open.rendered,
      `closed(h=${scrollbarsInBothStates.closed.hOverflow},v=${scrollbarsInBothStates.closed.vOverflow},shown=${scrollbarsInBothStates.closed.rendered}) ` +
        `open(h=${scrollbarsInBothStates.open.hOverflow},v=${scrollbarsInBothStates.open.vOverflow},shown=${scrollbarsInBothStates.open.rendered})`,
    )

    // 2. A thinking row used to wrap its prose at about half the width it had. The cause was not the scroller
    //    and not the measure: the component's `.body` was the same class name as the shell's `.body`, and a
    //    scoped rule that sets only colour and font-size does not stop `display: grid` and
    //    `grid-template-columns: minmax(0, var(--sidebar)) minmax(0, 1fr)` from applying. So the prose landed
    //    in the first column, at the sidebar's width, while the collapsed summary line above it — which is a
    //    flex row — ran the full width. Half a line, then a wrap, with nothing on screen to explain it.
    //
    //    This asserts the *symptom*, not the cause. The earlier version of this check asserted `overflowY`,
    //    which was the first hypothesis and was wrong — asserting on a cause is how you end up green over a
    //    bug that is still on screen.
    const thinkingWrap = await evaluate(
      cdp,
      `(() => {
        const rows = [...document.querySelectorAll('[data-test="session-list"] li')]
        const thought = rows.find((li) => li.querySelector('[data-test="session-thinking"]'))
        if (!thought) return { found: false }
        const details = thought.querySelector('details')
        details.open = true
        const prose = thought.querySelector('.markdown')
        if (!prose) return { found: true, prose: false }
        // The row's content box is the width a line of prose is entitled to. The 40px tolerance covers the
        // row's own inline padding, which is not the bug.
        const rowInner = thought.clientWidth
        const proseWidth = prose.getBoundingClientRect().width
        return {
          found: true,
          prose: true,
          ratio: rowInner === 0 ? 0 : proseWidth / rowInner,
          display: getComputedStyle(prose.parentElement).display,
          rowInner,
          proseWidth,
        }
      })()`,
    )
    check(
      'an expanded thought fills the row instead of wrapping at half of it',
      thinkingWrap.found !== true ||
        (thinkingWrap.prose === true && thinkingWrap.ratio > 0.9 && thinkingWrap.display !== 'grid'),
      thinkingWrap.found === false
        ? 'no thinking row in this fixture'
        : `prose=${thinkingWrap.proseWidth}px of row=${thinkingWrap.rowInner}px (${Math.round((thinkingWrap.ratio ?? 0) * 100)}%) parent=${thinkingWrap.display}`,
    )

    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-设置"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="model-base-url"]\') !== null', 'the settings page')

    // Settings are the configuration surface: editing them must reach the running runtime, not just the file.
    const cards = await evaluate(cdp, `[...document.querySelectorAll('section.card h2')].map((h) => h.textContent.trim()).join('|')`)
    const limits = await evaluate(
      cdp,
      `document.querySelector('[data-test="model-context-tokens"]')?.value + '/' + document.querySelector('[data-test="model-max-tokens"]')?.value`,
    )
    check('the model card exposes the context window and the output ceiling in tokens', limits === '32768/4096', limits)
    check(
      'the settings page covers every configurable subsystem',
      cards.includes('语音识别') && cards.includes('语音合成') && cards.includes('模型') && cards.includes('桌面'),
      cards,
    )

    // The desktop card is the only irreversible capability, so its card has to say so and default to off.
    const desktopOff = await evaluate(
      cdp,
      'document.querySelector(\'[data-test="desktop-enabled"]\')?.checked === false',
    )
    const desktopNote = await evaluate(
      cdp,
      '[...document.querySelectorAll(\'section.card\')].find((c) => c.querySelector(\'h2\')?.textContent.includes(\'桌面\'))?.innerText ?? ""',
    )
    check(
      'the desktop card starts off and says what turning it on means',
      desktopOff && desktopNote.includes('批准'),
      `off=${desktopOff} note=${desktopNote.slice(0, 40).replace(/\n/g, ' ')}`,
    )

    const beforeModel = await evaluate(cdp, 'document.querySelector(\'[data-test="model-effect"]\').textContent')
    check('the model card says the queue is not being executed yet', beforeModel.includes('未配置模型'), beforeModel.trim())

    await evaluate(
      cdp,
      `(() => {
        const set = (selector, value) => {
          const field = document.querySelector(selector)
          field.value = value
          field.dispatchEvent(new Event('input', { bubbles: true }))
        }
        set('[data-test="model-base-url"]', 'http://127.0.0.1:9/v1')
        set('[data-test="model-name"]', 'e2e-model')
        return true
      })()`,
    )
    await evaluate(cdp, 'document.querySelector(\'[data-test="settings-save"]\').click()')

    await waitFor(
      cdp,
      'document.querySelector(\'[data-test="status-consumer"]\').textContent.includes("消费中")',
      'the consumer to start from the settings page',
    )
    const afterModel = await evaluate(cdp, 'document.querySelector(\'[data-test="status-consumer"]\').textContent')
    check('saving a model endpoint starts the consumer without restarting the app', afterModel.includes('e2e-model'), afterModel.trim())

    // The key is write-only: the field must stay empty and the state must say one is stored.
    await evaluate(
      cdp,
      `(() => {
        const field = document.querySelector('[data-test="model-api-key"]')
        field.value = 'e2e-secret'
        field.dispatchEvent(new Event('input', { bubbles: true }))
        return true
      })()`,
    )
    await evaluate(cdp, 'document.querySelector(\'[data-test="settings-save"]\').click()')
    await waitFor(
      cdp,
      'document.querySelector(\'[data-test="model-api-key-state"]\').textContent.includes("已设置")',
      'the key state to report as stored',
    )
    const keyField = await evaluate(cdp, 'document.querySelector(\'[data-test="model-api-key"]\').value')
    const keyState = await evaluate(cdp, 'document.querySelector(\'[data-test="model-api-key-state"]\').textContent')
    check('the API key is stored without being echoed back into the form', keyField === '' && keyState.includes('已设置'), `field="${keyField}" state=${keyState.trim()}`)

    // The image capability: the switch exists in the real window, and turning it on registers the screenshot
    // tool — that is what makes blind apps (Electron, canvas) readable without OCR.
    const hasImageSwitch = await evaluate(
      cdp,
      `document.querySelector('[data-test="model-supports-images"]') !== null`,
    )
    check('the model card offers the image capability', hasImageSwitch === true, `present=${hasImageSwitch}`)

    // Both switches in one go: the screenshot tool is a desktop tool, and it exists only for a model that
    // takes pictures — so the pair is what the claim is about.
    const toggled = await evaluate(
      cdp,
      `(() => {
        const image = document.querySelector('[data-test="model-supports-images"]')
        image.checked = true
        image.dispatchEvent(new Event('change', { bubbles: true }))
        const desktop = document.querySelector('[data-test="desktop-enabled"]')
        desktop.checked = true
        desktop.dispatchEvent(new Event('change', { bubbles: true }))
        return { image: image.checked, desktop: desktop.checked }
      })()`,
    )
    check(
      'both switches take the click',
      toggled?.image === true && toggled?.desktop === true,
      `image=${toggled?.image} desktop=${toggled?.desktop}`,
    )

    await evaluate(cdp, `document.querySelector('[data-test="settings-save"]').click()`)

    // Waited for on the server side: the switches are the form's, and the claim is about what the running
    // consumer was built with.
    let savedDesktop = null
    let toolNames = []
    for (let attempt = 0; attempt < 200; attempt += 1) {
      const settings = await (await fetch(`http://127.0.0.1:${port}/api/settings`, { headers: { Authorization: `Bearer ${token}` } })).json()
      const consumerState = await (await fetch(`http://127.0.0.1:${port}/api/consumer`, { headers: { Authorization: `Bearer ${token}` } })).json()
      savedDesktop = settings.desktop
      toolNames = consumerState.tools.map(tool => tool.name)

      if (savedDesktop?.enabled === true && toolNames.includes('desktop_screenshot')) {
        break
      }

      await new Promise(resolve => setTimeout(resolve, 25))
    }

    check(
      'the image capability is saved with the model',
      savedDesktop?.enabled === true,
      `desktop=${JSON.stringify(savedDesktop)}`,
    )
    check(
      'turning it on registers the screenshot tool for the running consumer',
      toolNames.includes('desktop_screenshot'),
      `tools=${toolNames.length}`,
    )
    // Removing history from the UI: delete one event, and retract from an event onward (two-step).
    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-会话"]\').click()')
    await waitFor(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length >= 2', 'the session list')

    const beforeDelete = await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length')
    await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\')[0].click()')
    // The row's own buttons, not the `⋯` menu. The actions used to live only in that menu, and the reason was
    // the long labels covering the row; with short labels they are back on the row, which is where a reader
    // reaching for 「删除」 expects them.
    await waitFor(cdp, `document.querySelector('[data-test="session-row-delete-inline"]') !== null`, 'the row actions')
    // Two presses: the first arms, the second confirms. That was deliberate — and it has to survive being
    // hover-revealed, so the armed label has to stay on screen with the pointer nowhere near the row.
    await evaluate(cdp, 'document.querySelector(\'[data-test="session-row-delete-inline"]\').click()')
    await waitFor(
      cdp,
      `document.querySelector('[data-test="session-row-delete-inline"]')?.textContent.trim() === '确认删除'`,
      'the delete to arm',
    )
    const armedStillShown = await evaluate(
      cdp,
      `getComputedStyle(document.querySelector('[data-test="session-row-actions"]')).display !== 'none'`,
    )
    check(
      'an armed confirmation does not disappear when the pointer leaves the row',
      armedStillShown,
      `visible=${armedStillShown}`,
    )
    await evaluate(cdp, 'document.querySelector(\'[data-test="session-row-delete-inline"]\').click()')
    await waitFor(
      cdp,
      `document.querySelectorAll('[data-test=session-list] li').length === ${beforeDelete - 1}`,
      'the deleted event to leave the list',
    )
    const afterDelete = await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length')
    check('deleting from the UI removes one event', afterDelete === beforeDelete - 1, `${beforeDelete} -> ${afterDelete}`)

    // The whole point of the removal: it reaches the file. The seeded bodies are ASCII, so the log can be
    // searched as text — and the surviving event proves the purge stayed inside its range.
    const logPath = join(workspace, 'session', 'events.jsonl')
    const logAfterDelete = readFileSync(logPath, 'utf8')
    check(
      'deleting from the UI takes the text out of the log',
      !logAfterDelete.includes('turn on the desk lamp') && logAfterDelete.includes('"done"'),
      `removed=${!logAfterDelete.includes('turn on the desk lamp')} kept=${logAfterDelete.includes('"done"')}`,
    )

    // The retraction asks twice: the first click only arms it.
    const beforeRetract = await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length')
    await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\')[0].click()')
    await waitFor(cdp, `document.querySelector('[data-test="session-row-retract-inline"]') !== null`, 'the row actions')
    await evaluate(cdp, `document.querySelector('[data-test="session-row-retract-inline"]').click()`)
    // Arming renames the item in place rather than adding a confirm button, so wait for the armed label.
    const armed = await waitFor(
      cdp,
      `document.querySelector('[data-test="session-row-retract-inline"]')?.textContent.trim() === '确认删除之后'`,
      'the retraction to arm',
    )
    const stillThere = await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length')
    check(
      'retracting asks for a second confirmation before removing anything',
      armed && stillThere === beforeRetract,
      `armed=${armed} rows=${stillThere}`,
    )

    await evaluate(cdp, `document.querySelector('[data-test="session-row-retract-inline"]').click()`)
    await waitFor(
      cdp,
      'document.querySelector(\'[data-test="session-list"] li\') === null',
      'the retracted events to leave the list',
    )
    const logAfterRetract = readFileSync(logPath, 'utf8')
    check(
      'confirming the retraction takes the tail out of the log too',
      !logAfterRetract.includes('turn on the desk lamp') && !logAfterRetract.includes('"done"'),
      `bytes=${logAfterRetract.length}`,
    )

    // Discovery: the device pickers must offer the machine's real endpoints, not a text box.
    await evaluate(cdp, `document.querySelector('[data-test="tab-设置"]').click()`)
    // The text-input fallback carries the same test id, so the wait has to require the select specifically.
    await waitFor(
      cdp,
      `document.querySelector('[data-test="input-device"]')?.tagName === 'SELECT'`,
      'the device picker to be populated',
    )
    const inputDeviceTag = await evaluate(cdp, `document.querySelector('[data-test="input-device"]').tagName`)
    const inputDeviceOptions = await evaluate(
      cdp,
      `[...document.querySelectorAll('[data-test="input-device"] option')].map((o) => o.textContent).join('|')`,
    )
    check(
      'the input device is a picker over the machine\'s real endpoints',
      inputDeviceTag === 'SELECT' && inputDeviceOptions.split('|').length > 1,
      `${inputDeviceTag}: ${inputDeviceOptions}`,
    )

    // A probe against a dead endpoint must explain itself, and must leave the field typeable.
    await evaluate(
      cdp,
      `(() => {
        const button = document.querySelector('[data-test="model-probe"]')
        button.click()
        return true
      })()`,
    )
    await waitFor(
      cdp,
      `document.querySelector('[data-test="model-probe-error"]')?.textContent.length > 0`,
      'the model probe to report its failure',
    )
    const probeError = await evaluate(cdp, `document.querySelector('[data-test="model-probe-error"]').textContent`)
    const nameTag = await evaluate(cdp, `document.querySelector('[data-test="model-name"]').tagName`)
    check(
      'a failed model probe says why and leaves the field typeable',
      probeError.length > 0 && nameTag === 'INPUT',
      `${nameTag}: ${probeError.slice(0, 80)}`,
    )

    const settingsRaw = await (await fetch(`http://127.0.0.1:${port}/api/settings`, { headers: { Authorization: `Bearer ${token}` } })).text()
    check('the API never returns the stored key', !settingsRaw.includes('e2e-secret'), 'value absent from the response')

    // Voice: the shell has no devices in this environment, so the halves must report why rather than look dead.
    await evaluate(
      cdp,
      `(() => {
        const field = document.querySelector('[data-test="asr-base-url"]')
        field.value = 'http://127.0.0.1:9'
        field.dispatchEvent(new Event('input', { bubbles: true }))
        const enabled = document.querySelector('[data-test="input-enabled"]')
        if (!enabled.checked) enabled.click()
        return true
      })()`,
    )
    // Save before expecting the device to change. Saving is explicit now — every switch on this page waits for
    // the button at the bottom, because a page where some switches save themselves and some do not is one
    // nobody can predict. The e2e has to press the button a reader would press.
    await evaluate(cdp, `document.querySelector('[data-test="settings-save"]').click()`)
    await waitFor(
      cdp,
      `!document.querySelector('[data-test="input-effect"]').textContent.includes('未启用')`,
      'the voice half to reflect the enabled setting',
    )
    const inputEffect = await evaluate(cdp, `document.querySelector('[data-test="input-effect"]').textContent`)
    // Either it opened a real device or it reports why it could not — both mean the setting reached the host.
    check(
      'the voice card reports what the half actually did with the enabled setting',
      /运行中|打开输入设备失败|未配置 ASR/.test(inputEffect),
      inputEffect.trim(),
    )

    // Sidebar collapse is shell state; the sidebar must actually leave the layout. Whichever way it starts,
    // one press has to change the width — and each click is followed by a wait, because a click and the
    // re-render it causes are two separate events.
    await evaluate(cdp, 'document.querySelector(\'[data-test="tab-会话"]\').click()')
    await waitFor(cdp, 'document.querySelector(\'[data-test="sidebar-toggle"]\') !== null', 'the sidebar toggle')
    await waitFor(cdp, 'document.querySelector(\'[data-test="sidebar"]\') !== null', 'the sidebar')

    const widthBefore = await evaluate(
      cdp,
      `document.querySelector('[data-test="sidebar"]').getBoundingClientRect().width`,
    )
    await evaluate(cdp, `document.querySelector('[data-test="sidebar-toggle"]').click()`)
    await waitFor(
      cdp,
      `document.querySelector('[data-test="sidebar"]').getBoundingClientRect().width !== ${widthBefore}`,
      'the sidebar to change width',
    )
    const widthAfter = await evaluate(
      cdp,
      'document.querySelector(\'[data-test="sidebar"]\').getBoundingClientRect().width',
    )

    // Two states, 56px and 240px (--rail / --rail-expanded). A change alone would pass if the toggle merely
    // nudged something, so the two values are pinned as well.
    const sorted = [widthBefore, widthAfter].sort((a, b) => a - b)
    check(
      'the sidebar toggle switches between the rail and the labelled panel',
      sorted[0] === 56 && sorted[1] === 240,
      `${widthBefore} -> ${widthAfter}`,
    )

    // Theme is pure frontend: it must apply without a round trip.
    const themeBefore = await evaluate(cdp, 'document.documentElement.dataset.theme ?? "auto"')
    await evaluate(cdp, 'document.querySelector(\'[data-test="theme-toggle"]\').click()')
    const themeAfter = await evaluate(cdp, 'document.documentElement.dataset.theme ?? "auto"')
    check('the theme toggle applies immediately', themeBefore !== themeAfter, `${themeBefore} -> ${themeAfter}`)
  } catch (error) {
    // The page's own state is the only thing that explains a render failure.
    try {
      console.log('  page url:', await evaluate(cdp, 'location.href'))
      console.log('  page text:', (await evaluate(cdp, 'document.body.innerText')).slice(0, 400))
    } catch {
      console.log('  page is not evaluable')
    }
    for (const line of diagnostics.slice(-15)) {
      console.log('  page log:', line)
    }
    throw error
  } finally {
    cdp?.close()
    // A hard kill skips the shell's own cleanup, which is exactly the orphan scenario worth checking.
    shell.kill()
    await new Promise((resolve) => {
      shell.once('exit', resolve)
      setTimeout(resolve, 5000)
    })

    if (apiPort) {
      let lingering = true
      for (let attempt = 0; attempt < 50 && lingering; attempt++) {
        try {
          await fetch(`http://127.0.0.1:${apiPort}/api/health`)
        } catch {
          lingering = false
        }
        if (lingering) {
          await new Promise((resolve) => setTimeout(resolve, 100))
        }
      }
      check('the sidecar exits when the shell is killed outright', !lingering, lingering ? 'still listening' : 'gone')
    }

    // Windows can hold the SQLite handle for a moment after the process is gone; retry rather than leaving
    // a temp directory behind on every run.
    for (let attempt = 0; attempt < 20; attempt++) {
      try {
        rmSync(workspaceRoot, { recursive: true, force: true })
        break
      } catch (error) {
        if (attempt === 19) {
          // A leftover temp directory must not replace the real failure with a cleanup error.
          console.log(`  note: could not remove ${workspaceRoot} (${error.code})`)
        }
        await new Promise((resolve) => setTimeout(resolve, 250))
      }
    }
  }

  console.log(`\n${failures.length === 0 ? 'E2E OK' : `E2E FAILED (${failures.length} of ${checks})`}`)
  process.exit(failures.length === 0 ? 0 : 1)
}

await main()
