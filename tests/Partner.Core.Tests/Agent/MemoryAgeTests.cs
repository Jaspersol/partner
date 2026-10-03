using Partner.Core.Agent.Hooks;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

/// <summary>
/// What a recalled memory says about itself when it is injected.
/// <para>
/// The problem is not retrieval — that ranks correctly — it is that a ranked line read as a bare fact is
/// indistinguishable from something the person just said. These pin the wording that keeps them apart.
/// </para>
/// </summary>
public sealed class MemoryAgeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "partner-memory-age-tests", Guid.NewGuid().ToString("n"));

    private JsonlSessionStore? _session;
    private MemoryStore? _memory;

    public void Dispose()
    {
        _memory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MemoryStore Open()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        _session = session;
        _memory = new MemoryStore(Path.Combine(_dir, "memory.db"), clock: new FixedClock(Now));
        return _memory;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HookContext Context(string payload) => new()
    {
        SessionId = "main",
        Source = InputSource.Text,
        InputPayload = payload,
        WorkspaceDirectory = ".",
    };

    private async Task<string> InjectAsync(MemoryTier tier, DateTimeOffset createdAt, string body, string query)
    {
        await using var memory = Open();

        await memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m-1",
            Tier = tier,
            Scope = MemoryScope.Recall,
            Body = body,
            CreatedAt = createdAt,
        });

        var ctx = Context(query);
        await new MemoryRecallHook(memory).RunAsync(ctx, default);

        return Assert.Single(ctx.InjectedPrompts);
    }

    [Fact]
    public async Task A_hit_carries_when_it_was_recorded()
    {
        // Without the timestamp there is nothing for a caller to say, and the line reads as current.
        await using var memory = Open();

        await memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m-1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "用户喜欢深色主题",
            CreatedAt = Now.AddDays(-3),
        });

        var hit = Assert.Single(await memory.RecallAsync("深色主题"));

        Assert.Equal(Now.AddDays(-3), hit.CreatedAt);
        Assert.Equal(Now, hit.Now);
        Assert.Equal(MemoryTier.Semantic, hit.Tier);
    }

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(90, "1 小时前")]
    [InlineData(1_440, "1 天前")]
    [InlineData(43_000, "29 天前")]
    [InlineData(43_300, "很久以前")]
    [InlineData(576_000, "很久以前")]
    public void An_age_is_said_in_words_rather_than_a_number(int minutesAgo, string expected)
    {
        Assert.Equal(expected, MemoryAge.Describe(Now.AddMinutes(-minutesAgo), Now));
    }

    [Fact]
    public void A_fresh_memory_does_not_claim_a_precision_it_does_not_have()
    {
        // Minutes collapse to "刚刚": a 20-minute-old memory is not meaningfully fresher than a 40-minute-old
        // one, and a precise figure invites reasoning about a distinction that does not matter.
        Assert.Equal("刚刚", MemoryAge.Describe(Now.AddMinutes(-20), Now));
    }

    [Fact]
    public async Task An_injected_memory_says_how_old_it_is()
    {
        var injected = await InjectAsync(
            MemoryTier.Semantic, Now.AddDays(-10), "用户喜欢深色主题", "我的主题呢");

        Assert.Contains("10 天前", injected, StringComparison.Ordinal);
        Assert.Contains("深色主题", injected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfirmed_memory_says_so()
    {
        // Working-tier records have not survived the promotion age, so nothing has corroborated them by their
        // being recalled again. Saying that is the difference between background and a fact.
        var injected = await InjectAsync(
            MemoryTier.Working, Now.AddHours(-2), "用户可能在用 pnpm 包管理器", "现在用哪个包管理器");

        Assert.Contains("unconfirmed", injected, StringComparison.Ordinal);
        Assert.Contains("2 小时前", injected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_established_memory_does_not_say_unconfirmed()
    {
        var injected = await InjectAsync(
            MemoryTier.Semantic, Now.AddDays(-10), "用户喜欢深色主题", "我的主题呢");

        Assert.Contains("established", injected, StringComparison.Ordinal);
        Assert.DoesNotContain("unconfirmed", injected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_header_says_prefer_what_the_person_says_now()
    {
        // The whole point: without this line, a stale preference and a current instruction look the same to
        // the model, and the stale one can win.
        var injected = await InjectAsync(
            MemoryTier.Semantic, Now.AddDays(-30), "用户喜欢深色主题", "主题");

        Assert.Contains("contradict", injected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background", injected, StringComparison.OrdinalIgnoreCase);
    }
}
