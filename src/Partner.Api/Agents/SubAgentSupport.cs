using Partner.Api.Settings;
using Partner.Core.Agent.Files;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Web;
using Partner.Core.Agent.Tools;
using Partner.Core.Memory;
using Partner.Desktop.Tools;

namespace Partner.Api.Agents;

/// <summary>A model ready to use: the client, how it counts, and how much room it has.</summary>
/// <param name="Owned">Disposed when the job ends. Set when the binding built its own HTTP client.</param>
public sealed record ModelBinding(
    ILlmClient Llm,
    ITokenCounter Tokens,
    int ContextTokens,
    int MaxTokens,
    bool SupportsImages,
    IDisposable? Owned = null);

/// <summary>
/// The tool table a job runs with, reporting which tool is running so the job can say what it is doing. The
/// tools themselves are untouched; each is wrapped for the length of one call.
/// </summary>
internal sealed class ObservedToolRegistry(IToolRegistry inner, Action<string?> onActivity) : IToolRegistry
{
    public IReadOnlyList<ToolSpec> Specs => inner.Specs;

    public ITool? Find(string name) => inner.Find(name) is { } tool ? new Observed(tool, onActivity) : null;

    public void Register(ITool tool) => inner.Register(tool);

    private sealed class Observed(ITool tool, Action<string?> onActivity) : ITimedTool
    {
        public ToolSpec Spec => tool.Spec;

        // The inner tool's own budget when it has one; zero never extends anything.
        public TimeSpan Timeout => tool is ITimedTool timed ? timed.Timeout : TimeSpan.Zero;

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            onActivity(tool.Spec.Name);
            try
            {
                return await tool.InvokeAsync(argumentsJson, ct).ConfigureAwait(false);
            }
            finally
            {
                onActivity(null);
            }
        }
    }
}

/// <summary>
/// Approval requests that say who is asking. A sub-agent has no screen of its own, so the question reaches the
/// person through the same banner as everything else — and has to name the job, or it is anonymous.
/// </summary>
internal sealed class OriginApprovalHandler(QueuedApprovalHandler inner, string origin) : IApprovalHandler
{
    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default) =>
        inner.RequestAsync(toolName, argumentsJson, origin, ct);
}

/// <summary>
/// The desktop tools, registered the way the main agent used to register them and now only a desktop sub-agent
/// does. One place, so the tool set a job gets cannot drift from what the settings describe.
/// </summary>
internal static class DesktopToolSet
{
    public static void Register(InMemoryToolRegistry tools, ApiContext context, RuntimeSettings settings, bool supportsImages)
    {
        var policy = new DesktopToolPolicy(settings.Desktop.AllowedWindowTitles);
        tools.Register(new DesktopObserveTool(context.Desktop, policy));

        // Only for a model that takes pictures: a screenshot handed to a text-only model fails the request.
        // "That model" is the sub-agent's own — which is the point of letting it have one.
        if (supportsImages)
        {
            tools.Register(new DesktopScreenshotTool(context.Desktop, context.Media));
        }

        tools.Register(new DesktopActTool(context.Desktop, policy, context.Media, supportsImages: supportsImages));

        tools.Register(new DesktopWindowTool(context.Desktop, policy));

        // Waiting is read-only and self-bounded (≤5 s): a slow page spends seconds, not steps.
        tools.Register(new DesktopWaitTool(context.Desktop));

        // The automation's own launch tool, so the port it opens is one the observation can read.
        tools.Register(context.Desktop.LaunchTool ?? new DesktopLaunchTool());
        tools.Register(new DesktopExecTool());
        tools.Register(new DesktopClipboardGetTool());
        tools.Register(new DesktopClipboardSetTool());
    }
}

/// <summary>The file tools and the whitelist they answer to.</summary>
internal static class FileToolSet
{
    public static void Register(InMemoryToolRegistry tools, RuntimeSettings settings, string workspace)
    {
        // The workspace holds the settings (and with them the model's API key), the session log and the memory
        // database. It is refused whatever the roots say.
        var policy = new FileAccessPolicy(settings.Files.Roots, deniedDirectories: [workspace]);
        tools.Register(new FileListTool(policy));
        tools.Register(new FileReadTool(policy));
    }
}

/// <summary>
/// The web tools. Both or neither: the reader and the searcher are one capability, and <c>web_fetch</c> without
/// <c>web_search</c> would leave the model guessing at addresses.
/// </summary>
internal static class WebToolSet
{
    public static void Register(InMemoryToolRegistry tools, ApiContext context)
    {
        tools.Register(new WebSearchTool(context.Web));
        tools.Register(new WebFetchTool(new WebPageReader(context.Web.Transport)));
    }
}
