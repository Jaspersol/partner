# Desktop control

[简体中文](./desktop-control.md) | [繁體中文](./desktop-control.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

Lets the model see windows, press controls, type and launch programs. Off by default; when on, the desktop sub-agent holds these tools and the main agent can only delegate (see [Sub-agents](./sub-agents.en.md)).

## Turning it on

"Settings → Computer control":

- **Let the assistant operate the computer**: the master switch.
- **Only these windows**: a window allowlist; empty means no limit.
- **Take over the screen?**: foreground / auto / background, see "Background input" below.
- For screenshots, turn on "Can see images" under "Settings → Chat model". Text-only models do not get the screenshot tool.

## Tools

| Tool | What it does | Approval |
|---|---|---|
| `desktop_observe` | Lists windows and the interactive controls inside them | Never |
| `desktop_screenshot` | Captures a window or the whole desktop, with a coordinate grid and numbered controls | Never |
| `desktop_act` | Focus, click, double-click, right-click, key press (with modifiers), type, scroll, drag | By level |
| `desktop_wait` | Waits for a control, a window, or a still frame, up to 5 seconds | Never |
| `desktop_clipboard_get` / `_set` | Read / write the clipboard | Write: yes |
| `desktop_launch` | Starts a program or opens a URI; bare names are looked up in the Start menu first | Yes |
| `desktop_exec` | Runs a command and returns its output, 30 s timeout | Yes |

## Find the control, then act

- Observation returns controls and their click points; actions target an `elementId` instead of having the model guess coordinates.
- When a control supports a UI Automation pattern (buttons, list items, checkboxes), the pattern is invoked directly, without bringing the window to the front.
- After an action, a fresh screenshot of the window is attached by default (`feedback=auto`; also `on-failure`, `off`).
- Chromium / Electron page controls usually show up through UI Automation without a debugging port; starting them through `desktop_launch` additionally opens one, which makes background input and page nodes more complete.

## Approval levels

Switch them from the drop-down in the chat input area; they apply immediately:

| Level | Desktop actions |
|---|---|
| No approval | Never asks |
| Approve risky actions | Asks for running commands, launching programs, clipboard writes, and system shortcuts (Win combinations, Alt+F4, Alt+Tab, Ctrl+Alt+*, Ctrl+Shift+Esc) |
| Approve everything (default) | Asks for every UI action; observing and screenshots never ask |

The approval prompt appears just above the chat input; no answer means denied. Under "Approve everything", one approval lets the same window continue for 10 more steps (within 10 minutes); commands, launches, clipboard writes and system shortcuts still ask every time, and a system shortcut does not spend the lease.

## Guardrails

All guardrails run before any input is injected:

- Key presses and typing require the target window in the foreground; otherwise it is focused first.
- Coordinates must fall inside the target window and the virtual desktop; out-of-range points are refused, not clamped.
- If another window covers the point, the target is brought to the front first; if that fails, the action is refused and the covering window is named.
- Minimized windows cannot be captured or clicked; focus them first to restore.
- Results name the target window and report "posted" and "did the screen change" as two separate facts. The first line is `{"code","delivery","retry"}`: a guard refusal is `not_delivered`+`safe`, and a posted action whose effect was not confirmed is `posted_unverified`+`unsafe`.
- Password fields (UI Automation `IsPassword`, or a page input with `type=password` / `autocomplete` containing password) never report a value; after a write the state compares only empty/filled.
- System shortcuts ask separately: any Win combination, Alt+F4, Alt+Tab, Ctrl+Alt+*, Ctrl+Shift+Esc. Approving one does not grant a task lease.
- A key press or typing aimed at an `elementId` first calls SetFocus and confirms `HasKeyboardFocus`; if the caret is elsewhere the action is refused and the current focus is named.
- Controls in one window that share an identity are marked `ambiguous`; acting on one by `elementId` returns `AMBIGUOUS_TARGET` and asks for coordinates or a narrower observation.
- A window capture checks handle, process start time and rectangle before and after; a frame from a window that moved or was replaced is discarded.
- A hung window is probed with `SendMessageTimeout(WM_NULL, 2s)` first and reported as `APP_UNRESPONSIVE` without entering UI Automation.

## Background input

"Take over the screen?" (`desktop.inputMode`):

| Value | Behavior |
|---|---|
| `foreground` (default) | Uses control patterns or the page protocol when possible, otherwise takes the foreground |
| `auto` | Works in the background when it can, takes over the screen when it cannot |
| `background` | Never takes over the screen; refuses what it cannot do and suggests alternatives |

There are only two background channels: the page debugging protocol (only for browsers / Electron started by `desktop_launch`) and UI Automation patterns. Programs you already had open have no debugging port.

## UIA client

- Uses the UI Automation COM API (`CUIAutomation8`) on a dedicated MTA thread, with a 2-second connection timeout and a 5-second transaction timeout. Waiting for a Chromium tree to settle happens on the caller, not the UIA queue.
- A provider in another process is read through a `CacheRequest` (`AutomationElementMode_Full`) that fetches every property the walk needs; a provider in this process is walked without caching (on this machine a 21-node fixture took 48.4ms cached vs 27.6ms uncached). The default `None` mode is wrong: on an Edge page it returned 63 of 129 nodes.
- Measured on this machine: Notepad 43/43, Explorer 7/7, a Chromium window 87/87 and an Edge page 127/127 nodes (COM vs managed client); single-window observe p50 was 6.9ms (Explorer), 61.7ms (Chromium, at the 40-control cap) and 327.0ms (Edge page).
- The ledger and reproduction commands are in `probes/windows/FINDINGS.md`.

## Known limits

- The debugging port has no authentication and listens on loopback, so other local processes can reach it. It is only opened when `desktop_launch` starts the program.
- No OCR. Interfaces UI Automation cannot see are left to a vision model reading screenshots.
- Measured here: one 2560×1440 screen at 96 DPI (100%), virtual-desktop origin (0,0). Dual monitors, a secondary screen with negative coordinates, and 125%/150% scaling are untested; `probes/windows/run.ps1 -Command monitors` prints screen identity and DPI. Compare handle + bounds + primary flag + scale together, and do not assert Per-Monitor V2 from a `GetProcessDpiAwareness` read-back.
- The "first Chromium read is incomplete, then settles" behaviour was not reproduced here; the code still re-reads at 250ms until the count stops growing.
- The page-control path has not been verified on real Electron apps (such as a music client).
