using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

/// <summary>
/// The circuit breaker. Its whole value is that it distinguishes three grades of evidence — a deliberate block,
/// a network error, and silence next to a companion that answered — so most of what follows is checking that the
/// grades are kept apart. Getting this wrong in the cautious direction costs a working source; getting it wrong
/// in the other direction costs every search its latency while a dead engine is waited for.
/// </summary>
public sealed class EngineHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record_BelievesADeliberateBlockTheFirstTime()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        health.Record(FakeEngines.Empty(Engine(), blocked: true), companionAnswered: false);

        var snapshot = Assert.Single(health.Snapshot(), entry => entry.Id == "duckduckgo");
        Assert.True(snapshot.Cooling);
        Assert.Equal(1, snapshot.Strikes);
        Assert.Equal(Now.AddMinutes(5), snapshot.Until!.Value);

        // The note is carried into the wording the answer shows, so "cooling" never appears without why.
        Assert.Contains("验证码", health.Unavailable("duckduckgo")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_TreatsAPrefixedBlockFailureTheSameAsABlockVerdict()
    {
        // Two paths reach the same judgement: a parser that recognised a challenge page, and a transport that
        // saw the status.
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        health.Record(FakeEngines.Failed(Engine(), "blocked：403 拒绝"), companionAnswered: false);

        Assert.NotNull(health.Unavailable("duckduckgo"));
    }

    [Fact]
    public void Record_NeedsTwoNetworkErrorsInARow()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        health.Record(FakeEngines.Failed(Engine(), "unreachable：连接被拒绝"), companionAnswered: false);
        Assert.Null(health.Unavailable("duckduckgo"));

        health.Record(FakeEngines.Failed(Engine(), "unreachable：连接被拒绝"), companionAnswered: false);
        Assert.Contains("10 分钟", health.Unavailable("duckduckgo")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 80)]
    [InlineData(6, 160)]
    [InlineData(7, 320)]
    [InlineData(8, 360)] // clamped: 640 minutes would be past the ceiling
    [InlineData(9, 360)]
    public void Record_DoublesTheCooldownUpToSixHours(int strikes, int expectedMinutes)
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        for (var index = 0; index < strikes; index++)
        {
            health.Record(FakeEngines.Empty(Engine(), blocked: true), companionAnswered: false);
        }

        Assert.Contains($"{expectedMinutes} 分钟", health.Unavailable("duckduckgo")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_OneSuccessClearsEverythingTheBreakerBelieved()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        for (var index = 0; index < 3; index++)
        {
            health.Record(FakeEngines.Empty(Engine(), blocked: true), companionAnswered: false);
        }

        var hits = new[] { FakeEngines.Hit("duckduckgo", "anything") };
        health.Record(FakeEngines.Answered(Engine(), hits), companionAnswered: true);

        var snapshot = Assert.Single(health.Snapshot(), entry => entry.Id == "duckduckgo");
        Assert.False(snapshot.Cooling);
        Assert.Equal(0, snapshot.Strikes);
        Assert.Null(snapshot.LastReason);
        Assert.Null(health.Unavailable("duckduckgo"));
    }

    [Theory]
    [InlineData("denied：用户没有批准这次访问")]
    [InlineData("rate_limited：本轮被限速跳过")]
    [InlineData("too_large：正文超过 2 MB")]
    [InlineData("invalid_args：地址不是 http 或 https")]
    [InlineData("not_found：域名不存在")]
    public void Record_DoesNotBlameTheEngineForOurOwnDecisions(string failure)
    {
        // These are either this pipeline's choice or the shape of the question; counting them would grow the
        // cooldown of a source that is working perfectly.
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        for (var index = 0; index < 5; index++)
        {
            health.Record(FakeEngines.Failed(Engine(), failure), companionAnswered: true);
        }

        var snapshot = Assert.Single(health.Snapshot(), entry => entry.Id == "duckduckgo");
        Assert.Equal(0, snapshot.Strikes);
        Assert.False(snapshot.Cooling);
    }

    [Fact]
    public void Record_LeavesAnHonestEmptyResultAloneUntilItHasACompanionAndRepeats()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        // Alone, an empty batch is just an empty result: there is nothing to compare it against.
        for (var index = 0; index < 5; index++)
        {
            health.Record(FakeEngines.Empty(Engine()), companionAnswered: false);
        }

        Assert.Null(health.Unavailable("duckduckgo"));

        // With a companion that found plenty it becomes evidence — but the weakest kind, so it takes three.
        health.Record(FakeEngines.Empty(Engine()), companionAnswered: true);
        health.Record(FakeEngines.Empty(Engine()), companionAnswered: true);
        Assert.Null(health.Unavailable("duckduckgo"));

        health.Record(FakeEngines.Empty(Engine()), companionAnswered: true);
        Assert.Contains("20 分钟", health.Unavailable("duckduckgo")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_CountsAPageTheParserNoLongerUnderstands()
    {
        // A redesign must reach the breaker rather than quietly become "found nothing", which is the failure
        // mode the unparseable status exists to prevent.
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        health.Record(FakeEngines.Unparseable(Engine()), companionAnswered: true);
        Assert.Null(health.Unavailable("duckduckgo"));

        health.Record(FakeEngines.Unparseable(Engine()), companionAnswered: true);
        Assert.NotNull(health.Unavailable("duckduckgo"));
    }

    [Fact]
    public void Record_AddsStrikesUpAcrossFailureClasses()
    {
        // An engine that was unreachable and is now silent is one failing source, not two incidents. The
        // threshold is the current attempt's; the count is everything it has done.
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        health.Record(FakeEngines.Failed(Engine(), "timeout after 8s"), companionAnswered: false);
        health.Record(FakeEngines.Empty(Engine()), companionAnswered: true);

        var snapshot = Assert.Single(health.Snapshot(), entry => entry.Id == "duckduckgo");
        Assert.Equal(2, snapshot.Strikes);

        // Two strikes are only a cooldown for a network error; the silent-empty threshold is three, so the second
        // failure has not cooled it yet.
        Assert.False(snapshot.Cooling);

        health.Record(FakeEngines.Failed(Engine(), "timeout after 8s"), companionAnswered: false);

        // Third strike, and the cooldown is the third one's length rather than the class's.
        Assert.Contains("20 分钟", health.Unavailable("duckduckgo")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_CooldownEndsWhenTheClockSaysSo()
    {
        using var workspace = new TempWorkspace();
        var clock = new MutableClock(Now);
        var health = new EngineHealth(workspace.Root, () => clock.Now);

        health.Record(FakeEngines.Empty(Engine(), blocked: true), companionAnswered: false);
        Assert.NotNull(health.Unavailable("duckduckgo"));

        clock.Now = Now.AddMinutes(6);

        Assert.Null(health.Unavailable("duckduckgo"));

        // The count survives the cooldown — the next offence is the second one and costs twice as long.
        var snapshot = Assert.Single(health.Snapshot(), entry => entry.Id == "duckduckgo");
        Assert.Equal(1, snapshot.Strikes);
        Assert.False(snapshot.Cooling);
        Assert.Null(snapshot.Until);
    }

    [Fact]
    public void A_batch_recorded_without_saving_writes_nothing_until_it_is_saved()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);
        var file = Path.Combine(workspace.Root, "web", "health.json");

        Assert.True(health.Record(FakeEngines.Failed(Engine(), "timeout after 8s"), companionAnswered: true, save: false));
        Assert.True(health.Record(FakeEngines.Unparseable(Engine()), companionAnswered: true, save: false));
        Assert.False(File.Exists(file), "recording alone should not touch the disk");

        health.Save();

        Assert.True(File.Exists(file));
        Assert.Equal(2, EngineHealth.Load(workspace.Root, () => Now).Snapshot().Single(entry => entry.Id == "duckduckgo").Strikes);
    }

    [Fact]
    public void Record_SurvivesARestart()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);
        health.Record(FakeEngines.Unparseable(Engine()), companionAnswered: true);
        health.Save();

        var reloaded = EngineHealth.Load(workspace.Root, () => Now);

        var snapshot = Assert.Single(reloaded.Snapshot(), entry => entry.Id == "duckduckgo");
        Assert.Equal(1, snapshot.Strikes);
        Assert.Contains("结果块不见了", snapshot.LastReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_StartsFreshRatherThanFatalOnACorruptFile()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "web"));
        File.WriteAllText(Path.Combine(workspace.Root, "web", "health.json"), "{\"version\":1,\"engines\":[{\"id\"");

        var health = EngineHealth.Load(workspace.Root, () => Now);

        Assert.Null(health.Unavailable("duckduckgo"));
        Assert.Equal(0, Assert.Single(health.Snapshot(), entry => entry.Id == "duckduckgo").Strikes);
    }

    [Fact]
    public void Snapshot_ListsEveryKnownEngineAndMarksTheUsersOwnSwitches()
    {
        using var workspace = new TempWorkspace();
        var health = NewHealth(workspace);

        var snapshot = health.Snapshot(["duckduckgo"]);

        Assert.Equal(WebEngines.Ids, snapshot.Select(entry => entry.Id));

        // The switch is the user's, so it is reported rather than acted on: the store keeps no opinion about it.
        Assert.True(snapshot.Single(entry => entry.Id == "duckduckgo").DisabledOff);
        Assert.All(
            snapshot.Where(entry => entry.Id != "duckduckgo"),
            entry => Assert.False(entry.DisabledOff));
    }

    [Fact]
    public void FilePath_IsWhereTheSettingsPageSaysItIs()
    {
        using var workspace = new TempWorkspace();

        Assert.Equal(
            Path.Combine(workspace.Root, "web", "health.json"),
            EngineHealth.Load(workspace.Root).FilePath);
    }

    /// <summary>A throwaway workspace per test, so no test can read another one's health file.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            "partner-web-health-tests",
            Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A leftover directory in the temp folder is not a reason for a test to fail.
            }
        }
    }

    /// <summary>A clock the test moves by hand, so no test ever waits five minutes.</summary>
    private sealed class MutableClock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    private static EngineHealth NewHealth(TempWorkspace workspace) => new(workspace.Root, () => Now);

    private static IWebSearchEngine Engine() => FakeEngines.Make("duckduckgo", ContentKinds.General);
}
