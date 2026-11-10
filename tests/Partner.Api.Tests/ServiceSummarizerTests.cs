using System.Text.Json;
using Microsoft.AspNetCore.Http;
using System.Net;
using Partner.Api.Services;
using Partner.Api.Settings;
using Partner.Core.Memory;

namespace Partner.Api.Tests;

/// <summary>
/// Summarizer timing, fallback, idempotency and crash recovery, driven by a manual clock so no real timer is
/// waited on.
/// </summary>
public sealed class ServiceSummarizerTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new NoopTimer();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ApiContext Ctx { get; init; }
        public required PendingRounds Rounds { get; init; }
        public required ServiceSummarizer Summarizer { get; init; }
        public required ManualClock Clock { get; init; }
        public required string Dir { get; init; }
        public required ServiceSettings Settings { get; init; }
        public required HttpClient Http { get; init; }

        /// <summary>A test that reopens the same directory must not have it deleted behind it.</summary>
        public bool KeepDir { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Summarizer.DisposeAsync().ConfigureAwait(false);
            await Ctx.DisposeAsync().ConfigureAwait(false);
            Http.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!KeepDir && Directory.Exists(Dir))
            {
                Directory.Delete(Dir, recursive: true);
            }
        }
    }

    private static ServiceSettings SettingsFor(string upstreamBase) => new()
    {
        Id = "svc",
        Name = "Summary",
        Protocol = "openai",
        Token = "0123456789abcdef0123456789abcdef",
        Summary = new ServiceSummarySettings
        {
            Enabled = true,
            IntervalMinutes = 10,
            MaxWaitMinutes = 60,
        },
        Upstream = new ServiceUpstreamSettings
        {
            Inherit = false,
            BaseUrl = upstreamBase,
            Model = "upstream",
            ApiKey = "key",
        },
    };

    private static async Task<Fixture> NewAsync(
        int intervalMinutes,
        int maxWaitMinutes,
        string upstreamBase = "",
        string? dir = null,
        ManualClock? clock = null,
        bool keepDir = false)
    {
        dir ??= Path.Combine(Path.GetTempPath(), "partner-summary-tests", Guid.NewGuid().ToString("N"));
        clock ??= new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ctx = await ApiContext.OpenAsync(dir, clock);
        var settings = SettingsFor(upstreamBase) with
        {
            Summary = new ServiceSummarySettings
            {
                Enabled = true,
                IntervalMinutes = intervalMinutes,
                MaxWaitMinutes = maxWaitMinutes,
            },
        };
        var http = new HttpClient();
        var rounds = new PendingRounds(Path.Combine(dir, "services", settings.Id), clock, ct => PendingRounds.RecoverWindowSeqAsync(ctx.Memory, settings.Id, ct));
        var summarizer = new ServiceSummarizer(() => settings, ctx, http, rounds, clock);
        return new Fixture
        {
            Ctx = ctx, Rounds = rounds, Summarizer = summarizer, Clock = clock,
            Dir = dir, Settings = settings, Http = http, KeepDir = keepDir,
        };
    }

    [Fact]
    public async Task A_window_is_written_only_after_the_idle_interval_and_replay_does_not_duplicate_it()
    {
        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60);
        await f.Rounds.AppendAsync("user asks X", "prefix", "service answers Y", new[] { "tool_a" }, default);

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await f.Summarizer.FlushAsync(force: false, templateOnly: false, default));

        f.Clock.Advance(TimeSpan.FromMinutes(6));
        var id = await f.Summarizer.FlushAsync(force: false, templateOnly: false, default);
        Assert.NotNull(id);

        var memory = await f.Ctx.Memory.GetAsync(id);
        Assert.NotNull(memory);
        Assert.StartsWith("[对外服务:Summary]", memory!.Body, StringComparison.Ordinal);
        Assert.Equal(MemoryOrigins.Service("svc"), memory.Origin);

        // A crash after the write but before the commit leaves the record behind; replaying the window must not
        // write a second one, and the second flush has nothing left to do.
        Assert.Null(await f.Summarizer.FlushAsync(force: true, templateOnly: false, default));
        Assert.Single(await f.Ctx.Memory.ListAsync(new MemoryQuery(Origin: MemoryOrigins.Service("svc"))));
    }

    [Fact]
    public async Task High_frequency_traffic_still_writes_when_max_wait_is_reached()
    {
        await using var f = await NewAsync(intervalMinutes: 1440, maxWaitMinutes: 10);
        for (var i = 0; i < 4; i++)
        {
            await f.Rounds.AppendAsync($"user {i}", "prefix", "answer", Array.Empty<string>(), default);
            if (i < 3)
            {
                f.Clock.Advance(TimeSpan.FromMinutes(3));
            }
        }
        // Idle is 1 minute (far below 1440), but the oldest round has waited 10 minutes.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await f.Summarizer.FlushAsync(force: false, templateOnly: false, default));
    }

    [Fact]
    public async Task A_failed_model_call_falls_back_to_a_template_instead_of_losing_the_window()
    {
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            await reader.ReadToEndAsync();
            ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await ctx.Response.WriteAsync("{\"error\":\"upstream down\"}");
        });

        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, upstreamBase: upstream.BaseUrl);
        await f.Rounds.AppendAsync("user asks about deploy", "prefix", "service answered", Array.Empty<string>(), default);
        f.Clock.Advance(TimeSpan.FromMinutes(11));

        var id = await f.Summarizer.FlushAsync(force: false, templateOnly: false, default);
        Assert.NotNull(id);
        var memory = await f.Ctx.Memory.GetAsync(id);
        Assert.NotNull(memory);
        Assert.StartsWith("[对外服务:Summary]", memory!.Body, StringComparison.Ordinal);
        Assert.Contains("调用方要求", memory.Body); // the template, not a model answer
    }

    [Fact]
    public async Task Rounds_arriving_after_a_flush_belong_to_the_next_window()
    {
        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60);
        await f.Rounds.AppendAsync("first", "prefix", "one", Array.Empty<string>(), default);
        var first = await f.Summarizer.FlushAsync(force: true, templateOnly: false, default);
        Assert.NotNull(first);

        await f.Rounds.AppendAsync("second", "other", "two", Array.Empty<string>(), default);
        var second = await f.Summarizer.FlushAsync(force: true, templateOnly: false, default);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Template_only_skips_the_model_and_still_writes()
    {
        var called = false;
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            called = true;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"模型答案\"}}]}");
        });

        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, upstreamBase: upstream.BaseUrl);
        await f.Rounds.AppendAsync("user asks", "prefix", "service answers", Array.Empty<string>(), default);

        var id = await f.Summarizer.FlushAsync(force: true, templateOnly: true, default);
        Assert.NotNull(id);
        Assert.False(called);

        var memory = await f.Ctx.Memory.GetAsync(id);
        Assert.Contains("调用方要求", memory!.Body);
    }

    /// <summary>
    /// A process that wrote the buffer and then died: no summarizer, so nothing is flushed on the way out, and the
    /// directory is left for the next open. This is what a crash actually leaves behind.
    /// </summary>
    private static async Task<(ApiContext Ctx, PendingRounds Rounds)> OpenRawAsync(
        string dir, ManualClock clock)
    {
        var ctx = await ApiContext.OpenAsync(dir, clock);
        var rounds = new PendingRounds(
            Path.Combine(dir, "services", "svc"), clock, ct => PendingRounds.RecoverWindowSeqAsync(ctx.Memory, "svc", ct));
        return (ctx, rounds);
    }

    [Fact]
    public async Task A_corrupt_state_file_does_not_reuse_a_window_number_that_already_has_a_record()
    {
        // The scenario the recovery exists for: a crash leaves pending rounds on disk and a state file that
        // cannot be parsed. Resetting the counter would reuse m-service-svc-0, which is already written, and the
        // summarizer would read that as "already done" and drop the buffered window.
        var dir = Path.Combine(Path.GetTempPath(), "partner-summary-tests", Guid.NewGuid().ToString("N"));
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using (var first = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, dir: dir, keepDir: true))
        {
            await first.Rounds.AppendAsync("first window", "prefix", "answer one", Array.Empty<string>(), default);
            var id = await first.Summarizer.FlushAsync(force: true, templateOnly: false, default);
            Assert.Equal("m-service-svc-0", id);
        }

        // The next process buffers a window and dies without flushing: the buffer is on disk, no summarizer ran.
        var (crashCtx, crashRounds) = await OpenRawAsync(dir, wall);
        await crashRounds.AppendAsync("second window", "prefix", "answer two", Array.Empty<string>(), default);
        await crashCtx.DisposeAsync();

        // The crash also left the state file half-written.
        await File.WriteAllTextAsync(Path.Combine(dir, "services", "svc", "state.json"), "{ not json");

        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        await using var third = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, dir: dir, clock: clock);

        // The window number is recovered from the record that already exists, so the buffered window is written
        // under a fresh id instead of colliding with the first one.
        var id2 = await third.Summarizer.FlushAsync(force: true, templateOnly: false, default);
        Assert.NotNull(id2);
        Assert.Equal("m-service-svc-1", id2);

        var all = await third.Ctx.Memory.ListAsync(
            new MemoryQuery(Origin: MemoryOrigins.Service("svc"), Limit: int.MaxValue));
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Body.Contains("first window", StringComparison.Ordinal));
        Assert.Contains(all, r => r.Body.Contains("second window", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_late_reply_after_the_window_is_summarized_opens_a_continuation_round()
    {
        var bodies = new List<string>();
        await using var upstream = await FakeUpstream.StartAsync(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            bodies.Add(await reader.ReadToEndAsync());
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"choices\":[{\"message\":{\"content\":\"摘要\"}}]}");
        });

        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, upstreamBase: upstream.BaseUrl);
        await f.Rounds.AppendAsync("同一个请求", "prefix", "第一段回答", Array.Empty<string>(), default);
        Assert.NotNull(await f.Summarizer.FlushAsync(force: true, templateOnly: false, default));

        // A tool-loop step that lands after the window closed must not be dropped at commit; it starts a new round
        // marked as a continuation.
        await f.Rounds.AppendAsync("同一个请求", "prefix", "第二段回答", Array.Empty<string>(), default);

        var view = await f.Rounds.ReadAsync(default);
        var round = Assert.Single(view.Rounds);
        Assert.Equal("第二段回答", Assert.Single(round.Assistants));
        Assert.NotNull(round.ContinuesKey);

        await f.Summarizer.FlushAsync(force: true, templateOnly: false, default);

        // The payload is JSON, which escapes non-ASCII: read the text back instead of searching the wire form.
        var messages = JsonDocument.Parse(bodies[^1]).RootElement.GetProperty("messages");
        var user = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Contains("（续）", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_conversations_with_the_same_last_message_are_two_rounds()
    {
        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60);
        await f.Rounds.AppendAsync("你好", "conversation-a", "answer a", Array.Empty<string>(), default);
        await f.Rounds.AppendAsync("你好", "conversation-b", "answer b", Array.Empty<string>(), default);

        var view = await f.Rounds.ReadAsync(default);
        Assert.Equal(2, view.Rounds.Count);
    }

    [Fact]
    public async Task A_resend_of_the_same_conversation_stays_one_round()
    {
        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60);
        await f.Rounds.AppendAsync("你好", "conversation-a", "step one", Array.Empty<string>(), default);
        await f.Rounds.AppendAsync("你好", "conversation-a", "step two", new[] { "tool_a" }, default);

        var round = Assert.Single((await f.Rounds.ReadAsync(default)).Rounds);
        Assert.Equal(new[] { "step one", "step two" }, round.Assistants);
        Assert.Equal("tool_a", Assert.Single(round.Tools));
    }

    [Fact]
    public async Task Reading_the_state_and_a_tick_that_is_not_due_do_not_split_a_round()
    {
        await using var f = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60);
        await f.Rounds.AppendAsync("同一个请求", "conversation-a", "step one", Array.Empty<string>(), default);

        // What GET /api/services and the minute timer do between two steps of one tool loop.
        await f.Rounds.ReadAsync(default);
        Assert.Null(await f.Summarizer.FlushAsync(force: false, templateOnly: false, default));

        await f.Rounds.AppendAsync("同一个请求", "conversation-a", "step two", Array.Empty<string>(), default);

        var round = Assert.Single((await f.Rounds.ReadAsync(default)).Rounds);
        Assert.Equal(new[] { "step one", "step two" }, round.Assistants);
        Assert.Null(round.ContinuesKey);
    }

    [Fact]
    public async Task A_crash_between_the_record_write_and_the_commit_does_not_write_a_second_record()
    {
        var dir = Path.Combine(Path.GetTempPath(), "partner-summary-tests", Guid.NewGuid().ToString("N"));
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // The dying process got as far as the flush does before its commit: intent recorded, record written.
        var (crashCtx, crashRounds) = await OpenRawAsync(dir, wall);
        await crashRounds.AppendAsync("请求", "prefix", "回复", Array.Empty<string>(), default);
        var snapshot = await crashRounds.SnapshotAsync(default);
        var recordId = $"m-service-svc-{snapshot.WindowSeq:x}";
        await crashRounds.BeginFlushAsync(recordId, snapshot.SnapshotSeq, default);
        await crashCtx.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = recordId,
            Tier = MemoryTier.Episodic,
            Scope = MemoryScope.Recall,
            Body = "[对外服务:Summary] 调用方要求请求；服务回复。",
            Origin = MemoryOrigins.Service("svc"),
            CreatedAt = wall.GetUtcNow(),
        });
        await crashCtx.DisposeAsync();

        await using var next = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, dir: dir);
        Assert.Equal(recordId, await next.Summarizer.FlushAsync(force: true, templateOnly: true, default));

        Assert.Single(await next.Ctx.Memory.ListAsync(
            new MemoryQuery(Origin: MemoryOrigins.Service("svc"), Limit: int.MaxValue)));
        Assert.Empty((await next.Rounds.ReadAsync(default)).Rounds);
    }

    [Fact]
    public async Task The_buffer_survives_a_restart_without_a_state_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "partner-summary-tests", Guid.NewGuid().ToString("N"));
        var wall = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // Written by a process that then died: appended to the log, nothing flushed, no state file left.
        var (crashCtx, crashRounds) = await OpenRawAsync(dir, wall);
        await crashRounds.AppendAsync("buffered", "prefix", "reply", new[] { "t" }, default);
        await crashCtx.DisposeAsync();
        File.Delete(Path.Combine(dir, "services", "svc", "state.json"));

        await using var second = await NewAsync(intervalMinutes: 10, maxWaitMinutes: 60, dir: dir, keepDir: true);
        var round = Assert.Single((await second.Rounds.ReadAsync(default)).Rounds);
        Assert.Equal("buffered", round.User);
        Assert.Equal("reply", Assert.Single(round.Assistants));
    }
}