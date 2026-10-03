using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Partner.Api.Auth;
using Partner.Api.Voice;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;

namespace Partner.Api.Tests;

/// <summary>
/// The tool-failure summary over HTTP.
/// <para>
/// Its own fixture because the summary lives on the consumer host, and the fixture that watches a live row
/// parks a turn on a gate — reconfiguring that consumer would deadlock (see README §必须知道的坑).
/// </para>
/// </summary>
public sealed class ToolFailureEndpointTests : ApiFixture
{
    private readonly FailingToolLlm _llm = new("flaky");

    protected override string WorkspaceName => "partner-failure-tests";

    protected override ILlmClient Llm => _llm;

    /// <summary>Calls one tool, which fails, then answers.</summary>
    private sealed class FailingToolLlm(string tool) : ILlmClient
    {
        private int _calls;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            _calls++;

            if (_calls == 1)
            {
                return Task.FromResult(new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c1", tool, "{}") },
                    "tool_calls"));
            }

            return Task.FromResult(new LlmResponse("done", Array.Empty<LlmToolCall>(), "stop"));
        }
    }

    private async Task<JsonElement> WaitForFailuresAsync(Func<JsonElement, bool> ready)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var summary = await _http.GetFromJsonAsync<JsonElement>("/api/tools/failures");
            if (ready(summary))
            {
                return summary;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("the failure summary never reached the expected state");
    }

    [Fact]
    public async Task An_empty_summary_is_an_empty_list_not_a_missing_field()
    {
        // Before anything has failed. A client that has to handle "the key might be absent" is a client that
        // will eventually render nothing where it should have rendered zero.
        var summary = await _http.GetFromJsonAsync<JsonElement>("/api/tools/failures");

        Assert.Empty(summary.GetProperty("failures").EnumerateArray());
        Assert.Equal(0, summary.GetProperty("totals").GetProperty("occurrences").GetInt32());
    }

    [Fact]
    public async Task A_failing_tool_shows_up_with_its_count()
    {
        // The whole endpoint: something the model did not manage, counted, on the way out.
        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "试试" });

        var summary = await WaitForFailuresAsync(s => s.GetProperty("totals").GetProperty("occurrences").GetInt32() > 0);

        var row = summary.GetProperty("failures").EnumerateArray().First();
        Assert.False(string.IsNullOrEmpty(row.GetProperty("tool").GetString()));
        Assert.True(row.GetProperty("count").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(row.GetProperty("reason").GetString()));

        // "Last seen" is what tells a reader the problem is current rather than resolved.
        Assert.False(string.IsNullOrEmpty(row.GetProperty("lastSeen").GetString()));
    }
}
