using System.Diagnostics;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>The table of debug ports forgets processes that are gone.</summary>
public sealed class CdpRegistryLifecycleTests
{
    [Fact]
    public void A_port_is_forgotten_once_its_process_has_exited()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        var registry = new CdpRegistry();
        registry.Register(process.Id, 45123);

        process.WaitForExit(5000);

        Assert.Null(registry.PortOf(process.Id));
    }

    [Fact]
    public void A_port_of_a_live_process_is_kept()
    {
        var registry = new CdpRegistry();
        registry.Register(Environment.ProcessId, 45124);

        Assert.Equal(45124, registry.PortOf(Environment.ProcessId));
    }

    [Fact]
    public void The_listener_table_reports_a_port_this_process_opened()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            Assert.Contains(port, TcpListeners.LoopbackPortsOf(Environment.ProcessId));
        }
        finally
        {
            listener.Stop();
        }
    }
}
