# Memory

[简体中文](./memory.md) | [繁體中文](./memory.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

Say "my intranet account is alice" once, ask about it in other words later, and it remembers. Memory lives in a local SQLite database; every record can be viewed, changed and deleted.

## How it works

| Step | What happens |
|---|---|
| Write | Distilled from the user's own words; scheduled turns and assistant execution paraphrases do not automatically become general memory |
| Recall | Before each request, relevant memories are inserted as background before the current user message; the allowance is bounded by the configured limit and remaining context, without displacing selected history |
| Age | Each injected record carries its tier and age, such as `[established, 10 days ago]`, and the model is told what you say now wins |
| Chinese | A separate bigram full-text index, so Chinese search does not fall back to a table scan |

Sub-agent reports are not stored as memory.

For short references without an explicit subject, recent visible user text can supplement candidate search without replacing the current words. Complete matches of explicit identifiers such as error codes and filenames can receive a bounded boost, explained in `reasons`. The management endpoint `POST /api/memory/recall` searches only the submitted `query`, without conversational context, service-origin exclusions or injection budgets.

Rolling summaries, background recall and source history serve different purposes. Compaction folds older conversation into a rolling summary; facts in that summary are not automatically written to long-term memory. Background recall selects relevant SQLite records each turn. The main agent can also use `session_history` on demand to search the current workspace's history or page through a source by `Seq`; original text is not injected automatically. Search and reads expose the latest revision of text hidden by compaction, but not other withdrawn content. Physically deleted (purged) content cannot be read or recovered from summaries or backups. Tool results contain only text actually recorded; anything truncated before recording cannot be recovered.

`session_history` accepts `day` as `YYYY-MM-DD`, `today`, or `yesterday`, filtering by original event time in the host's local time zone, optionally with `query`. It returns the absolute date and at most eight records. Continue with that date, the original `query`, and the exclusive `beforeSeq`; use `seq` / `offset` to read full text. An empty day does not fall back to another date.

Relevance uses the source record time for age; repeated recall does not make an old fact new. A Working record and its searchable promoted copy do not both occupy retrieval candidates; both remain in the management list.

## Four tiers

Shown in the UI as **New / Experience / Fact / Method**.

- Similar wording or a shared tier and source does not establish the same fact and does not automatically supersede an older record; explicit revisions can still replace it.
- Records not recalled for three months drop one tier and are eventually removed by cleanup.
- You can change a record's tier on the Memory page. Whether something is a fact you rely on is your call.

## On the Memory page

- See every record, including superseded ones, what replaced them and when they were last used.
- Filter by tier, with a count per tier.
- **Pinned**: pinned records are never demoted or cleaned up, but can still be explicitly revised.
- **Create**: write a memory by hand. It defaults to the Fact tier; length 4–400 characters.
- Delete a single record.

## When conversations are deleted

Deleting or withdrawing a conversation also deletes the memories distilled from it. Hand-written memories belong to no conversation and are not deleted with it.

## Memories written by Memory Services

A [Memory Service](./services.en.md) summarizes exchanges with external programs such as game mods and plugins into one sentence starting with `[对外服务:<name>]` and writes it to the main memory. The Memory page labels these records with their service, and "Filter by source" shows only main memories or only one service's. They are recalled like main-session memories but never retire one; deleting a conversation does not delete them. To remove them, delete the service on the Memory Services page with "Also delete the memories it wrote" checked.

## Maintenance and settings

- The host runs maintenance once a day: it first ensures the database has a copy younger than 24 hours (`memory/backups/`, newest 7 kept), then promotes, demotes, drops expired records and compacts. Startup also takes a copy when the newest one is stale. A failed pass is logged and retried next time.
- `memory.embeddings`: local n-gram hashing vectors, off by default, no external dependency. Takes effect the next time the database is opened.

## Storage

`<workspace>/memory/memory.db` is the primary database; `memory/backups/` holds the newest 7 daily copies. Deleting the primary loses only what was written since the last copy.
