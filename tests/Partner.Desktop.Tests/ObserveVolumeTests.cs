using System.Text.Json;
using System.Windows.Automation;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>What one desktop_observe may cost the context: a cap per window, filters, and short values.</summary>
public sealed class ObserveVolumeTests
{
    private static UiElement Element(string windowId, int index, string role, string name, string? value = null) =>
        new($"{windowId}:{index}", windowId, name, role, new PixelRect(0, 0, 10, 10), "digest") { Value = value };

    /// <summary>Every window reports sixty controls: alternating buttons and edits, the edits holding long text.</summary>
    private static DesktopAutomation Crowded() => new(walk: (_, windowId) =>
        (Enumerable.Range(0, 60)
            .Select(i => i % 2 == 0
                ? Element(windowId, i, "Button", $"按钮 {i}")
                : Element(windowId, i, "Edit", $"输入 {i}", new string('长', 500)))
            .ToList(),
            Array.Empty<AutomationElement>(),
            WalkStopReason.None));

    private static async Task<JsonElement> ObserveAsync(DesktopAutomation automation, string arguments)
    {
        var result = await new DesktopObserveTool(automation, new DesktopToolPolicy(Array.Empty<string>()))
            .InvokeAsync(arguments);
        Assert.True(result.Ok, result.Content);

        // The answer is JSON followed by optional notes; the JSON ends at the first line break.
        var json = result.Content.Split('\n')[0];
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task The_controls_listed_per_window_are_capped_and_the_rest_is_counted()
    {
        using var automation = Crowded();

        var answer = await ObserveAsync(automation, """{"all":true,"maxElements":10}""");

        foreach (var window in answer.GetProperty("entities").EnumerateArray())
        {
            Assert.Equal(10, window.GetProperty("elements").GetArrayLength());
            Assert.Equal(50, window.GetProperty("elementsMore").GetInt32());
        }
    }

    [Fact]
    public async Task Roles_and_names_narrow_the_controls()
    {
        using var automation = Crowded();

        var byRole = await ObserveAsync(automation, """{"all":true,"roles":["Edit"],"maxElements":200}""");
        foreach (var window in byRole.GetProperty("entities").EnumerateArray())
        {
            Assert.Equal(30, window.GetProperty("elements").GetArrayLength());
            Assert.All(window.GetProperty("elements").EnumerateArray(), e => Assert.Equal("Edit", e.GetProperty("role").GetString()));
        }

        var byName = await ObserveAsync(automation, """{"all":true,"nameContains":"按钮 4","maxElements":200}""");
        foreach (var window in byName.GetProperty("entities").EnumerateArray())
        {
            // 按钮 4, 40, 42, 44, 46, 48
            Assert.Equal(6, window.GetProperty("elements").GetArrayLength());
        }
    }

    [Fact]
    public async Task A_long_value_is_clipped()
    {
        using var automation = Crowded();

        var answer = await ObserveAsync(automation, """{"all":true,"roles":["Edit"],"maxElements":1}""");

        var value = answer.GetProperty("entities")[0].GetProperty("elements")[0].GetProperty("value").GetString()!;
        Assert.True(value.Length <= DesktopObserveTool.DesktopObserveFilter.ValueChars + 1);
        Assert.EndsWith("…", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_default_a_window_lists_at_most_forty_controls()
    {
        using var automation = Crowded();

        var answer = await ObserveAsync(automation, """{"all":true}""");

        Assert.All(
            answer.GetProperty("entities").EnumerateArray(),
            window => Assert.Equal(DesktopObserveTool.DesktopObserveFilter.DefaultMaxElements, window.GetProperty("elements").GetArrayLength()));
    }
}
