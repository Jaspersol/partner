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
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const repoRoot = join(import.meta.dirname, '..')

// The release shell is the real product: it embeds the built frontend and bundles the published API. A debug
// shell would instead need a running vite dev server, which is a different thing to verify.
const shellExe = process.env.PARTNER_SHELL_EXE ?? join(repoRoot, 'src-tauri', 'target', 'release', 'partner-shell.exe')

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
    throw new Error(`missing ${shellExe} — run "cargo build --manifest-path src-tauri/Cargo.toml" first`)
  }

  const workspaceRoot = mkdtempSync(join(tmpdir(), 'partner-e2e-'))
  const workspace = join(workspaceRoot, 'Partner', 'workspace')
  mkdirSync(join(workspace, 'session'), { recursive: true })
  mkdirSync(join(workspace, 'queue'), { recursive: true })

  const events = [
    { seq: 1, at: '2026-09-26T10:00:00+00:00', kind: 'user', source: 'text', body: 'turn on the lamp' },
    { seq: 2, at: '2026-09-26T10:00:01+00:00', kind: 'assistant', source: 'text', body: 'done' },
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

    // The token is injected by the shell, not read from anywhere by the test.
    const token = await evaluate(cdp, 'window.__PARTNER_TOKEN__')
    const port = await evaluate(cdp, 'window.__PARTNER_PORT__')
    apiPort = port
    check('the shell injected a token and a port into the webview', Boolean(token) && Number.isInteger(port), `port=${port}`)

    await waitFor(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length > 0', 'the session list')
    const rows = await evaluate(cdp, 'document.querySelectorAll(\'[data-test="session-list"] li\').length')
    check('the webview renders the seeded session', rows === 2, `rows=${rows}`)

    const blocked = await evaluate(cdp, 'document.querySelector(\'[data-test="blocked"]\').textContent')
    check('the status bar surfaces blocked work', blocked.includes('失败 1'), blocked.trim())

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

    // Tab switching: every view must render against the same live API.
    await evaluate(cdp, `[...document.querySelectorAll('nav button')].find((b) => b.textContent.trim() === '队列').click()`)
    await waitFor(cdp, 'document.querySelector(\'[data-test="queue-list"] li\') !== null', 'the queue view')
    const queueSummary = await evaluate(cdp, 'document.querySelector(\'[data-test="queue-summary"]\').textContent')
    check('the queue view renders the live queue', queueSummary.includes('失败 1'), queueSummary.trim())

    await evaluate(cdp, `[...document.querySelectorAll('nav button')].find((b) => b.textContent.trim() === '设置').click()`)
    await waitFor(cdp, 'document.querySelector(\'[data-test="utterance-gap"]\') !== null', 'the settings view')
    const gap = await evaluate(cdp, 'document.querySelector(\'[data-test="utterance-gap"]\').value')
    check('the settings view loads the stored value', Number(gap) === 1200, `utteranceGapMs=${gap}`)
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
