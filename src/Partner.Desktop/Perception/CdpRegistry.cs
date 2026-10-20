using System.Net.Http;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace Partner.Desktop.Perception;

/// <summary>
/// Which windows have a DevTools endpoint, and on which port.
/// <para>
/// A browser exposes one only if it was started asking to: <b>only a window this agent launched</b> gets a debug
/// port, because the flag must be on the command line. For anything else the UI Automation and coordinate paths
/// still apply, and this does not exist.
/// </para>
/// <para>
/// The flag does nothing for a single-instance application that is already running: the new command line goes to
/// the process already up, which never opens a port.
/// </para>
/// </summary>
public sealed class CdpRegistry
{
    private readonly ConcurrentDictionary<int, int> _ports = new();

    /// <summary>
    /// Programs built on Chromium that answer to the flag. Matched on the executable's own name, which is what
    /// identifies the engine — the products around it (Edge, Chrome, Electron apps) are all the same runtime.
    /// </summary>
    public static bool IsChromiumFamily(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);

        return name.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            || name.Contains("chromium", StringComparison.OrdinalIgnoreCase)
            || name.Contains("electron", StringComparison.OrdinalIgnoreCase)
            || name.Contains("brave", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vivaldi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("opera", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shared by every poll: a client per probe is a socket per probe, up to thirty of them per launch.</summary>
    private static readonly System.Net.Http.HttpClient Probe = new() { Timeout = TimeSpan.FromMilliseconds(500) };

    private readonly ConcurrentDictionary<int, long> _discoveryTried = new();

    /// <summary>How long a process that had no debug port is left alone before it is looked at again.</summary>
    internal const long DiscoveryRetryMs = 30_000;

    /// <summary>
    /// Finds a debug port the <i>user</i> opened: a Chromium-family process started with
    /// <c>--remote-debugging-port</c> by hand or by a shortcut. Its listening loopback ports are read from the
    /// TCP table and each is asked for <c>/json/version</c>; one that answers like DevTools is recorded.
    /// <para>
    /// This cannot reach a browser not opened for it. A process looked at with no port is skipped for
    /// <see cref="DiscoveryRetryMs"/>, so the common path costs nothing.
    /// </para>
    /// </summary>
    public async Task<int?> DiscoverAsync(int processId, string? executableName, CancellationToken ct = default)
    {
        if (PortOf(processId) is { } known)
        {
            return known;
        }

        if (!IsChromiumFamily(executableName))
        {
            return null;
        }

        var now = Environment.TickCount64;
        if (_discoveryTried.TryGetValue(processId, out var last) && now - last < DiscoveryRetryMs)
        {
            return null;
        }

        _discoveryTried[processId] = now;

        foreach (var port in TcpListeners.LoopbackPortsOf(processId))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await Probe.GetAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!body.Contains("webSocketDebuggerUrl", StringComparison.Ordinal) && !body.Contains("Browser", StringComparison.Ordinal))
                {
                    continue;
                }

                Register(processId, port);
                return port;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                // Not a DevTools endpoint (or not answering): the next listener.
            }
        }

        return null;
    }

    /// <summary>Records the port a launched process opened.</summary>
    public void Register(int processId, int port) => _ports[processId] = port;

    /// <summary>
    /// The port a process opened, when this agent knows one. An exited process takes its entry with it, so a
    /// recycled pid cannot inherit a dead port.
    /// </summary>
    public int? PortOf(int processId)
    {
        if (!_ports.TryGetValue(processId, out var port))
        {
            return null;
        }

        if (!IsAlive(processId))
        {
            Forget(processId);
            return null;
        }

        return port;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Drops a process that is gone, so the map does not outlive what it describes.</summary>
    public void Forget(int processId) => _ports.TryRemove(processId, out _);

    /// <summary>A port nothing on this machine is listening on, asked of the kernel rather than guessed.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Waits for a launched process to open its debug port. Bounded, because the honest answer for "it never
    /// did" is worth more than a turn spent waiting: a single-instance application that was already running
    /// hands the command line to the running process and never opens one.
    /// </summary>
    public async Task<bool> WaitForPortAsync(int processId, int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            // The launched process may be gone already, which is not a failure: a Chromium launcher that hands
            // its window to the real browser process exits at once. What counts is whether the port answers and
            // who owns it.
            try
            {
                using var response = await Probe.GetAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    // Windows belong to the process that listens, which may not be the one that was launched.
                    Register(TcpListeners.OwnerOf(port) ?? processId, port);
                    return true;
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                // Not up yet.
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return false;
    }
}