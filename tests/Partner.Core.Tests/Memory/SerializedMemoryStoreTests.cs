using Partner.Core.Memory;

namespace Partner.Core.Tests.Memory;

/// <summary>The daily maintenance pass and a turn's recall run on different threads over one SQLite connection.</summary>
public sealed class SerializedMemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));

    public SerializedMemoryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recall_writes_and_maintenance_can_overlap_without_corrupting_each_other()
    {
        await using var store = new SerializedMemoryStore(new MemoryStore(Path.Combine(_dir, "m.db")));
        for (var i = 0; i < 20; i++)
        {
            await store.WriteAsync(new MemoryRecord
            {
                RecordId = $"r{i}",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Recall,
                Body = $"用户喜欢第{i}种不同的茶 variety{i}",
            });
        }

        var failures = new List<Exception>();
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            try
            {
                for (var round = 0; round < 25; round++)
                {
                    switch ((worker + round) % 4)
                    {
                        case 0:
                            await store.RecallAsync("喜欢 茶");
                            break;
                        case 1:
                            await store.WriteAsync(new MemoryRecord
                            {
                                RecordId = $"w{worker}-{round}",
                                Tier = MemoryTier.Working,
                                Scope = MemoryScope.Recall,
                                Body = $"临时记录 {worker} {round} unique{worker}x{round}",
                            });
                            break;
                        case 2:
                            await store.DemoteIdleAsync();
                            await store.TrimAsync();
                            break;
                        default:
                            await store.ListAsync();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Empty(failures);
        Assert.NotNull(await store.GetAsync("r0"));
    }
}
