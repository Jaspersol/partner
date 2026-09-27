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
