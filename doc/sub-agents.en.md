# Sub-agents

[简体中文](./sub-agents.md) | [繁體中文](./sub-agents.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

The main agent only talks and delegates. Slow work such as clicking through windows or reading files goes to a sub-agent in the background; when it finishes, the result comes back as a new input and the main agent relays it to you.

## Scenes

There are two fixed scenes, at most one sub-agent each:

| Scene | Sub-agent tools | Switch | Taken by |
|---|---|---|---|
| `desktop` desktop control | `desktop_*` | `desktop.enabled` | The built-in desktop sub-agent; cannot be deleted |
| `file-read` file access | `file_list`, `file_read`; `file_write` and `file_edit` when write access is available (edits require NTFS transactions) | `files.enabled` + read allowlist; writing also requires `files.writeEnabled` + write allowlist (both off by default) | A sub-agent you add |

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

Edit them under Settings → Agent:

- The main agent and each sub-agent have editable prompts. Unchanged prompts use the built-in default and pick up new defaults on upgrade.
- The generated file-tool boundary, "delegable scenes" and "how to report" sections are not editable; the same boundary applies to text and voice requests.
- Prompt edits do not restart the consumer; they apply on the next turn.
- A sub-agent's model follows the main model by default, or can be set on its own, such as a vision model for the desktop sub-agent.

## File access boundaries

- Native file tools can list, read and, when permitted, create and edit files. Direct file requests must use the corresponding native tool. If it was not called, is unavailable, is denied or fails, the agent should not claim success or switch to desktop, commands or MCP. Desktop tasks explicitly requested through an application are an exception.
- `files.writeEnabled` defaults to off and `files.writeRoots` to empty; old settings gain no write permission. When enabled, configure at least one existing absolute directory. `file_write` supports only local Windows NTFS and requires the target to be in both read and write allowlists. It runs without approval under "No approval" and requires explicit approval each time at the other levels. It creates new files only, never overwriting or editing existing files.
- Creation requires an existing parent directory and does not create directories. Network drives, special paths, reparse points, and Partner data and credential paths are refused. Files are UTF-8 without BOM. If creation fails or its result is unconfirmed, do not claim success or automatically retry; check the target and permissions before deciding what to do.
- `file_edit` uses the same allowlists and approval rules. It replaces one exact UTF-8 match using the SHA-256 version returned by `file_read`. It writes only the original file object's default data stream, preserving the BOM, security descriptor and alternate data streams. The result is capped at 8 MiB. Editing requires a local Windows volume supporting [Transactional NTFS (TxF)](https://learn.microsoft.com/en-us/windows/win32/fileio/transactional-ntfs-portal). Commit rechecks the version and hard-link count under the transaction lock. A changed version, multiple hard links, an unavailable transaction, or an unsupported target prevents the edit; there is no non-transacted fallback.
- This is an instruction to the agent, not a system sandbox: desktop applications, commands and MCP access files under system permissions and do not inherit the file allowlist. The MCP no-approval list is not file authorization. Use system security measures for system-level isolation.
- Paths must be inside `files.roots`. `..` traversal and links pointing outside are refused.
- On Windows and Linux, reads and directory listings check the actual path of the opened handle; platforms where it cannot be verified are refused. Windows alternate data streams are refused. Hard-link aliases cannot be identified as protected files by this check; the allowlist is not a filesystem sandbox.
- `file_list` sorts directories first, then names case-insensitively with an ordinal case-sensitive tie-breaker. It starts at `offset: 0` and returns at most 200 entries by default; specify a nonnegative integer `offset` and a `limit` of 1–200, and use the returned next offset with the same `path` and `limit` when more entries remain. Each call enumerates the directory again, not a snapshot: additions, deletions, or renames can cause duplicates or omissions, so restart after changes.
- Text only, read in segments: 12000 characters by default, 40000 max. Binary files and files over 8 MB are refused.
- `file_read` counts offset and limit in UTF-16 code units and never splits a surrogate pair; an offset inside a pair or a limit too small for one returns an argument error.
- If a page exceeds the context result budget, it is not returned partially; retry the same path and offset with a smaller limit.
- Each read returns a SHA-256 version of the bytes read. The file can change between pages, so compare versions; the version is not a lock and does not guarantee subsequent reads match.
- Even inside the roots, `.env*`, `*.pem`, `*.key`, `*.pfx` and similar key files are never read, nor is the workspace.
- What is read goes into the model context; keep the allowlist narrow.
