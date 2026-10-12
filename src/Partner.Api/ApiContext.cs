using Partner.Api.Settings;
using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;
using Partner.Desktop;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;

namespace Partner.Api;

/// <summary>
/// Everything the UI needs, opened once and shared. The API is a thin projection over the same stores the
/// rest of the system writes — it never keeps state of its own, which is what makes the views rebuildable.
/// </summary>
public sealed class ApiContext : IAsyncDisposable
{
    private ApiContext(
        string workspace,
        ISessionStore session,
        IInputQueue queue,
        IMemoryStore memory,
        SettingsStore settings,
        IMediaStore media,
        DesktopAutomation desktop,
        QueuedApprovalHandler approvals,
        TaskLeaseStore taskLeases)
    {
        Workspace = workspace;
        Session = session;
        Queue = queue;
        Memory = memory;
        Settings = settings;
        Media = media;
        Desktop = desktop;
        Approvals = approvals;
        TaskLeases = taskLeases;
    }

    public string Workspace { get; }

    public ISessionStore Session { get; }

    public IInputQueue Queue { get; }

    public IMemoryStore Memory { get; }

    public SettingsStore Settings { get; }

    /// <summary>Attachments live here, not in the log: the bytes are files, the events carry only their ids.</summary>
    public IMediaStore Media { get; }

    /// <summary>
    /// The desktop facade, shared by the tools and the endpoints so a lease issued by one is visible to the
    /// other — the point of a lease is that observing and acting agree on what was seen.
    /// </summary>
    public DesktopAutomation Desktop { get; }

    /// <summary>
    /// Where a tool call waits for a human. Shared with the API so the UI can answer what the model asked.
    /// </summary>
    public QueuedApprovalHandler Approvals { get; }

    /// <summary>
    /// Continuing approval, per window: what a human granted is recorded here rather than on the
    /// governance instance, so restarting the consumer does not erase the fact the human acted on.
    /// </summary>
    public TaskLeaseStore TaskLeases { get; }

    public static async Task<ApiContext> OpenAsync(string workspace, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        Directory.CreateDirectory(workspace);

        var (session, _) = JsonlSessionStore.Open(workspace, clock);
        var queue = new JsonlInputQueue(workspace, capacity: 1024, clock: clock);
        var memory = new MemoryStore(Path.Combine(workspace, "memory", "memory.db"), clock: clock);
        // Holds the whole runtime configuration now, not just the voice halves.
        var settings = new SettingsStore(Path.Combine(workspace, "settings", "runtime.json"));
        var media = FileMediaStore.Open(workspace);

        // The TTL comes from the settings at call time rather than at construction: a lease policy is a
        // per-observation decision, and the settings can change while the process runs.
        //
        // The DOM source and its port registry are shared with the launch tool on purpose: a debug port only
        // exists for a program this process started, so the thing that knows which ports exist is the thing
        // that opened them.
        var cdpPorts = new CdpRegistry();
        var desktop = new DesktopAutomation(
            ttlPolicy: LeaseTtlPolicyFor(settings.Load()),
            cdp: new CdpDomSource(),
            cdpPorts: cdpPorts,
            desktopLaunch: new DesktopLaunchTool(startMenuRoots: null, ports: cdpPorts));

        return await Task.FromResult(new ApiContext(
            workspace,
            session,
            queue,
            memory,
            settings,
            media,
            desktop,
            new QueuedApprovalHandler(),
            new TaskLeaseStore(clock: clock)))
            .ConfigureAwait(false);
    }

    /// <summary>The lease policy the settings ask for, clamped by the policy's own floor and cap.</summary>
    public static LeaseTtlPolicy LeaseTtlPolicyFor(RuntimeSettings settings)
    {
        var wanted = Math.Clamp(settings.Desktop.LeaseTtlMs, (int)LeaseTtlPolicy.Default.FloorMs, (int)LeaseTtlPolicy.Default.CapMs);
        return LeaseTtlPolicy.Default with { BaseMs = wanted };
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        await Memory.DisposeAsync().ConfigureAwait(false);
    }
}
