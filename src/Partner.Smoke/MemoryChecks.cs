namespace Partner.Smoke;

internal static partial class Program
{
    private static async Task MemoryChecksAsync(string root, TimeProvider clock)
    {
        await using (var memory = new MemoryStore(Path.Combine(root, "memory", "memory.db"), clock: clock))
        {
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "pref-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Retain,
                Body = "the user prefers dark mode in every editor",
                Importance = 0.9,
            });
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "noise-1",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Retain,
                Body = "the cat sleeps on the keyboard",
            });

            var hits = await memory.RecallAsync("dark mode editor");
            Check("memory: recall finds the relevant record and not the noise",
                hits.Count > 0 && hits[0].RecordId == "pref-1" && hits.All(h => h.RecordId != "noise-1"),
                string.Join(",", hits.Select(h => h.RecordId)));

            Check("memory: recall is explainable",
                hits.Count > 0 && hits[0].Reasons.Count > 0
                && hits[0].Reasons.Any(r => r.StartsWith("lexical", StringComparison.Ordinal)),
                string.Join(" | ", hits.Count > 0 ? hits[0].Reasons : Array.Empty<string>()));

            // Offline path: the default store has no embedding provider at all, and recall still works.
            Check("memory: offline (no embeddings) still recalls", hits.Count > 0);

            // A hit is one of the records the list shows, so it has to arrive knowing which one: the view
            // filters on these two, and a hit that lost them empties the list under a count that still claims
            // there are hits. The contract test pins the string form over HTTP; this pins the store's own.
            Check("memory: a hit carries the tier and retention of the record it matched",
                hits.Count > 0 && hits[0].Tier == MemoryTier.Semantic && hits[0].Scope == MemoryScope.Retain,
                hits.Count > 0 ? $"tier={hits[0].Tier} scope={hits[0].Scope}" : "(no hits)");

            // Privacy delete: four steps, then compaction removes the row entirely.
            await memory.SoftDeleteAsync("pref-1", Actor.Human);
            var purged = await memory.GetAsync("pref-1");
            var afterDelete = await memory.RecallAsync("dark mode editor");
            var compacted = await memory.CompactAsync();
            Check("memory: privacy delete purges, hides from search, then compacts",
                purged is not null && purged.Body.Length == 0 && purged.ValidTo is not null
                && afterDelete.All(h => h.RecordId != "pref-1")
                && compacted == 1 && await memory.GetAsync("pref-1") is null,
                $"compacted={compacted}");

            // Promotion preserves content as an episodic copy.
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = "working-1",
                Tier = MemoryTier.Working,
                Scope = MemoryScope.Recall,
                Body = "scratch note about the deployment window",
                CreatedAt = clock.GetUtcNow() - TimeSpan.FromHours(30),
            });
            var promoted = await memory.PromoteEligibleAsync();
            var copy = await memory.GetAsync(MemoryStore.EpisodicCopyId("working-1"));
            Check("memory: promotion preserves content as an episodic copy",
                promoted == 1 && copy is not null && copy.Tier == MemoryTier.Episodic
                && copy.Body == "scratch note about the deployment window",
                $"promoted={promoted}");

            // Injection budget — through the production path now: the recall hook (M0) runs the store's
            // InjectAsync, so the same bounded-then-marked contract applies to a turn's prompt. Here the two
            // arms are checked side by side: the store result, and what the hook writes into a turn.
            await using (var tight = new MemoryStore(
                Path.Combine(root, "memory", "tight.db"),
                new MemoryStoreOptions { InjectionTokenLimit = 20, RecallLimit = 10 },
                clock))
            {
                for (var i = 0; i < 5; i++)
                {
                    await tight.WriteAsync(new MemoryRecord
                    {
                        RecordId = $"budget-{i}",
                        Tier = MemoryTier.Semantic,
                        Scope = MemoryScope.Retain,
                        // Distinct bodies that share only the query words: identical (or mostly identical)
                        // records are de-duplicated by recall and superseded on write, and this check needs
                        // several live rows for the token budget to actually cut the list.
                        Body = $"alpha beta gamma d{i} e{i} f{i} h{i} k{i} l{i} m{i}",
                    });
                }

                var injection = await tight.InjectAsync("alpha beta");
                Check("memory: injection respects the token budget",
                    injection.Tokens <= 20 && injection.Truncated && injection.Hits.Count < 5,
                    $"tokens={injection.Tokens} hits={injection.Hits.Count} truncated={injection.Truncated}");

                var hook = new MemoryRecallHook(tight);
                var hookCtx = new HookContext
                {
                    SessionId = "smoke",
                    Source = InputSource.Text,
                    InputPayload = "alpha beta",
                    WorkspaceDirectory = root,
                };
                await hook.RunAsync(hookCtx, default);

                var injectedText = string.Join('\n', hookCtx.InjectedPrompts);
                Check("memory: the recall hook injects through the budget and marks the cut",
                    hookCtx.InjectedPrompts.Count == 1
                    && injectedText.Contains("token budget", StringComparison.Ordinal)
                    && injectedText.Contains("alpha beta gamma", StringComparison.Ordinal),
                    $"prompts={hookCtx.InjectedPrompts.Count} truncatedMark={injectedText.Contains("token budget", StringComparison.Ordinal)}");
            }

            // Maintenance pass (M0): a store nobody reads or writes still ages and prunes itself.
            await using (var dusty = new MemoryStore(
                Path.Combine(root, "memory", "dusty.db"),
                clock: clock))
            {
                var longAgo = clock.GetUtcNow() - TimeSpan.FromDays(90) - TimeSpan.FromMinutes(5);
                await dusty.WriteAsync(new MemoryRecord
                {
                    RecordId = "dusty-idle",
                    Tier = MemoryTier.Semantic,
                    Scope = MemoryScope.Recall,
                    Body = "an idle record nobody recalled in over 90 days",
                    CreatedAt = longAgo,
                });
                await dusty.WriteAsync(new MemoryRecord
                {
                    RecordId = "dusty-expired",
                    Tier = MemoryTier.Working,
                    Scope = MemoryScope.Recall,
                    Body = "a working record whose TTL is long blown",
                    CreatedAt = clock.GetUtcNow() - TimeSpan.FromDays(30),
                });
                await dusty.PromoteEligibleAsync();

                var maintenance = new MemoryMaintenanceService(dusty, clock);
                var dustRun = await maintenance.RunAsync();
                await maintenance.DisposeAsync();

                Check("memory: the maintenance pass ages and trims an untouched store",
                    dustRun.Demoted >= 1
                    && dustRun.Trimmed >= 1
                    && (await dusty.GetAsync("dusty-idle"))!.Tier == MemoryTier.Episodic
                    && await dusty.GetAsync("dusty-expired") is null
                    && await dusty.GetAsync(MemoryStore.EpisodicCopyId("dusty-expired")) is not null,
                    $"demoted={dustRun.Demoted} trimmed={dustRun.Trimmed}");
            }

            // Scale: 100k records, retrieval p90 under 1s.
            const int scale = 100_000;
            var records = new List<MemoryRecord>(scale);
            for (var i = 0; i < scale; i++)
            {
                records.Add(new MemoryRecord
                {
                    RecordId = $"bulk-{i}",
                    Tier = MemoryTier.Semantic,
                    Scope = MemoryScope.Retain,
                    Body = $"record {i} about deployment pipelines and blue green releases in region {i % 16}",
                });
            }

            var seedWatch = System.Diagnostics.Stopwatch.StartNew();
            await memory.WriteBatchAsync(records);
            seedWatch.Stop();

            var queries = new[]
            {
                "deployment pipelines", "blue green releases", "region 7", "record 9999", "pipelines region 3",
            };

            var samples = new List<double>();

            // Warm-up calls are discarded on purpose. The first few recalls on a freshly seeded store pay for
            // SQLite's page cache and the FTS tokenizer's own first pass, and they are not what a person
            // waits for: every call after the first few costs a fraction of them. Sampling the cold calls
            // measures start-up, and would fail a store that answers every real query in 80ms.
            for (var i = 0; i < 3; i++)
            {
                await memory.RecallAsync(queries[i % queries.Length]);
            }

            for (var i = 0; i < 60; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await memory.RecallAsync(queries[i % queries.Length]);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            samples.Sort();
            var p99 = samples[(int)Math.Ceiling(samples.Count * 0.99) - 1];
            Console.WriteLine(
                $"  INFO  memory scale: {scale} records, seed {seedWatch.Elapsed.TotalSeconds:0.0}s, "
                + $"recall median {samples[samples.Count / 2]:0.0}ms p99 {p99:0.0}ms");
            var p90 = samples[(int)Math.Ceiling(samples.Count * 0.90) - 1];
            Check($"memory: {scale} records — retrieval p90 under 1000ms",
                p90 < 1000,
                $"p90={p90:0.0}ms p99={p99:0.0}ms median={samples[samples.Count / 2]:0.0}ms seed={seedWatch.Elapsed.TotalSeconds:0.0}s");
        }

        // The same scale in Chinese, which is a different index: unicode61 cannot tokenize it, so those rows are
        // found through the bigram index instead. At 100k rows the bigram index is the difference between a
        // lookup and a full scan.
        await using (var cjkStore = new MemoryStore(Path.Combine(root, "memory", "cjk-scale.db"), clock: clock))
        {
            const int cjkScale = 100_000;
            var cjkRecords = new List<MemoryRecord>(cjkScale);
            for (var i = 0; i < cjkScale; i++)
            {
                cjkRecords.Add(new MemoryRecord
                {
                    RecordId = $"cjk-{i}",
                    Tier = MemoryTier.Semantic,
                    Scope = MemoryScope.Recall,
                    Body = $"第 {i} 条记录，讲的是部署流程和蓝绿发布，区域 {i % 16}",
                });
            }

            var cjkSeedWatch = System.Diagnostics.Stopwatch.StartNew();
            await cjkStore.WriteBatchAsync(cjkRecords);
            cjkSeedWatch.Stop();

            var cjkQueries = new[]
            {
                "部署流程", "蓝绿发布", "区域 7", "第 9999 条记录", "流程 区域 3",
            };

            var cjkSamples = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                await cjkStore.RecallAsync(cjkQueries[i % cjkQueries.Length]);
            }

            for (var i = 0; i < 60; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await cjkStore.RecallAsync(cjkQueries[i % cjkQueries.Length]);
                watch.Stop();
                cjkSamples.Add(watch.Elapsed.TotalMilliseconds);
            }

            cjkSamples.Sort();
            var cjkP99 = cjkSamples[(int)Math.Ceiling(cjkSamples.Count * 0.99) - 1];
            Console.WriteLine(
                $"  INFO  memory scale (Chinese): {cjkScale} records, seed {cjkSeedWatch.Elapsed.TotalSeconds:0.0}s, "
                + $"recall median {cjkSamples[cjkSamples.Count / 2]:0.0}ms p99 {cjkP99:0.0}ms");
            var cjkP90 = cjkSamples[(int)Math.Ceiling(cjkSamples.Count * 0.90) - 1];
            Check($"memory: {cjkScale} Chinese records — retrieval p90 under 1000ms",
                cjkP90 < 1000,
                $"p90={cjkP90:0.0}ms p99={cjkP99:0.0}ms median={cjkSamples[cjkSamples.Count / 2]:0.0}ms "
                + $"seed={cjkSeedWatch.Elapsed.TotalSeconds:0.0}s");
        }

    }
}
