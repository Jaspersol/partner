using System.Text;
using System.Text.Json;
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
    private readonly object _gate = new();
    private readonly LinkedList<string> _active = new();

    public IReadOnlyList<ToolSpec> Specs => inner.Specs;

    public ITool? Find(string name) => inner.Find(name) is { } tool
        ? tool is IPreparedFileWriteTool write ? new ObservedWrite(write, this) : new Observed(tool, this)
        : null;

    public void Register(ITool tool) => inner.Register(tool);

    private LinkedListNode<string> Start(string name)
    {
        lock (_gate)
        {
            var node = _active.AddLast(name);
            onActivity(name);
            return node;
        }
    }

    private void Stop(LinkedListNode<string> node)
    {
        lock (_gate)
        {
            _active.Remove(node);
            onActivity(_active.Last?.Value);
        }
    }

    private sealed class ObservedWrite(IPreparedFileWriteTool tool, ObservedToolRegistry registry) : IPreparedFileWriteTool
    {
        public ToolSpec Spec => tool.Spec;
        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) => tool.InvokeAsync(argumentsJson, ct);
        public IPreparedFileWrite Prepare(string argumentsJson, CancellationToken ct) =>
            new Operation(tool.Prepare(argumentsJson, ct), registry, tool.Spec.Name);

        private sealed class Operation(IPreparedFileWrite inner, ObservedToolRegistry registry, string name) : IPreparedFileWrite
        {
            public string TargetPath => inner.TargetPath;
            public string ApprovalJson => inner.ApprovalJson;
            public async Task<ToolResult> CommitAsync(CancellationToken ct)
            {
                var activity = registry.Start(name);
                try { return await inner.CommitAsync(ct).ConfigureAwait(false); }
                finally { registry.Stop(activity); }
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class Observed(ITool tool, ObservedToolRegistry registry) : ITimedTool
    {
        public ToolSpec Spec => tool.Spec;

        // The inner tool's own budget when it has one; zero never extends anything.
        public TimeSpan Timeout => tool is ITimedTool timed ? timed.Timeout : TimeSpan.Zero;

        public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            var activity = registry.Start(tool.Spec.Name);
            try
            {
                return await tool.InvokeAsync(argumentsJson, ct).ConfigureAwait(false);
            }
            finally
            {
                registry.Stop(activity);
            }
        }
    }
}

/// <summary>
/// Approval requests that say who is asking. A sub-agent has no screen of its own, so the question reaches the
/// person through the same banner as everything else — and has to name the job, or it is anonymous.
/// </summary>
internal sealed class OriginApprovalHandler(QueuedApprovalHandler inner, string origin, string fileOrigin) : IApprovalHandler
{
    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default) =>
        inner.RequestAsync(toolName, argumentsJson, toolName is "file_write" or "file_edit" ? fileOrigin : origin, ct);
}

/// <summary>
/// The desktop tools a desktop sub-agent gets. One place, so the tool set a job gets cannot drift from what the
/// settings describe.
/// </summary>
internal static class DesktopToolSet
{
    public static void Register(InMemoryToolRegistry tools, ApiContext context, RuntimeSettings settings, bool supportsImages)
    {
        var policy = new DesktopToolPolicy(settings.Desktop.AllowedWindowTitles);
        Func<bool> isEnabled = () => context.Settings.Load().Desktop.Enabled;
        tools.Register(new DesktopObserveTool(context.Desktop, policy));

        // Only for a model that takes pictures: a screenshot handed to a text-only model fails the request.
        // "That model" is the sub-agent's own — which is the point of letting it have one.
        if (supportsImages)
        {
            tools.Register(new DesktopScreenshotTool(context.Desktop, context.Media));
        }

        tools.Register(new DesktopActTool(context.Desktop, policy, context.Media, supportsImages: supportsImages, isEnabled: isEnabled));

        tools.Register(new DesktopWindowTool(context.Desktop, policy, isEnabled));

        // Waiting is read-only and self-bounded (≤5 s): a slow page spends seconds, not steps.
        tools.Register(new DesktopWaitTool(context.Desktop));

        // The automation's own launch tool, so the port it opens is one the observation can read.
        tools.Register(new EnabledDesktopLaunchTool(context.Desktop.LaunchTool ?? new DesktopLaunchTool(), isEnabled));
        if (settings.Desktop.ExecEnabled)
        {
            tools.Register(new EnabledDesktopExecTool(context));
        }
        tools.Register(new DesktopClipboardGetTool());
        tools.Register(new DesktopClipboardSetTool(isEnabled));
    }
}

/// <summary>Rechecks the desktop capability inside the shared mutation lock.</summary>
internal sealed class EnabledDesktopLaunchTool(DesktopLaunchTool inner, Func<bool> isEnabled) : ITool
{
    public ToolSpec Spec => inner.Spec;

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        inner.InvokeWhenEnabledAsync(argumentsJson, isEnabled, ct);
}

/// <summary>Rechecks command permission immediately before starting the command.</summary>
internal sealed class EnabledDesktopExecTool(ApiContext context) : ITool
{
    private readonly DesktopExecTool _inner = new(isEnabled: () =>
    {
        var desktop = context.Settings.Load().Desktop;
        return desktop.Enabled && desktop.ExecEnabled;
    });

    public ToolSpec Spec => _inner.Spec;

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        _inner.InvokeAsync(argumentsJson, ct);
}

/// <summary>The file tools and the whitelist they answer to.</summary>
internal static class FileToolSet
{
    public static void Register(InMemoryToolRegistry tools, SettingsStore settings, RuntimeSettings snapshot, string workspace, string? agentId = null)
    {
        // The workspace holds secrets and session data and is refused regardless of the configured roots.
        var initialPolicy = new FileAccessPolicy(settings.Load().Files.Roots, deniedDirectories: [workspace]);
        tools.Register(new LiveFileTool(settings, workspace, agentId, new FileListTool(initialPolicy), p => new FileListTool(p)));
        tools.Register(new LiveFileTool(settings, workspace, agentId, new FileReadTool(initialPolicy), p => new FileReadTool(p)));
        if (AgentRouting.FileWriteAvailable(snapshot))
        {
            tools.Register(new LiveFileWriteTool(settings, workspace, agentId));
            tools.Register(new LiveFileEditTool(settings, workspace, agentId));
        }
    }

    private sealed class LiveFileTool(
        SettingsStore settings, string workspace, string? agentId, ITool prototype, Func<FileAccessPolicy, ITool> create) : ITool
    {
        public ToolSpec Spec => prototype.Spec;

        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        {
            var current = settings.Load();
            var allowed = agentId is null
                ? AgentRouting.MainOwnsFiles(current)
                : current.Files.Enabled && current.Agents.Owner(AgentScenarios.FileRead) is { Enabled: true } owner
                    && string.Equals(owner.Id, agentId, StringComparison.Ordinal);
            if (!allowed)
                return Task.FromResult(new ToolResult(false, "文件读取已关闭或权限已转移"));

            var policy = new FileAccessPolicy(current.Files.Roots, deniedDirectories: [workspace]);
            return create(policy).InvokeAsync(argumentsJson, ct);
        }
    }

    internal static bool CanWrite(RuntimeSettings current, string? agentId) => current.Files.Enabled && current.Files.WriteEnabled
        && (agentId is null ? AgentRouting.MainOwnsFiles(current)
            : current.Agents.Owner(AgentScenarios.FileRead) is { Enabled: true } owner
                && string.Equals(owner.Id, agentId, StringComparison.Ordinal));
}

/// <summary>Prepares a create-only operation and commits it under the settings publication boundary.</summary>
internal sealed class LiveFileWriteTool(SettingsStore settings, string workspace, string? agentId) : IPreparedFileWriteTool
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public ToolSpec Spec { get; } = new("file_write", "在允许的目录内新建 UTF-8 文件，不覆盖已有文件；是否批准取决于审批挡位。",
        """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""");

    private bool Authorized(RuntimeSettings current) => FileToolSet.CanWrite(current, agentId);

    public IPreparedFileWrite Prepare(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = settings.Snapshot().Settings;
        if (!Authorized(snapshot)) throw new UnauthorizedAccessException();
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("path", out var path)
            || path.ValueKind != JsonValueKind.String || !root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
            throw new ArgumentException("path and content are required");
        var text = content.GetString()!;
        if (Utf8.GetByteCount(text) > FileReadTool.MaxFileBytes)
            throw new ArgumentException("content exceeds the file size limit");
        var bytes = Utf8.GetBytes(text);
        var prepared = WindowsSafeFileCreate.Prepare(path.GetString()!, bytes, snapshot.Files.Roots,
            snapshot.Files.WriteRoots, [workspace]);
        try
        {
            ct.ThrowIfCancellationRequested();
            return new Prepared(settings, snapshot, Authorized, prepared,
                JsonSerializer.Serialize(new { path = prepared.TargetPath }));
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult(false, "denied: file write requires prepared execution"));

    private sealed class Prepared(SettingsStore settings, RuntimeSettings snapshot, Func<RuntimeSettings, bool> authorized,
        PreparedFileCreate inner, string approvalJson) : IPreparedFileWrite
    {
        public string ApprovalJson => approvalJson;
        public string TargetPath => inner.TargetPath;

        public Task<ToolResult> CommitAsync(CancellationToken ct)
        {
            try
            {
                FileCreateReceipt? receipt = null;
                if (!settings.TryCommitFileWrite(snapshot, authorized, () => receipt = inner.Commit(ct), ct))
                    return Task.FromResult(new ToolResult(false, "denied: file write authorization changed"));
                return Task.FromResult(new ToolResult(true, JsonSerializer.Serialize(new
                {
                    path = receipt!.Path, bytes = receipt.ByteCount, version = receipt.Version,
                })));
            }
            catch (FileCreateConflictException)
            {
                return Task.FromResult(new ToolResult(false, "conflict: target already exists; nothing was overwritten"));
            }
            catch (FileCreateUnconfirmedException)
            {
                return Task.FromResult(new ToolResult(false, "file write status unconfirmed; verify before retrying"));
            }
        }

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Prepares a version-checked edit for a governed commit.</summary>
internal sealed class LiveFileEditTool(SettingsStore settings, string workspace, string? agentId) : IPreparedFileWriteTool
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public ToolSpec Spec { get; } = new("file_edit", "在支持事务的本地 NTFS 上精确替换文本；需要读取版本，是否批准取决于审批挡位，版本变化时不会提交。",
        """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"},"expected_version":{"type":"string"}},"required":["path","old_text","new_text","expected_version"]}""");

    private bool Authorized(RuntimeSettings current) => FileToolSet.CanWrite(current, agentId);

    public IPreparedFileWrite Prepare(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = settings.Snapshot().Settings;
        if (!Authorized(snapshot)) throw new UnauthorizedAccessException();
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;
        string Field(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("missing field");
        var path = Field("path");
        var oldText = Field("old_text");
        var newText = Field("new_text");
        var version = Field("expected_version");
        if (Utf8.GetByteCount(oldText) > FileReadTool.MaxFileBytes || Utf8.GetByteCount(newText) > FileReadTool.MaxFileBytes)
            throw new ArgumentException("edit text exceeds the file size limit");
        var prepared = ExactFileEdit.Prepare(path, version, oldText, newText, snapshot.Files.Roots,
            snapshot.Files.WriteRoots, [workspace]);
        try
        {
            ct.ThrowIfCancellationRequested();
            return new Prepared(settings, snapshot, Authorized, prepared, JsonSerializer.Serialize(new
            {
                path = prepared.TargetPath,
                risk = "文件内容与读取版本不一致时，不会提交。",
            }));
        }
        catch { prepared.Dispose(); throw; }
    }

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult(false, "denied: file edit requires prepared execution"));

    private sealed class Prepared(SettingsStore settings, RuntimeSettings snapshot, Func<RuntimeSettings, bool> authorized,
        PreparedFileEdit inner, string approvalJson) : IPreparedFileWrite
    {
        public string TargetPath => inner.TargetPath;
        public string ApprovalJson => approvalJson;
        public Task<ToolResult> CommitAsync(CancellationToken ct)
        {
            try
            {
                FileEditReceipt? receipt = null;
                if (!settings.TryCommitFileWrite(snapshot, authorized, () => receipt = inner.Commit(ct), ct))
                    return Task.FromResult(new ToolResult(false, "denied: file edit authorization changed"));
                return Task.FromResult(new ToolResult(true, JsonSerializer.Serialize(new
                {
                    path = receipt!.Path, bytes = receipt.ByteCount, version = receipt.Version,
                })));
            }
            catch (FileEditConflictException)
            {
                return Task.FromResult(new ToolResult(false, "conflict: file changed; read it again"));
            }
            catch (FileCreateUnconfirmedException)
            {
                return Task.FromResult(new ToolResult(false, "file edit status unconfirmed; verify before retrying"));
            }
        }
        public void Dispose() => inner.Dispose();
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
