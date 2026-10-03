using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// The UI's delivery entry point (design doc §2.3.1). What it promises is narrow and worth pinning down: the
/// item lands in the single FIFO queue, with the caller's source, and nothing executes it here.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    [Fact]
    public async Task Submitting_input_enqueues_it_with_the_given_source()
    {
        var response = await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "turn on the lamp" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("seq").GetInt64() > 0);
        Assert.False(body.GetProperty("deduplicated").GetBoolean());

        // It is in the queue, waiting — not in the session, and not done.
        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        var item = queue.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Queued", item.GetProperty("status").GetString());
        Assert.Equal("Text", item.GetProperty("source").GetString());
        Assert.Equal("turn on the lamp", item.GetProperty("payload").GetString());
        Assert.Equal(body.GetProperty("itemId").GetString(), item.GetProperty("itemId").GetString());

        var session = await _http.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Empty(session.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("schedule")]
    public async Task Submitting_input_accepts_every_source_the_runtime_defines(string source)
    {
        var response = await _http.PostAsJsonAsync("/api/input", new { source, payload = "x" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Submitting_input_rejects_an_unknown_source()
    {
        var response = await _http.PostAsJsonAsync("/api/input", new { source = "telepathy", payload = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Empty(queue.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Submitting_input_rejects_an_empty_payload()
    {
        var response = await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Empty(queue.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_supplied_idempotency_key_makes_a_retry_land_on_the_same_item()
    {
        var first = await _http.PostAsJsonAsync(
            "/api/input",
            new { source = "text", payload = "once", idempotencyKey = "composer:1" });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();

        var retry = await _http.PostAsJsonAsync(
            "/api/input",
            new { source = "text", payload = "once", idempotencyKey = "composer:1" });
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(firstBody.GetProperty("deduplicated").GetBoolean());
        Assert.True(retryBody.GetProperty("deduplicated").GetBoolean());
        Assert.Equal(firstBody.GetProperty("seq").GetInt64(), retryBody.GetProperty("seq").GetInt64());

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Single(queue.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Two_sends_without_a_key_are_two_items()
    {
        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "first" });
        await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "second" });

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Equal(2, queue.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_submitted_gear_rides_the_queued_item()
    {
        // The gear is chosen per message, so it has to survive the hop through the queue: the consumer reads
        // it off the item, not off whatever the settings happened to say by the time the turn ran.
        var response = await _http.PostAsJsonAsync(
            "/api/input",
            new { source = "text", payload = "think harder", reasoningEffort = "high" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Equal("high", queue.GetProperty("items")[0].GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public async Task Submitting_input_without_a_gear_leaves_it_unset()
    {
        // Unset is not the default gear: the default is applied when the request is built, so an item can
        // say "no choice" and still get the configured gear.
        var response = await _http.PostAsJsonAsync("/api/input", new { source = "text", payload = "hi" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Equal(JsonValueKind.Null, queue.GetProperty("items")[0].GetProperty("reasoningEffort").ValueKind);
    }

    [Fact]
    public async Task Submitting_input_rejects_a_gear_that_is_not_on_the_menu()
    {
        // Rejected rather than passed through: the picker only ever offers listed gears, so anything else
        // is a stale or hand-rolled client, and accepting it would hide the mismatch.
        var response = await _http.PostAsJsonAsync(
            "/api/input",
            new { source = "text", payload = "x", reasoningEffort = "turbo" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("turbo", await response.Content.ReadAsStringAsync());

        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.Empty(queue.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_gear_the_menu_grew_is_accepted()
    {
        // The list is the user's, not a built-in enum — an endpoint with its own word must be usable without
        // a code change.
        var settings = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { reasoningGears = new[] { "medium", "high", "minimal" } },
        });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        var response = await _http.PostAsJsonAsync(
            "/api/input",
            new { source = "text", payload = "x", reasoningEffort = "minimal" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
