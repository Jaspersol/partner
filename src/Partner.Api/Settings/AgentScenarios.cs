using Partner.Core.Agent;

namespace Partner.Api.Settings;

/// <summary>
/// One thing the main agent may hand to a sub-agent.
/// </summary>
/// <param name="Key">The value stored in settings and passed as <c>scenario</c> to <c>agent_delegate</c>.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="When">Which requests belong to it — written for the main agent, who decides by reading it.</param>
/// <param name="DefaultPrompt">The prompt a sub-agent on this scenario starts from.</param>
public sealed record AgentScenario(string Key, string Name, string When, string DefaultPrompt);

/// <summary>
/// The closed list of scenarios. There are exactly three: each names a capability with its own switch, its own
/// tools and its own safety story, so a free-form scenario would promise a tool set nobody has built.
/// <para>
/// Names and descriptions live here and are not editable: the main agent reads them to decide whether to
/// delegate, so they must not drift from the scenario table.
/// </para>
/// </summary>
public static class AgentScenarios
{
    public const string Desktop = "desktop";
    public const string FileRead = "file-read";
    public const string WebSearch = "web-search";

    public static IReadOnlyList<AgentScenario> All { get; } =
    [
        new(
            Desktop,
            "桌面操控",
            "需要看屏幕、点击、输入、打开或切换程序，或在应用和网页里完成多步界面操作时",
            AgentPrompts.DesktopDefault),
        new(
            FileRead,
            "读取文件",
            "需要在电脑上查找、阅读文件或目录内容，并把要点整理出来时（只读）",
            AgentPrompts.FileReadDefault),
        new(
            WebSearch,
            "联网搜索",
            "需要这台电脑上没有的、必须联网查证的信息时：最新版本、时事新闻、别人的做法、某个说法是否属实",
            AgentPrompts.WebSearchDefault),
    ];

    public static AgentScenario? Find(string? key) =>
        All.FirstOrDefault(scenario => string.Equals(scenario.Key, key, StringComparison.Ordinal));
}
