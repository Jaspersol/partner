using Partner.Api.Settings;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

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
        SettingsStore settings)
    {
        Workspace = workspace;
        Session = session;
        Queue = queue;
        Memory = memory;
        Settings = settings;
    }

    public string Workspace { get; }

    public ISessionStore Session { get; }

    public IInputQueue Queue { get; }

    public IMemoryStore Memory { get; }

    public SettingsStore Settings { get; }

    public static async Task<ApiContext> OpenAsync(string workspace, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        Directory.CreateDirectory(workspace);

        var (session, _) = JsonlSessionStore.Open(workspace, clock);
        var queue = new JsonlInputQueue(workspace, capacity: 1024, clock: clock);
        var memory = new MemoryStore(Path.Combine(workspace, "memory", "memory.db"), clock: clock);
        var settings = new SettingsStore(Path.Combine(workspace, "settings", "voice.json"));

        return await Task.FromResult(new ApiContext(workspace, session, queue, memory, settings)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        await Memory.DisposeAsync().ConfigureAwait(false);
    }
}
