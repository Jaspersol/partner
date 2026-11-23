# Sub-agents

[简体中文](./sub-agents.md) | [繁體中文](./sub-agents.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

The main agent handles conversation and delegation. Time-consuming work such as operating windows or reading files can run in a sub-agent in the background. When it finishes, its report enters the queue as a new input for the main agent to relay.

## Scenes

There are two fixed scenes, with at most one sub-agent assigned to each:

| Scene | Available tools | Switch | Owner |
|---|---|---|---|
| `desktop` desktop control | `desktop_*` | `desktop.enabled` | Built-in desktop sub-agent; cannot be deleted |
| `file-read` file access | `file_list`, `file_read`, plus `file_write` and `file_edit` when write requirements are met (editing requires NTFS transactions) | `files.enabled` + read allowlist; writing also needs `files.writeEnabled` + write allowlist (both off by default) | A sub-agent you create |

- If no sub-agent owns a scene, the main agent handles it. For example, if file reading is enabled but no file-reading sub-agent exists, the main agent receives the `file_*` tools.
- Desktop control is different: desktop tools are never assigned to the main agent. Enabling desktop control requires an enabled desktop sub-agent, and the setting is checked when saved.

## Delegating and reporting

- The main agent has three tools: `agent_delegate` (returns immediately), `agent_job_status` and `agent_job_cancel`.
- Jobs run in the background without blocking the main conversation. A completed report returns through the same queue, and the main agent starts a new turn to relay it.
- Each job has its own session log at `agents/<agent>/job-*/session/`. Each agent retains only its latest 30 jobs. Screenshots and file contents do not enter the main session or memory.
- A sub-agent runs only one job at a time. After a target fails, the identical task cannot be assigned again for 10 minutes.
- If the process crashes, unfinished jobs receive an “interrupted” report at the next startup.

## Retries and failures

- A job can use up to `maxRounds` rounds, 5 by default (configurable from 1 to 10), with a total time limit of 15 minutes.
- The sub-agent declares success or failure by calling `task_report`. Success requires evidence; failure requires an error. Not calling the tool counts as failure.
- Terminal failures such as denied approval, governance refusal, cancellation or timeout are not retried. Before retrying other failures, the sub-agent is told what actions the previous round already performed and asked to inspect the current state first.
- If every round fails, the main agent receives the failure category, the final error and the round-by-round log. It is required to report the outcome accurately, not claim success.

## Prompts and models

Edit prompts under **Settings → Agent**:

- The main agent and every sub-agent have editable prompts. A prompt that has not been customized uses the built-in default and picks up updated defaults after an upgrade.
- Automatically appended file-tool boundaries, delegable scenes and reporting instructions cannot be edited. Text and voice requests follow the same boundaries.
- Prompt changes take effect on the next turn without restarting the consumer.
- A sub-agent follows the main model by default but can use a separately configured model, such as a vision model for desktop work.

## File access boundaries

- Native file tools list directories and read files, and can create or edit files when the requirements are met. Direct file requests must use the corresponding native tool. If the tool was not called, is unavailable, denied or fails, do not claim success or switch to desktop, commands or MCP to bypass it. The exception is a desktop task the user explicitly asked to perform through an application.
- `files.writeEnabled` is off and `files.writeRoots` is empty by default; existing settings do not gain write access automatically. Enabling writes requires at least one existing absolute directory. `file_write` supports local Windows NTFS only, and its target must be in both read and write allowlists. It runs directly under “No approval”; other levels require explicit approval each time. It creates files only and never overwrites or edits existing ones.
- The parent directory for a new file must already exist; directory creation is not supported. Network drives, special paths, reparse points, and Partner data or credential paths are rejected. Files are written as UTF-8 without a BOM. If creation fails or the result is unconfirmed, do not claim success or retry automatically; inspect the target and permissions before deciding what to do.
- `file_edit` uses the same allowlists and approval rules. It performs an exact replacement of one unique UTF-8 match using the SHA-256 version returned by `file_read`. It writes only the original file object's default data stream while preserving the BOM, security descriptor and alternate data streams. The result is limited to 8 MiB. Editing is allowed only on local Windows volumes that support [Transactional NTFS (TxF)](https://learn.microsoft.com/en-us/windows/win32/fileio/transactional-ntfs-portal). The transaction lock rechecks the version and hard-link count at commit; a changed version, multiple hard links, unavailable transactions or unsupported targets cause refusal, with no non-transactional fallback.
- These rules guide the agent; they are not a system sandbox. Desktop applications, commands and MCP processes access files under system permissions and do not inherit the file allowlist. The MCP no-approval list is not file authorization. Use system-level security measures when isolation is required.
- Paths must stay inside `files.roots`. `..` traversal and links that point outside are rejected.
- On Windows and Linux, reads and directory enumeration check the actual path of the opened handle; platforms where this cannot be verified refuse access. Windows alternate data streams are rejected. Hard-link aliases to protected files cannot necessarily be detected, so the allowlist is not a filesystem sandbox.
- `file_list` sorts directories first and then names case-insensitively, using ordinal character order to break case-only ties. It starts at `offset: 0` and returns up to 200 entries by default. Supply a nonnegative integer `offset` and `limit` from 1 to 200; if more results remain, continue at the returned offset with the same `path` and `limit`. Each call re-enumerates the directory rather than reading a snapshot, so additions, deletions or renames can produce duplicates or omissions. Restart enumeration after changes.
- Only text is read, in chunks of 12,000 characters by default and up to 40,000. Binary files and files larger than 8 MB are refused.
- `file_read` counts `offset` and `limit` in UTF-16 code units and does not split surrogate pairs. A limit too small for a pair or an offset inside one produces an argument error.
- A page that exceeds the context-result budget is not returned partially. Keep the same path and offset, reduce the limit and try again.
- Each read returns the SHA-256 version of the bytes read. Compare it across pages because the file may change; this value is not a lock and does not guarantee later reads are consistent.
- Even within an allowed directory, files such as `.env*`, `*.pem`, `*.key` and `*.pfx` are not read, nor is the workspace as a whole.
- Read content becomes part of model context, so keep the allowlist narrow.
