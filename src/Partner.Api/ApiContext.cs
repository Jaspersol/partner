using Partner.Api.Settings;
using Partner.Core.Media;
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
        SettingsStore settings,
        IMediaStore media)
    {
        Workspace = workspace;
        Session = session;
        Queue = queue;
        Memory = memory;
        Settings = settings;
        Media = media;
    }

    public string Workspace { get; }

    public ISessionStore Session { get; }

    public IInputQueue Queue { get; }

    public IMemoryStore Memory { get; }

    public SettingsStore Settings { get; }

    /// <summary>Attachments live here, not in the log: the bytes are files, the events carry only their ids.</summary>
    public IMediaStore Media { get; }

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

        return await Task.FromResult(new ApiContext(workspace, session, queue, memory, settings, media))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        await Memory.DisposeAsync().ConfigureAwait(false);
    }
}
