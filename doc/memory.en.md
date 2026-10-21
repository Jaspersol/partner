# Memory

[简体中文](./memory.md) | [繁體中文](./memory.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

Say "my intranet account is alice" once, ask about it in other words later, and it remembers. Memory lives in a local SQLite database; every record can be viewed, changed and deleted.

## How it works

| Step | What happens |
|---|---|
| Write | Distilled automatically at the end of each turn. Desktop tasks only contribute actions that succeeded |
| Recall | Before each turn, relevant memories are retrieved and injected into the system prompt, up to about 5000 tokens; overflow is marked as truncated |
| Age | Each injected record carries its tier and age, such as `[established, 10 days ago]`, and the model is told what you say now wins |
| Chinese | A separate bigram full-text index, so Chinese search does not fall back to a table scan |

Sub-agent reports are not stored as memory.

## Four tiers

Shown in the UI as **New / Experience / Fact / Method**.

- When a new record conflicts with an older one in the same tier, the old one is marked superseded and linked to its replacement, but not deleted.
- Records not recalled for three months drop one tier and are eventually removed by cleanup.
- You can change a record's tier on the Memory page. Whether something is a fact you rely on is your call.

## On the Memory page

- See every record, including superseded ones, what replaced them and when they were last used.
- Filter by tier, with a count per tier.
- **Pinned**: pinned records are never demoted or cleaned up, but a newer record in the same tier can still correct them.
- **Create**: write a memory by hand. It defaults to the Fact tier; length 4–400 characters.
- Delete a single record.

## When conversations are deleted

Deleting or withdrawing a conversation also deletes the memories distilled from it. Hand-written memories belong to no conversation and are not deleted with it.

## Maintenance and settings

- The host runs maintenance once a day: promote, demote, drop expired, compact. A failed pass is logged and retried next time.
- `memory.embeddings`: local n-gram hashing vectors, off by default, no external dependency. Takes effect the next time the database is opened.

## Storage

`<workspace>/memory/memory.db`. It is the only copy; deleting it loses it.
