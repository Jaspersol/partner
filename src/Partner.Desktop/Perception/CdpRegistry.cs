using System.Net.Http;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace Partner.Desktop.Perception;

/// <summary>
/// Which windows have a DevTools endpoint, and on which port.
/// <para>
/// A browser only exposes one if it was started asking to. That is the whole limit of this mechanism and it is
/// worth being blunt about: <b>only a window this agent launched</b> gets a debug port, because the flag has to
/// be on the command line and the user's already-running application cannot be given one. For anything else the
/// UI Automation path and the coordinate path still apply, and this simply does not exist.
/// </para>
/// <para>
/// The flag also does nothing if the application is a single-instance one that is already running: the new
/// command line is handed to the process that is already up, and it never opens a port. That is reported when it
/// happens rather than left to look like "the page has no controls".
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

    /// <summary>Records the port a launched process opened.</summary>
    public void Register(int processId, int port) => _ports[processId] = port;

    /// <summary>The port a process opened, when it is one this agent started.</summary>
    public int? PortOf(int processId) => _ports.TryGetValue(processId, out var port) ? port : null;

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
    /// Waits for a launched process to open its debug port.
    /// <para>
    /// Bounded, because the honest answer for "it never did" is worth more than a turn spent waiting: a
    /// single-instance application that was already running hands the command line to the process already up and
    /// never opens one, and no amount of waiting changes that.
    /// </para>
    /// </summary>
    public async Task<bool> WaitForPortAsync(int processId, int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (Process.GetProcessById(processId).HasExited)
            {
                return false;
            }

            try
            {
                using var response = await Probe.GetAsync($"http://127.0.0.1:{port}/json/version").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    Register(processId, port);
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