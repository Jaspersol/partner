# Sub-agents

[简体中文](./sub-agents.md) | [繁體中文](./sub-agents.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

The main agent only talks and delegates. Slow work such as clicking through windows or reading files goes to a sub-agent in the background; when it finishes, the result comes back as a new input and the main agent relays it to you.

## Scenes

There are two fixed scenes, at most one sub-agent each:

| Scene | Sub-agent tools | Switch | Taken by |
|---|---|---|---|
| `desktop` desktop control | `desktop_*` | `desktop.enabled` | The built-in desktop sub-agent; cannot be deleted |
| `file-read` file reading | `file_list`, `file_read` (read-only) | `files.enabled` (off by default) + directory allowlist | A sub-agent you add |

- A scene nobody takes is handled by the main agent. For example, with file access on and no file-reading sub-agent, the `file_*` tools go to the main agent.
- Desktop is the exception: desktop tools never go to the main agent. Turning desktop on requires an enabled desktop sub-agent; this is checked on save.

## Delegation and reports

- The main agent gets three tools: `agent_delegate` (returns immediately), `agent_job_status`, `agent_job_cancel`.
- Jobs run in the background without holding the main conversation. The report enters the same queue, and the main agent starts a new turn to relay it.
- Each job has its own session log (`agents/<agent>/job-*/session/`); each agent keeps only the newest 30 jobs. Screenshots and file contents stay out of the main session and out of memory.
- A sub-agent runs one job at a time. A target that just failed cannot be delegated again unchanged for 10 minutes.
- After a crash, unfinished jobs get an "interrupted" report on the next start.

## Retries and failure

- Each job runs up to `maxRounds` rounds, default 5 (1–10). Total limit 15 minutes.
- Success or failure is decided by the sub-agent calling `task_report`: success needs evidence, failure needs an error. Not calling it counts as failure.
- Terminal failures (approval denied, governance refusal, cancel, timeout and so on) are not retried. Before other retries, the sub-agent is told which actions the previous round already performed and asked to look at the current state first.
- When every round fails, the main agent receives the failure category, the last error and a per-round log, and is told to report it honestly rather than claim success.

## Prompts and models

Edit them on the **agent** page in the sidebar:

- The main agent and each sub-agent have editable prompts. Unchanged prompts use the built-in default and pick up new defaults on upgrade.
- The generated "delegable scenes" and "how to report" sections at the end are not editable.
- Prompt edits do not restart the consumer; they apply on the next turn.
- A sub-agent's model follows the main model by default, or can be set on its own, such as a vision model for the desktop sub-agent.

## File reading boundaries

- Paths must be inside `files.roots`. `..` traversal and links pointing outside are refused.
- Text only, read in segments: 12000 characters by default, 40000 max. Binary files and files over 8 MB are refused.
- Even inside the roots, `.env*`, `*.pem`, `*.key`, `*.pfx` and similar key files are never read, nor is the workspace.
- What is read goes into the model context; keep the allowlist narrow.
