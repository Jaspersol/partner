using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// The desktop capability over HTTP. These checks stay desktop-independent on purpose: the parts that need a
/// real session (observing windows, the model's tool call) are covered by the smoke, which already assumes one.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    [Fact]
    public async Task The_desktop_capability_is_off_until_it_is_switched_on()
    {
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/desktop");

        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(15000, state.GetProperty("leaseTtlMs").GetInt32());
        Assert.Null(state.GetProperty("lastObsId").GetString());
    }

    [Fact]
    public async Task Observing_is_refused_while_the_capability_is_off()
    {
        // Fail-closed: the default must not be "enumerate the user's windows because nobody said no".
        var response = await _http.PostAsync("/api/desktop/observe", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("未启用", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acting_is_refused_while_the_capability_is_off()
    {
        var response = await _http.PostAsJsonAsync("/api/desktop/act", new
        {
            entityId = "1a2b",
            kind = "LeftClick",
            x = 1,
            y = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Enabling_it_is_reported_back_and_survives_a_reload()
    {
        await _http.PutAsJsonAsync("/api/settings", new
        {
            desktop = new { enabled = true, leaseTtlMs = 4000, allowedWindowTitles = new[] { "记事本" } },
        });

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/desktop");
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(4000, state.GetProperty("leaseTtlMs").GetInt32());
        Assert.Contains(
            "记事本",
            state.GetProperty("allowedWindowTitles").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task The_settings_response_carries_the_section_the_ui_refills_from()
    {
        // The UI refills its form from this response. When the section was missing, saving flipped the switch
        // back to off — the setting was stored, but the answer to the save said otherwise.
        var saved = await _http.PutAsJsonAsync("/api/settings", new
        {
            desktop = new { enabled = true, leaseTtlMs = 7000, allowedWindowTitles = new[] { "记事本" } },
        });

        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var desktop = body.GetProperty("settings").GetProperty("desktop");
        Assert.True(desktop.GetProperty("enabled").GetBoolean());
        Assert.Equal(7000, desktop.GetProperty("leaseTtlMs").GetInt32());

        // And the plain read agrees, which is what the settings page loads on open.
        var read = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(read.GetProperty("desktop").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Toggling_it_changes_what_the_model_can_call()
    {
        // The tool table is built when the consumer starts, and /api/consumer reports it — so this observes the
        // restart that makes the switch mean something, rather than asserting that a request returned 200.
        var off = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        Assert.Empty(off.GetProperty("tools").EnumerateArray());

        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = true, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var on = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        var tools = on.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() + ":" + tool.GetProperty("decision").GetString())
            .ToList();

        // Looking is allowed outright; acting always asks a human.
        Assert.Contains("desktop_observe:allow", tools);
        Assert.Contains("desktop_act:ask", tools);

        // And switching it off takes them away again.
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = false, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var offAgain = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
        Assert.Empty(offAgain.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task The_gear_decides_which_tools_ask()
    {
        // Observable in /api/consumer: the tool table is what the model can call and what governance will do
        // about each one. A gear that changed nothing here would be a switch that does nothing.
        async Task<List<string>> ToolsAsync()
        {
            var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
            return state.GetProperty("tools").EnumerateArray()
                .Select(tool => $"{tool.GetProperty("name").GetString()}:{tool.GetProperty("decision").GetString()}")
                .ToList();
        }

        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var all = await ToolsAsync();
        Assert.Contains("desktop_observe:allow", all);
        Assert.Contains("desktop_clipboard_get:allow", all);
        Assert.Contains("desktop_act:ask", all);
        Assert.Contains("desktop_exec:ask", all);

        // The middle gear: what a person can watch happen goes through, what leaves the GUI waits.
        var middle = await _http.PostAsJsonAsync("/api/desktop/approval", new { approval = "dangerous" });
        Assert.Equal(HttpStatusCode.OK, middle.StatusCode);

        var dangerous = await ToolsAsync();
        Assert.Contains("desktop_act:allow", dangerous);
        Assert.Contains("desktop_clipboard_get:allow", dangerous);
        Assert.Contains("desktop_exec:ask", dangerous);
        Assert.Contains("desktop_launch:ask", dangerous);
        Assert.Contains("desktop_clipboard_set:ask", dangerous);

        // And the gear the model was explicitly allowed to run unattended.
        await _http.PostAsJsonAsync("/api/desktop/approval", new { approval = "none" });

        var none = await ToolsAsync();
        Assert.All(none, tool => Assert.EndsWith(":allow", tool, StringComparison.Ordinal));
        Assert.Equal(6, none.Count);

        // An unknown gear is refused rather than silently stored.
        var bogus = await _http.PostAsJsonAsync("/api/desktop/approval", new { approval = "whatever" });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
    }

    [Fact]
    public async Task The_screenshot_tool_exists_only_for_a_model_that_takes_pictures()
    {
        // Sending an image to a text-only model fails the request, so the tool is registered from the claim
        // about the model — not always, and not never.
        async Task<List<string>> ToolsAsync()
        {
            var state = await _http.GetFromJsonAsync<JsonElement>("/api/consumer");
            return state.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!)
                .ToList();
        }

        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", supportsImages = false },
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        Assert.DoesNotContain("desktop_screenshot", await ToolsAsync());

        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m", supportsImages = true },
            desktop = new { enabled = true, approval = "all", leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var withImages = await ToolsAsync();
        Assert.Contains("desktop_screenshot", withImages);
        // Looking is free whatever the gear says.
        var screenshot = (await _http.GetFromJsonAsync<JsonElement>("/api/consumer"))
            .GetProperty("tools").EnumerateArray()
            .First(tool => tool.GetProperty("name").GetString() == "desktop_screenshot");
        Assert.Equal("allow", screenshot.GetProperty("decision").GetString());

        // And the setting is reported back, or the checkbox would forget itself on the next save.
        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(settings.GetProperty("model").GetProperty("supportsImages").GetBoolean());
    }

    [Fact]
    public async Task An_empty_approval_queue_is_reported_rather_than_hidden()
    {
        // The UI has to distinguish "nothing is waiting" from "we could not ask", so the shape is always there.
        var queue = await _http.GetFromJsonAsync<JsonElement>("/api/approvals");

        Assert.True(queue.GetProperty("timeoutSeconds").GetDouble() > 0);
        Assert.Empty(queue.GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task Answering_an_unknown_approval_is_not_found()
    {
        var response = await _http.PostAsJsonAsync("/api/approvals/ap-does-not-exist", new { approved = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
