using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Partner.Api.Auth;
using Partner.Api.Voice;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// The privacy cascade over HTTP: deleting a session event must delete what was remembered from it.
/// <para>
/// Its own fixture because it needs a session store to write into before it deletes anything, and the shared
/// <c>ViewEndpointsTests</c> fixture asserts on counts that a seeded event would change.
/// </para>
/// </summary>
public sealed class MemoryCascadeTests : ApiFixture
{
    protected override string WorkspaceName => "partner-memory-cascade";

    [Fact]
    public async Task Deleting_an_event_removes_the_memory_distilled_from_it()
    {
        // Without the cascade, a deleted event's memory would still be recalled into later conversations.
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "我用公司内网账号 alice",
        });
        var seq = _context.Session.LastSeq;

        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = $"m-{seq:x}",
            Tier = MemoryTier.Working,
            Scope = MemoryScope.Recall,
            Body = "用户使用内网账号 alice",
            SourceEventSeq = seq,
        });

        Assert.Single((await _http.GetFromJsonAsync<JsonElement>("/api/memory")).EnumerateArray());

        var deleted = await _http.PostAsync($"/api/session/{seq}/delete", null);
        Assert.True(deleted.IsSuccessStatusCode);

        var body = await deleted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("purgedMemories").GetInt32());

        // Gone from the view and from recall; recall is the part that matters.
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/memory")).EnumerateArray());
        Assert.Empty(await _context.Memory.RecallAsync("内网账号"));
    }

    [Fact]
    public async Task A_withdrawal_keeps_its_memories_until_it_is_final_and_then_removes_every_one()
    {
        // Withdrawal is reversible, so memories survive a cancel; sending a new message makes it final.
        for (var i = 0; i < 3; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = $"第 {i} 件事",
            });

            var seq = _context.Session.LastSeq;
            await _context.Memory.WriteAsync(new MemoryRecord
            {
                RecordId = $"m-{seq:x}",
                Tier = MemoryTier.Working,
                Scope = MemoryScope.Recall,
                Body = $"第 {i} 件事的细节",
                SourceEventSeq = seq,
            });
        }

        var withdrawn = await _http.PostAsync("/api/session/1/withdraw", null);
        Assert.True(withdrawn.IsSuccessStatusCode);
        Assert.Equal(3, (await _http.GetFromJsonAsync<JsonElement>("/api/memory")).GetArrayLength());

        Assert.True((await _http.PostAsync("/api/session/withdrawal/cancel", null)).IsSuccessStatusCode);
        Assert.Equal(3, (await _http.GetFromJsonAsync<JsonElement>("/api/memory")).GetArrayLength());

        await _http.PostAsync("/api/session/1/withdraw", null);
        var sent = await _http.PostAsJsonAsync("/api/input", new { source = "Text", payload = "换个话题" });
        Assert.True(sent.IsSuccessStatusCode);

        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/memory")).EnumerateArray());
        Assert.Empty(await _context.Memory.RecallAsync("细节"));
    }
}

/// <summary>
/// The maintenance pass over the real workspace: what a host that nobody talks to for a week still runs, on
/// the same store the views read.
/// </summary>
public sealed class MemoryMaintenanceTests : ApiFixture
{
    protected override string WorkspaceName => "partner-memory-maintenance";

    [Fact]
    public async Task The_pass_runs_over_the_workspace_store_and_reports_its_counts()
    {
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(200);

        await _context.Memory.WriteAsync(new MemoryRecord
        {
            RecordId = "idle-1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "很久没被回忆的旧事实",
            CreatedAt = longAgo,
        });

        var maintenance = new MemoryMaintenanceService(_context.Memory);
        var run = await maintenance.RunAsync();

        // A count of one shows the workspace store was actually walked rather than skipped.
        Assert.Equal(1, run.Demoted);
        Assert.Equal(0, run.Promoted);
        Assert.Equal(0, run.Trimmed);
        Assert.Equal(0, run.Compacted);
        Assert.Equal(MemoryTier.Episodic, (await _context.Memory.GetAsync("idle-1"))!.Tier);

        maintenance.Stop();
        await maintenance.DisposeAsync();
    }
}
