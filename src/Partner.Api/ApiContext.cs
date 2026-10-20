using Partner.Api.Settings;
using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Scheduler;
using Partner.Core.Sessions;
using Partner.Desktop;
using Partner.Desktop.Perception;
using Partner.Desktop.Telemetry;
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
        IScheduler scheduler,
        DesktopAutomation desktop,
        QueuedApprovalHandler approvals,
        TaskLeaseStore taskLeases,
        InMemoryTelemetrySink telemetry,
        TimeProvider clock)
    {
        Withdrawals = new WithdrawalService(session, memory);
        Clock = clock;
        Workspace = workspace;
        Session = session;
        Queue = queue;
        Memory = memory;
        Settings = settings;
        Media = media;
        Scheduler = scheduler;
        Desktop = desktop;
        Approvals = approvals;
        TaskLeases = taskLeases;
        Telemetry = telemetry;

        // Options are read through a delegate, so an edit to the proxy, the budget or the engine list takes
        // effect on the next search rather than at the next restart. The health state lives under the workspace,
        // beside the settings it answers to.
        Web = new Partner.Core.Agent.Web.WebSearchService(
            () => Settings.Load().Web.ToOptions(),
            workspace,
            () => Clock.GetUtcNow());
    }

    public string Workspace { get; }

    /// <summary>Deleting the latest conversation and withdrawing from a point, with the undo the latter allows.</summary>
    public WithdrawalService Withdrawals { get; }

    public ISessionStore Session { get; }

    public IInputQueue Queue { get; }

    public IMemoryStore Memory { get; }

    public SettingsStore Settings { get; }

    /// <summary>Attachments live here, not in the log: the bytes are files, the events carry only their ids.</summary>
    public IMediaStore Media { get; }

    /// <summary>
    /// Timed tasks. Firing one only enqueues an <c>InputSource.Schedule</c> item on <see cref="Queue"/>, so a
    /// scheduled message waits its turn behind whatever the person typed, like any other input.
    /// </summary>
    public IScheduler Scheduler { get; }

    private CancellationTokenSource? _schedulerStop;
    private Task? _schedulerRun;

    /// <summary>
    /// Starts the delivery loop. Separate from <see cref="OpenAsync"/> because the host decides when the runtime
    /// is up, the same way it starts memory maintenance; calling it twice is a no-op.
    /// </summary>
    public void StartScheduler()
    {
        if (_schedulerRun is not null)
        {
            return;
        }

        _schedulerStop = new CancellationTokenSource();
        _schedulerRun = Task.Run(async () =>
        {
            // The loop only ends by cancellation. A failure inside one pass must not end it for good, or every
            // timed task would silently stop firing until the next restart.
            while (!_schedulerStop.IsCancellationRequested)
            {
                try
                {
                    await Scheduler.RunAsync(_schedulerStop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"scheduler pass failed: {error.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(5), _schedulerStop.Token).ConfigureAwait(false);
                }
            }
        });
    }

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

    /// <summary>
    /// Desktop tool measurements, shared by the automation (walk side) and the tools (result side), and
    /// read by the telemetry endpoint. Session-level like every other store: it outlives a consumer
    /// restart, because a tuning question spans turns.
    /// </summary>
    public InMemoryTelemetrySink Telemetry { get; }

    /// <summary>
    /// The one web capability in this process. Shared on purpose: two instances would hold two rate limiters
    /// (so the same engine could be asked twice as often as its limit allows), two answer caches, and two
    /// views of the circuit-breaker health file — and the host ledger that decides whether <c>web_fetch</c>
    /// asks the user would be split in half.
    /// </summary>
    public Partner.Core.Agent.Web.WebSearchService Web { get; }

    /// <summary>The clock the host was opened with. The telemetry endpoint reads it so its window can be stated
    /// in the same time base the sink stamped its events in — a window measured against a different clock would
    /// be a second, silently different answer.</summary>
    public TimeProvider Clock { get; }

    public static async Task<ApiContext> OpenAsync(string workspace, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        Directory.CreateDirectory(workspace);

        var (session, _) = JsonlSessionStore.Open(workspace, clock);
        var queue = new JsonlInputQueue(workspace, capacity: 1024, clock: clock);

        // Read once, here: the store is built with its embedding provider fixed. Turning the switch on
        // therefore takes effect at the next process start, which the settings comment in MemorySettings
        // owes the reader — changing it live would mean re-embedding or silently diverging between old and
        // new records on the vector arm of the scorer.
        var settings = new SettingsStore(Path.Combine(workspace, "settings", "runtime.json"));
        var memoryOptions = new MemoryStoreOptions { MinScore = 0.3 };
        if (settings.Load().Memory.Embeddings)
        {
            memoryOptions = memoryOptions with { Embeddings = new HashingEmbeddingProvider() };
        }

        var memory = new MemoryStore(Path.Combine(workspace, "memory", "memory.db"), memoryOptions, clock);

        // Holds the whole runtime configuration now, not just the voice halves.
        var media = FileMediaStore.Open(workspace);

        // The machine's own zone: "every day at 9" means 9 on the wall clock of whoever wrote it, and a cron
        // expression read in UTC would be off by their offset.
        var scheduler = new JsonlScheduler(
            workspace, queue, clock, new SchedulerOptions { TimeZone = TimeZoneInfo.Local });

        // The TTL comes from the settings at call time rather than at construction: a lease policy is a
        // per-observation decision, and the settings can change while the process runs.
        //
        // The DOM source and its port registry are shared with the launch tool on purpose: a debug port only
        // exists for a program this process started, so the thing that knows which ports exist is the thing
        // that opened them.
        var cdpPorts = new CdpRegistry();
        var telemetry = new InMemoryTelemetrySink(clock);
        //
        // The input mode is read from the settings at each action and each launch rather than captured here:
        // it is the one desktop setting that has to take effect while a turn is running (a user who sees the
        // agent taking over their screen turns it down, and expects the very next action to obey).
        Func<Partner.Desktop.Input.InputMode> inputMode = () =>
            Partner.Desktop.Input.InputModes.Parse(settings.Load().Desktop.InputMode);

        var desktop = new DesktopAutomation(
            ttlPolicy: LeaseTtlPolicyFor(settings.Load()),
            cdp: new CdpDomSource(),
            cdpPorts: cdpPorts,
            desktopLaunch: new DesktopLaunchTool(startMenuRoots: null, ports: cdpPorts, inputMode: inputMode),
            telemetry: telemetry,
            inputMode: inputMode);

        return await Task.FromResult(new ApiContext(
            workspace,
            session,
            queue,
            memory,
            settings,
            media,
            scheduler,
            desktop,
            new QueuedApprovalHandler(),
            new TaskLeaseStore(clock: clock),
            telemetry,
            clock ?? TimeProvider.System))
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
        Web.Dispose();

        if (_schedulerStop is not null && _schedulerRun is not null)
        {
            await _schedulerStop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(_schedulerRun, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            _schedulerStop.Dispose();
        }

        await Scheduler.DisposeAsync().ConfigureAwait(false);
        await Queue.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        await Memory.DisposeAsync().ConfigureAwait(false);
    }
}
