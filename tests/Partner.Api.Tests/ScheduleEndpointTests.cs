using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Partner.Core.Contracts;

namespace Partner.Api.Tests;

/// <summary>
/// The timed-tasks API: creating, listing, pausing and deleting, what it refuses, and that a task which comes
/// due reaches the same queue typed input uses.
/// </summary>
public sealed class ScheduleEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-schedule-tests";

    protected override string ReadinessPath => "/api/schedules";

    private async Task<JsonElement> CreateAsync(object body)
    {
        var response = await _http.PostAsJsonAsync("/api/schedules", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement[]> ListAsync()
    {
        var body = await _http.GetFromJsonAsync<JsonElement>("/api/schedules");
        return body.GetProperty("tasks").EnumerateArray().ToArray();
    }

    [Fact]
    public async Task A_new_task_is_listed_with_its_rule_and_next_time()
    {
        var created = await CreateAsync(new { kind = "cron", cron = "0 9 * * *", payload = "  早报  " });

        Assert.Equal("cron", created.GetProperty("kind").GetString());
        Assert.Equal("0 9 * * *", created.GetProperty("cron").GetString());
        Assert.Equal("早报", created.GetProperty("payload").GetString());
        Assert.Equal("active", created.GetProperty("status").GetString());
        Assert.True(created.GetProperty("nextAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        var listed = Assert.Single(await ListAsync());
        Assert.Equal(created.GetProperty("id").GetString(), listed.GetProperty("id").GetString());
    }

    [Fact]
    public async Task An_interval_and_a_one_off_time_are_both_accepted()
    {
        var interval = await CreateAsync(new { kind = "interval", intervalMinutes = 30, payload = "喝水" });
        Assert.Equal(30, interval.GetProperty("intervalMinutes").GetInt32());

        var at = DateTimeOffset.UtcNow.AddHours(2);
        var once = await CreateAsync(new { kind = "once", at, payload = "开会" });
        Assert.Equal("once", once.GetProperty("kind").GetString());
        Assert.Equal(at, once.GetProperty("at").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("{\"kind\":\"cron\",\"cron\":\"not a cron\",\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"cron\",\"cron\":\"\",\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"interval\",\"intervalMinutes\":0,\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"interval\",\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"once\",\"at\":\"2000-01-01T00:00:00Z\",\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"once\",\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"weekly\",\"payload\":\"x\"}")]
    [InlineData("{\"kind\":\"interval\",\"intervalMinutes\":5,\"payload\":\"   \"}")]
    public async Task A_bad_rule_or_an_empty_message_is_refused_and_stores_nothing(string json)
    {
        var response = await _http.PostAsync(
            "/api/schedules", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("Parameter '", error);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task A_message_over_the_limit_is_refused()
    {
        var response = await _http.PostAsJsonAsync("/api/schedules", new
        {
            kind = "interval",
            intervalMinutes = 5,
            payload = new string('x', Endpoints.ScheduleRules.MaxPayloadLength + 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pausing_and_resuming_change_the_status_and_an_unknown_id_is_404()
    {
        var id = (await CreateAsync(new { kind = "interval", intervalMinutes = 60, payload = "x" }))
            .GetProperty("id").GetString();

        var paused = await _http.PostAsync($"/api/schedules/{id}/pause", null);
        Assert.Equal("inactive", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var resumed = await _http.PostAsync($"/api/schedules/{id}/resume", null);
        Assert.Equal("active", (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/schedules/nope/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/schedules/nope/resume", null)).StatusCode);
    }

    [Fact]
    public async Task Deleting_removes_the_task_and_a_second_delete_is_404()
    {
        var id = (await CreateAsync(new { kind = "interval", intervalMinutes = 60, payload = "x" }))
            .GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.OK, (await _http.DeleteAsync($"/api/schedules/{id}")).StatusCode);
        Assert.Empty(await ListAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/api/schedules/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_task_count_is_capped()
    {
        for (var i = 0; i < Endpoints.ScheduleRules.MaxTasks; i++)
        {
            await CreateAsync(new { kind = "interval", intervalMinutes = 60, payload = $"t{i}" });
        }

        var over = await _http.PostAsJsonAsync("/api/schedules", new { kind = "interval", intervalMinutes = 60, payload = "one too many" });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
    }

    [Fact]
    public async Task A_due_task_reaches_the_input_queue_as_schedule_input_and_a_one_off_then_goes_quiet()
    {
        // A real driver on the real clock: this proves the host starts the loop and that the loop's output lands
        // on the queue, not the clock arithmetic (the Core tests own that). The time is the nearest the API will
        // accept, which is "after now".
        var at = DateTimeOffset.UtcNow.AddMilliseconds(400);
        var created = await CreateAsync(new { kind = "once", at, payload = "到点了" });
        var id = created.GetProperty("id").GetString();

        var arrived = false;
        for (var attempt = 0; attempt < 80 && !arrived; attempt++)
        {
            await Task.Delay(50);
            arrived = _context.Queue.ActiveItems()
                .Any(i => i.Source == InputSource.Schedule && i.Payload == "到点了");
        }

        Assert.True(arrived, "the due task should have been enqueued as Schedule input");

        var task = Assert.Single(await ListAsync());
        Assert.Equal(id, task.GetProperty("id").GetString());
        Assert.Equal("inactive", task.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("nextAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, task.GetProperty("lastAt").ValueKind);
    }
}
