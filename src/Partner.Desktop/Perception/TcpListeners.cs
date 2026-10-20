using System.Runtime.InteropServices;

namespace Partner.Desktop.Perception;

/// <summary>
/// Which TCP ports on this machine each process is listening on. The operating system keeps the table; a process
/// that opened a debugging port can be found by asking for its listeners instead of guessing from its command line,
/// which only the process itself (and an elevated reader) can see.
/// </summary>
internal static class TcpListeners
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint NoError = 0;
    private const uint InsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct Row
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);

    /// <summary>
    /// The IPv4 loopback (or any-address) ports the process listens on. Empty when the table cannot be read.
    /// Loopback only: a debugging port that is reachable from the network is somebody else's business.
    /// </summary>
    public static IReadOnlyList<int> LoopbackPortsOf(int processId) =>
        Listeners().Where(entry => entry.ProcessId == processId).Select(entry => entry.Port).ToList();

    /// <summary>The process that is listening on a loopback port, or null when nothing is (or it cannot be read).</summary>
    public static int? OwnerOf(int port) =>
        Listeners().Where(entry => entry.Port == port).Select(entry => (int?)entry.ProcessId).FirstOrDefault();

    private static IReadOnlyList<(int ProcessId, int Port)> Listeners()
    {
        var size = 0;
        var result = GetExtendedTcpTable(nint.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (result != InsufficientBuffer || size <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (result != NoError)
            {
                return [];
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<Row>();
            var ports = new List<(int ProcessId, int Port)>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<Row>(buffer + sizeof(int) + (i * rowSize));
                // 127.0.0.1 is stored little-endian, and 0.0.0.0 listens on every interface including loopback.
                var loopbackOrAny = row.LocalAddress == 0x0100007F || row.LocalAddress == 0;
                if (!loopbackOrAny)
                {
                    continue;
                }

                // The port sits in the low 16 bits in network byte order.
                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                if (port > 0)
                {
                    ports.Add(((int)row.OwningPid, port));
                }
            }

            return ports;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
