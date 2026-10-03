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
        // The leak this closes: the person asked for it gone, and without the cascade it would still be
        // recalled — and injected — into a conversation weeks later where they never said it.
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

        // Gone from the view, and gone from recall — the second is the part that matters.
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/memory")).EnumerateArray());
        Assert.Empty(await _context.Memory.RecallAsync("内网账号"));
    }

    [Fact]
    public async Task Retracting_a_range_removes_every_memory_inside_it()
    {
        // A retraction empties the conversation; what it remembered is part of that conversation.
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

        Assert.Equal(3, (await _http.GetFromJsonAsync<JsonElement>("/api/memory")).GetArrayLength());

        var retracted = await _http.PostAsync($"/api/session/1/retract", null);
        Assert.True(retracted.IsSuccessStatusCode);

        var body = await retracted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("purgedMemories").GetInt32());
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/memory")).EnumerateArray());
    }
}
