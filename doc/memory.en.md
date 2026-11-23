# Memory

[简体中文](./memory.md) | [繁體中文](./memory.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

For example, mention once that “my intranet account is alice”. Later, ask in different words and the system can retrieve it. Memories live in a local SQLite database, where each record can be viewed, edited or deleted.

## How it works

| Stage | Behavior |
|---|---|
| Write | Facts are distilled from the user’s own words. Scheduled turns and assistant execution paraphrases do not automatically become general memories |
| Recall | Relevant records are searched before each request and added as background before the current user message. The allowance is bounded by the configured limit and remaining context, without displacing selected history |
| Age | Each injected item includes its tier and age, such as `[established, 10 days ago]`, and the model is told to prioritize what you say now |
| Chinese search | A separate bigram full-text index avoids scanning the entire table |

Reports from sub-agents are not saved as memories.

For a short reference with no clear subject, the most recent visible user text in the conversation may help form candidate searches, without replacing the original query. Exact matches for explicit identifiers such as error codes or filenames may receive a limited score boost, with the reason recorded in `reasons`. The management endpoint `POST /api/memory/recall` searches only its submitted `query`: it adds no conversation context and uses neither service-origin exclusions nor injection budgets.

Rolling summaries, background recall and source-text reads have separate purposes. Compaction folds older conversation into a rolling summary, but facts in that summary are not automatically written as long-term memories. Background recall selects relevant SQLite records on each turn. On demand, the main agent can use `session_history` to search the current workspace or read original text in `Seq` pages; source text is not injected automatically. Searches and reads can expose original messages hidden by compaction and their latest revision, but not other withdrawn content. After physical deletion (`purge`), content cannot be read or recovered from summaries or backups. Tool results contain only text actually written to storage, so content truncated before it was recorded cannot be recovered.

The `day` argument to `session_history` accepts `YYYY-MM-DD`, `today` or `yesterday`. Filtering uses the host machine’s local time zone and original event time, and can be combined with `query`. Results include the absolute date and up to 8 entries. To continue, use the returned date, the same `query` and exclusive `beforeSeq`; retrieve message text with `seq` / `offset`. If there are no matches for that day, the search does not fall back to another date.

Age for relevance is based on the source record’s timestamp; recalling a memory repeatedly does not make an old fact recent. A Working record and its searchable promoted copy do not both consume retrieval-candidate slots, although the management list still shows both.

## Four tiers

The UI labels are **New / Experience / Fact / Method**.

- Similar wording, or sharing a tier and source, does not establish that two records describe the same fact, so it does not automatically invalidate the older one. An explicit revision can still replace it.
- A record not recalled for three months drops one tier and is eventually removed during scheduled cleanup.
- You can change tiers manually on the Memory page. You decide which facts you rely on.

## Memory page

- Browse all records, including inactive ones, what superseded them and when they were last used.
- Filter by tier and see a count for each tier.
- **Pin** a record to prevent automatic demotion or cleanup; an explicit revision can still replace it.
- **Create** a memory manually. It starts at the Fact tier and must be 4–400 characters long.
- Delete individual records.

## Effect of deleting conversations

Deleting or withdrawing a conversation also removes memories distilled from it. Manually written memories are not linked to a turn, so they remain.

## Memories from Memory Services

[Memory Services](./services.en.md) turns interactions from external programs such as game mods and plugins into one-sentence memories in the main store. Their source tag starts with `[对外服务:<name>]`. The Memory page labels each source and lets you filter for main memories or one service. These records are recalled alongside main-session memories but do not invalidate them; deleting a conversation does not remove service memories. To remove them, delete the service on the Memory Services page and select “Also delete the memories it wrote”.

## Maintenance and settings

- The host runs daily maintenance. It first ensures a database backup no older than 24 hours exists in `memory/backups/` (the newest 7 are retained), then promotes and demotes records, removes expired ones and compacts the store. Startup also creates a copy if the latest backup is stale. A failed pass is logged and retried next time.
- `memory.embeddings` uses local n-gram hashing vectors. It is off by default, has no external dependencies and takes effect the next time the database is opened.

## Storage

The primary database is `<workspace>/memory/memory.db`; `memory/backups/` contains the latest 7 daily copies. Removing the primary loses writes made after the most recent backup.
