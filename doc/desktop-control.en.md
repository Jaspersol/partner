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
- Chromium / Electron apps started through `desktop_launch` get a debugging port, so buttons inside the page show up as controls too.

## Approval levels

Switch them from the drop-down in the chat input area; they apply immediately:

| Level | Desktop actions |
|---|---|
| No approval | Never asks |
| Approve risky actions | Asks only for running commands, launching programs and writing the clipboard |
| Approve everything (default) | Asks for every UI action; observing and screenshots never ask |

The approval prompt appears just above the chat input; no answer means denied. Under "Approve everything", one approval lets the same window continue for 10 more steps (within 10 minutes); commands, launches and clipboard writes still ask every time.

## Guardrails

All guardrails run before any input is injected:

- Key presses and typing require the target window in the foreground; otherwise it is focused first.
- Coordinates must fall inside the target window and the virtual desktop; out-of-range points are refused, not clamped.
- If another window covers the point, the target is brought to the front first; if that fails, the action is refused and the covering window is named.
- Minimized windows cannot be captured or clicked; focus them first to restore.
- Results name the target window and report "input injected" and "did the screen change" as two separate facts.

## Background input

"Take over the screen?" (`desktop.inputMode`):

| Value | Behavior |
|---|---|
| `foreground` (default) | Uses control patterns or the page protocol when possible, otherwise takes the foreground |
| `auto` | Works in the background when it can, takes over the screen when it cannot |
| `background` | Never takes over the screen; refuses what it cannot do and suggests alternatives |

There are only two background channels: the page debugging protocol (only for browsers / Electron started by `desktop_launch`) and UI Automation patterns. Programs you already had open have no debugging port.

## Known limits

- The debugging port has no authentication and listens on loopback, so other local processes can reach it. It is only opened when `desktop_launch` starts the program.
- No OCR. Interfaces UI Automation cannot see are left to a vision model reading screenshots.
- Only verified on a single 100% DPI screen; multi-monitor and high DPI are untested.
- The page-control path has not been verified on real Electron apps (such as a music client).
