# Schedules

[简体中文](./schedules.md) | [繁體中文](./schedules.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

A scheduled task combines one sentence with one trigger rule. When the rule fires, the sentence enters the queue with the `schedule` source and is processed in order alongside typed and spoken input.

Scheduled turns do not automatically write general memories or include the main-session summary, other scheduled turns or ordinary recall. The execution can still be read on demand. A completed run is not evidence that a business-level check succeeded.

When the main agent calls `web_fetch` directly, a non-redirected HTTP(S) GET with no read error may record status evidence only if the requested URL contains neither credentials nor a query string. A 5xx opens an anomaly for that task, and a later 2xx from the same target closes it. Only the normalized URI hash is stored; no extra URL or response body is kept. This proves only the HTTP status observed at that time, not that the page or the whole service is healthy. Normal completion, success from another target, delegated results and free-form user text cannot close the anomaly. If one parallel batch returns both 5xx and 2xx for the same target, that target’s state is left unchanged.

Projection shows at most one unresolved structured anomaly for the task together with its source `Seq`. With no such anomaly, it shows the latest ordinary tool-failure lead, which is not a business status. Removing or withdrawing evidence causes a recomputation from what remains visible, so withdrawing resolution evidence can expose an older anomaly again. A recheck must actually follow the current instruction; an old lead is not a substitute for the current result.

## Rules

| Type | Syntax | Details |
|---|---|---|
| `cron` | Five-field cron | Uses the local time zone |
| `interval` | Minutes, ≥ 1 | Counted from creation time |
| `once` | A time point | Must be in the future and is marked executed after it runs |

Limits: content is at most 2,000 characters, intervals range from 1 minute to 1 year, and there can be at most 100 tasks.

## Managing tasks

The **Schedules** page in the sidebar lets you:

- Create a task by choosing a rule and entering its content. The date-time picker offers shortcuts such as “in 1 hour / tomorrow 9:00 / next Monday 9:00”; past times are not selectable.
- Pause or resume it. Resuming skips times missed during the pause rather than replaying them; times missed while the app was closed are skipped as well.
- Delete it.

There is no edit action. Delete and recreate a task when its rule or content changes, so an old delivery record cannot consume a new trigger.

## Letting the assistant create tasks

The main agent always has `schedule_create`, `schedule_list` and `schedule_delete`; sub-agents do not.

- Tool results include the current local time, so a one-off task can say “in N minutes”.
- Approval follows the input-area level: “Approve risky actions” asks only for deletion, “Approve everything” asks for creation and deletion, and listing never asks.
- The reason is that task content is treated as a message from you when it fires. A webpage read by the assistant must not be able to plant a task silently.

## Storage

Tasks and delivery receipts are append-only files at `<workspace>/scheduler/tasks.jsonl` and `receipts.jsonl`.
