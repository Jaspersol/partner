# Schedules

[简体中文](./schedules.md) | [繁體中文](./schedules.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

A task is one sentence plus one trigger rule. When it fires, the sentence enters the queue with the "schedule" source, lined up with typing and voice, and runs in order.

Scheduled turns do not automatically create general memories or include the main-session summary, other tasks' turns, or general recall. Execution remains available in history on demand. Completion does not prove a business check passed.

For a direct main-agent `web_fetch`, a non-redirected HTTP(S) GET without a read error can record the status for a target without credentials or query parameters: a 5xx opens an anomaly for that task and a later 2xx for the same target closes it. Only the normalized URI hash is stored, not an additional URL or body. This establishes only the HTTP status at that observation, not page correctness or overall service health. Ordinary completion, another target's success, delegated results, and free-text user statements cannot close the anomaly. If parallel checks in one batch return both 5xx and 2xx for one target, that batch does not advance its state.

Projection shows at most one unresolved structured anomaly for the task and its source `Seq`; otherwise it shows the latest ordinary tool-failure lead, which is not a business status. Withdrawal or deletion recomputes from visible evidence, so withdrawing resolution evidence may reveal an older anomaly again. A recheck must actually follow the current instruction; an old lead is not the current result.

## Rules

| Kind | Format | Notes |
|---|---|---|
| `cron` | Five-field cron | Local time zone |
| `interval` | Minutes, ≥ 1 | Counted from creation |
| `once` | A point in time | Must be in the future; marked done after it runs |

Limits: content up to 2000 characters, interval from 1 minute to 1 year, at most 100 tasks.

## Managing them

The **Schedules** page in the sidebar:

- Create: choose a rule and enter the content. The date-time picker has shortcuts such as "in 1 hour / tomorrow 9:00 / next Monday 9:00"; past times cannot be picked.
- Pause / resume: resuming skips the times missed while paused; nothing is replayed. Times missed while the app was closed are skipped too.
- Delete.

There is no edit. To change a rule or its content, delete and recreate, so old delivery records cannot swallow new triggers.

## Letting the assistant create them

The main agent always has `schedule_create`, `schedule_list` and `schedule_delete` (sub-agents do not).

- Tool results include the current local time, so a one-off task can simply say "in N minutes".
- Approval follows the level in the input area: "Approve risky actions" asks only for deletes; "Approve everything" asks for create and delete; listing never asks.
- Why: when a task fires, its content is treated as a message from you, so a web page the assistant read must not be able to plant one quietly.

## Storage

`<workspace>/scheduler/tasks.jsonl` and `receipts.jsonl`, append-only.
