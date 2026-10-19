using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// The agents section over HTTP: the four rules, the write-only key, and the fact that saving one page does not
/// erase what the other wrote.
/// </summary>
public sealed class AgentsEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-agents-tests";

    protected override string ReadinessPath => "/api/agents";

    private static object Desktop(bool enabled = true, string? prompt = null, object? model = null) => new
    {
        id = "desktop",
        name = "桌面操控",
        enabled,
        scenario = "desktop",
        prompt,
        maxRounds = 5,
        model,
    };

    private static object Reader(string scenario = "file-read", string id = "", string? prompt = null, object? model = null) => new
    {
        id,
        name = "文件阅读",
        enabled = true,
        scenario,
        prompt,
        maxRounds = 5,
        model,
    };

    private Task<HttpResponseMessage> PutAgentsAsync(string? mainPrompt, params object[] subAgents) =>
        _http.PutAsJsonAsync("/api/agents", new { main = new { prompt = mainPrompt }, subAgents });

    private static async Task<List<string>> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")
            .EnumerateArray().Select(error => error.GetString()!).ToList();

    [Fact]
    public async Task A_fresh_install_has_the_builtin_desktop_agent_and_the_whole_scenario_catalogue()
    {
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/agents");

        var agent = Assert.Single(view.GetProperty("subAgents").EnumerateArray());
        Assert.Equal("desktop", agent.GetProperty("id").GetString());
        Assert.True(agent.GetProperty("builtin").GetBoolean());
        Assert.Equal(5, agent.GetProperty("maxRounds").GetInt32());
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("model").ValueKind);

        // Every scenario is offered whether or not an agent holds it: an unclaimed one belongs to the main agent.
        var scenarios = view.GetProperty("scenarios").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "desktop", "file-read", "web-search" },
            scenarios.Select(s => s.GetProperty("key").GetString()));

        Assert.Equal("desktop", scenarios[0].GetProperty("ownerId").GetString());
        Assert.Equal(JsonValueKind.Null, scenarios[1].GetProperty("ownerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, scenarios[2].GetProperty("ownerId").ValueKind);
    }

    [Fact]
    public async Task The_builtin_agent_cannot_be_removed_or_pointed_elsewhere()
    {
        var removed = await PutAgentsAsync(null);
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
        Assert.Contains(await ErrorsAsync(removed), error => error.Contains("不能删除", StringComparison.Ordinal));

        var moved = await PutAgentsAsync(null, new
        {
            id = "desktop", name = "桌面操控", enabled = true, scenario = "file-read", maxRounds = 5,
        });
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
    }

    [Fact]
    public async Task One_scenario_has_one_agent()
    {
        var taken = await PutAgentsAsync(null, Desktop(), Reader(scenario: "desktop"));

        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
        Assert.Contains(await ErrorsAsync(taken), error => error.Contains("只能有一个子 agent", StringComparison.Ordinal));

        var twice = await PutAgentsAsync(null, Desktop(), Reader(id: "a"), Reader(id: "b"));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
    }

    [Fact]
    public async Task A_disabled_agent_still_holds_its_scenario()
    {
        var response = await PutAgentsAsync(null, Desktop(), new
        {
            id = "r1", name = "甲", enabled = false, scenario = "file-read", maxRounds = 5,
        }, new
        {
            id = "r2", name = "乙", enabled = true, scenario = "file-read", maxRounds = 5,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_scenario_outside_the_closed_list_is_refused()
    {
        var response = await PutAgentsAsync(null, Desktop(), Reader(scenario: "weather"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ErrorsAsync(response), error => error.Contains("weather", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_agent_gets_an_id_from_the_server()
    {
        var response = await PutAgentsAsync(null, Desktop(), Reader());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var agents = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agents").GetProperty("subAgents");
        var added = agents.EnumerateArray().Single(agent => agent.GetProperty("scenario").GetString() == "file-read");
        Assert.False(string.IsNullOrWhiteSpace(added.GetProperty("id").GetString()));
        Assert.False(added.GetProperty("builtin").GetBoolean());
    }

    [Fact]
    public async Task Desktop_control_cannot_be_switched_on_without_its_agent()
    {
        var disabled = await PutAgentsAsync(null, Desktop(enabled: false));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        var on = await _http.PutAsJsonAsync("/api/settings", new
        {
            desktop = new { enabled = true, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, on.StatusCode);
        Assert.Contains(await ErrorsAsync(on), error => error.Contains("必须有一个启用的桌面操控子 agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_desktop_agent_cannot_be_disabled_while_desktop_control_is_on()
    {
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            desktop = new { enabled = true, leaseTtlMs = 15000, allowedWindowTitles = Array.Empty<string>() },
        });

        var response = await PutAgentsAsync(null, Desktop(enabled: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ErrorsAsync(response), error => error.Contains("桌面操控已开启", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reading_files_needs_a_directory_that_exists()
    {
        var none = await _http.PutAsJsonAsync("/api/settings", new { files = new { enabled = true, roots = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains(await ErrorsAsync(none), error => error.Contains("没有配置允许读取的目录", StringComparison.Ordinal));

        var missing = await _http.PutAsJsonAsync("/api/settings", new
        {
            files = new { enabled = true, roots = new[] { Path.Combine(_workspace, "no-such-dir") } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var fine = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            files = new { enabled = true, roots = new[] { _workspace } },
        });
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
    }

    [Fact]
    public async Task A_directory_that_has_gone_away_is_a_problem_not_a_reason_to_refuse_other_saves()
    {
        var drive = Path.Combine(Path.GetTempPath(), "partner-agents-drive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(drive);
        var enabled = await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            files = new { enabled = true, roots = new[] { drive } },
        });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        // The drive is unplugged. Saving something unrelated must still work...
        Directory.Delete(drive);
        // (The settings page sends the whole document, files section included.)
        var unrelated = await _http.PutAsJsonAsync("/api/settings", new
        {
            output = new { speed = 1.2 },
            files = new { enabled = true, roots = new[] { drive } },
        });
        Assert.Equal(HttpStatusCode.OK, unrelated.StatusCode);

        // ...and the page says what is wrong instead of failing silently.
        var view = await _http.GetFromJsonAsync<JsonElement>("/api/agents");
        Assert.Contains(
            view.GetProperty("problems").EnumerateArray().Select(p => p.GetString()),
            problem => problem!.Contains("不存在", StringComparison.Ordinal));

        // A *new* entry that does not exist is still a typo and is refused.
        var typo = await _http.PutAsJsonAsync("/api/settings", new
        {
            files = new { enabled = true, roots = new[] { drive, Path.Combine(drive, "typo") } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, typo.StatusCode);
    }

    [Fact]
    public async Task The_round_count_and_the_prompt_have_limits()
    {
        var rounds = await PutAgentsAsync(null, new
        {
            id = "desktop", name = "桌面操控", enabled = true, scenario = "desktop", maxRounds = 11,
        });
        Assert.Equal(HttpStatusCode.BadRequest, rounds.StatusCode);

        var zero = await PutAgentsAsync(null, new
        {
            id = "desktop", name = "桌面操控", enabled = true, scenario = "desktop", maxRounds = 0,
        });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        var huge = await PutAgentsAsync(new string('字', 20_000), Desktop());
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Contains(await ErrorsAsync(huge), error => error.Contains("主 agent 的提示词过长", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_prompt_that_is_only_whitespace_means_the_default()
    {
        await PutAgentsAsync("   \n ", Desktop(prompt: "  "));

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/agents");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("main").GetProperty("prompt").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("subAgents")[0].GetProperty("prompt").ValueKind);
        Assert.Equal("You are a single-user local assistant.", view.GetProperty("main").GetProperty("effectivePrompt").GetString());
    }

    [Fact]
    public async Task The_effective_prompts_show_the_parts_a_person_cannot_edit()
    {
        await PutAgentsAsync("我是自定义主提示词", Desktop(prompt: "我是自定义桌面提示词"));

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/agents");
        var sub = view.GetProperty("subAgents")[0].GetProperty("effectivePrompt").GetString()!;

        Assert.StartsWith("我是自定义桌面提示词", sub, StringComparison.Ordinal);
        Assert.Contains("task_report", sub, StringComparison.Ordinal);
        Assert.Contains("最多有 5 轮", sub, StringComparison.Ordinal);
        Assert.Equal("我是自定义主提示词", view.GetProperty("main").GetProperty("effectivePrompt").GetString());
    }

    [Fact]
    public async Task A_sub_agents_api_key_is_write_only_and_survives_other_edits()
    {
        var model = new
        {
            baseUrl = "http://127.0.0.1:9/v1", model = "vision", apiKey = "sk-secret-123",
            timeoutSeconds = 60, contextTokens = 16000, maxTokens = 2000, supportsImages = true, reasoningEffort = "off",
        };
        var saved = await PutAgentsAsync(null, Desktop(model: model));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var raw = await (await _http.GetAsync("/api/agents")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-secret-123", raw, StringComparison.Ordinal);
        var shown = JsonDocument.Parse(raw).RootElement.GetProperty("subAgents")[0].GetProperty("model");
        Assert.True(shown.GetProperty("apiKeySet").GetBoolean());
        Assert.False(shown.TryGetProperty("apiKey", out _));
        Assert.DoesNotContain("sk-secret-123", await (await _http.GetAsync("/api/settings")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Editing something else, with the key left out, keeps it.
        var withoutKey = new
        {
            baseUrl = "http://127.0.0.1:9/v1", model = "vision2",
            timeoutSeconds = 60, contextTokens = 16000, maxTokens = 2000, supportsImages = true, reasoningEffort = "off",
        };
        await PutAgentsAsync(null, Desktop(model: withoutKey));
        Assert.Equal("sk-secret-123", _context.Settings.Load().Agents.SubAgents[0].Model!.ApiKey);

        // An empty string clears it.
        await PutAgentsAsync(null, Desktop(model: new
        {
            baseUrl = "http://127.0.0.1:9/v1", model = "vision2", apiKey = "",
            timeoutSeconds = 60, contextTokens = 16000, maxTokens = 2000, supportsImages = true, reasoningEffort = "off",
        }));
        Assert.Null(_context.Settings.Load().Agents.SubAgents[0].Model!.ApiKey);

        // Back to following the main model.
        await PutAgentsAsync(null, Desktop(model: null));
        Assert.Null(_context.Settings.Load().Agents.SubAgents[0].Model);
    }

    [Fact]
    public async Task A_custom_model_has_to_be_complete()
    {
        var response = await PutAgentsAsync(null, Desktop(model: new { baseUrl = "", model = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await ErrorsAsync(response), error => error.Contains("模型地址", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_the_settings_page_does_not_erase_the_agents_or_the_file_whitelist()
    {
        await PutAgentsAsync("主提示词", Desktop(), Reader(prompt: "读文件的提示词"));
        await _http.PutAsJsonAsync("/api/settings", new
        {
            model = new { baseUrl = "http://127.0.0.1:9/v1", model = "m" },
            files = new { enabled = true, roots = new[] { _workspace } },
        });

        // What the settings page does: read the document, change nothing it owns, write the whole thing back.
        var document = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        var back = await _http.PutAsJsonAsync("/api/settings", document);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);

        var after = _context.Settings.Load();
        Assert.Equal("主提示词", after.Agents.Main.Prompt);
        Assert.Equal(2, after.Agents.SubAgents.Count);
        Assert.Equal("读文件的提示词", after.Agents.SubAgents.Single(agent => agent.Scenario == "file-read").Prompt);
        Assert.True(after.Files.Enabled);
        Assert.Equal(new[] { _workspace }, after.Files.Roots);
    }

    [Fact]
    public async Task The_input_endpoint_refuses_the_agent_source()
    {
        var response = await _http.PostAsJsonAsync("/api/input", new { source = "Agent", payload = "[子 agent「桌面操控」任务 x · 已完成]" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        Assert.DoesNotContain("Agent", error, StringComparison.Ordinal);
    }
}
