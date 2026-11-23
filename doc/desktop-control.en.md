# Desktop control

[简体中文](./desktop-control.md) | [繁體中文](./desktop-control.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

When enabled, desktop control lets the model inspect windows, operate controls, type and launch programs. It is off by default. The desktop sub-agent owns these tools, and the main agent can only delegate to it (see [Sub-agents](./sub-agents.en.md)).

## Enabling it

Configure it under “Settings → Computer control”:

- **Let the assistant operate the computer**: the master switch.
- **Allow system commands**: an independent switch, off by default, which also requires desktop control. Commands are approved one at a time only under “Approve everything”; a window lease never covers them.
- **Only allow these windows**: the window allowlist; an empty list means no restriction.
- **Take over the screen?**: choose foreground, auto or background; see “Background input” below.
- To let the model inspect screenshots, enable “Can see images” under “Settings → Chat model”. Text-only models do not register the screenshot tool.

GUI applications, commands and external MCP processes access files using system permissions and do not inherit the native file-tool allowlist. The MCP no-approval list is not file authorization. Use system security controls when isolation is needed; filtering command strings is not a sandbox. If a native file tool is unavailable or fails, explain why and adjust the permission, target or parameters rather than bypassing it through these entry points.

## Tools

| Tool | Purpose | Approval |
|---|---|---|
| `desktop_observe` | Lists windows and interactive controls inside them | None |
| `desktop_screenshot` | Captures a window or the whole desktop with a coordinate grid and control numbers | None |
| `desktop_act` | Focuses, clicks, double-clicks, right-clicks, presses keys, types, scrolls and drags | By level |
| `desktop_wait` | Waits for a control, window or still frame, for up to 5 seconds | None |
| `desktop_clipboard_get` / `_set` | Reads or writes the clipboard | Writing requires approval |
| `desktop_launch` | Launches a program or opens a URI; bare names are resolved through the Start menu first | Yes |
| `desktop_exec` | Executes a command and returns its output, with a 30-second timeout | Only under “Approve everything” |

## Find a control before acting

- Observation returns controls and their click points. Actions should identify the target with `elementId`, rather than relying on guessed coordinates.
- If a control supports a UI Automation pattern, invoke it directly. Buttons, list items and checkboxes do not need the window in the foreground.
- A fresh screenshot of the window is attached after an action by default (`feedback=auto`); `on-failure` and `off` are also available.
- Chromium and Electron page controls are usually visible through UI Automation without a debugging port. Starting an app with `desktop_launch` additionally opens a debugging port, which provides more complete background input and page-node information.

## Approval levels

Switch levels from the drop-down in the chat input area; changes apply immediately:

| Level | Desktop actions |
|---|---|
| No approval | Desktop actions and commands run without asking |
| Approve risky actions | Launches, clipboard writes and system shortcuts (Win combinations, Alt+F4, Alt+Tab, Ctrl+Alt+* and Ctrl+Shift+Esc) require approval; commands do not |
| Approve everything (default) | UI actions use window-lease approval, while commands ask individually; observation and screenshots do not ask |

The approval prompt appears directly above the chat input. No response means denial. Under “Approve everything”, once an approval is accepted by the execution pipeline, the same window may continue for 10 steps within 10 minutes. Commands, launches, clipboard writes and system shortcuts still ask every time, and system shortcuts do not consume those 10 lease steps. An approval arriving after cancellation, timeout or a settings invalidation does not leave a lease behind. A lease is tied to the settings revision in force when it was granted and cannot survive a settings change.

Desktop mutations are serialized within the process. Governance decisions are tied to the settings revision at decision time: any settings save or reload while an action is waiting for approval or queued invalidates an action that has not started. Restoring the old approval level does not revive it; start the action again and approve it under the current settings. The check performed under the lock does not consume another task-lease step. Disabling desktop control also rejects actions that have not started. A timeout does not prove that nothing happened, so observe the current state before retrying.

## Guardrails

Every guard is checked before input is injected:

- Key presses and typing require the target window to be foreground; otherwise it is focused first.
- Coordinates must be inside both the target window and the virtual desktop. Out-of-range coordinates are refused, not clamped to an edge.
- If another window covers the point, the target is brought forward before clicking. If that fails, the action is refused and the covering window is named.
- Minimized windows cannot be captured or clicked; they must be focused and restored first.
- Results identify the target window and separately report whether the action was posted and whether the screen changed. The first line is `{"code","delivery","retry"}`: a guard refusal is `not_delivered` + `safe`; a posted action without confirmed effect is `posted_unverified` + `unsafe`.
- Password fields (UI Automation `IsPassword`, or page inputs with `type=password` / `autocomplete` containing password) never expose values. After writing, only empty versus filled state is compared.
- System shortcuts require separate approval: any Win combination, Alt+F4, Alt+Tab, Ctrl+Alt+* or Ctrl+Shift+Esc. Approval does not grant a task lease.
- Key presses and typing aimed at an `elementId` first call SetFocus and verify `HasKeyboardFocus`. If focus is elsewhere, the action is refused and the current focus is reported.
- Controls with the same identity in one window are marked `ambiguous`. Acting by `elementId` returns `AMBIGUOUS_TARGET` and asks for coordinates or a narrower observation.
- Captures compare the window handle, process start time and rectangle before and after. A frame from a window that moved or was replaced is discarded.
- A hung window is probed with `SendMessageTimeout(WM_NULL, 2s)` first. `APP_UNRESPONSIVE` is returned without entering UI Automation.

## Background input

The “Take over the screen?” setting is `desktop.inputMode`:

| Value | Behavior |
|---|---|
| `foreground` (default) | Uses a control pattern or page protocol when possible; otherwise takes the foreground |
| `auto` | Completes work in the background when possible, then takes over the screen when necessary |
| `background` | Never takes over the screen; refuses impossible actions and suggests alternatives |

There are only two background channels: the page debugging protocol (only for browsers / Electron started by `desktop_launch`) and UI Automation patterns. Applications that were already open do not have a debugging port.

## UIA client

- The UI Automation COM API (`CUIAutomation8`) runs on a dedicated MTA thread. Connection timeout is 2 seconds and transaction timeout is 5 seconds. Waiting for a Chromium tree to settle happens on the caller, not on the UIA queue.
- Providers in another process are read through `CacheRequest` (`AutomationElementMode_Full`), which fetches all properties needed for traversal. Providers in the same process are read without caching. On this machine, a 21-node fixture took 48.4ms cached versus 27.6ms uncached. The default `None` mode is unsuitable: an Edge page returned 63 rather than 129 nodes in testing.
- Measurements on this machine: Notepad 43/43, Explorer 7/7, Chromium window 87/87 and Edge page 127/127 nodes (COM and managed clients agree). Single-window observe p50 was 6.9ms for Explorer, 61.7ms for Chromium at the 40-control cap, and 327.0ms for an Edge page.
- The tested environment was one 2560×1440 screen at 96 DPI (100%) with virtual-desktop origin (0,0). Dual monitors, negative-coordinate secondary screens and 125%/150% scaling are untested. Compare handle, bounds, primary-screen flag and scale together; do not infer Per-Monitor V2 from a `GetProcessDpiAwareness` read-back.

## Known limits

- The debugging port has no authentication and listens on loopback, so other local processes can reach it. It is opened only when `desktop_launch` starts an application.
- There is no OCR. Interfaces outside UI Automation are left to a vision model reading screenshots.
- The same single-screen environment is the only one measured: 2560×1440, 96 DPI (100%) and origin (0,0). Dual monitors, negative-coordinate secondary screens and 125%/150% scaling remain untested; validation must still combine handle, bounds, primary flag and scale rather than use `GetProcessDpiAwareness` alone.
- The incomplete-first-read behavior during Chromium cold start was not reproduced locally. The code still re-reads every 250ms until the node count stabilizes.
- Page-control paths in real Electron applications, such as music clients, have not been verified.
