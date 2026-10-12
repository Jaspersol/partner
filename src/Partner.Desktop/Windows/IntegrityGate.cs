using System.Runtime.InteropServices;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Windows;

/// <summary>
/// Whether Windows would deliver this process's <c>SendInput</c> to a window's owner.
/// <para>
/// UIPI (User Interface Privilege Isolation) silently drops input a lower-integrity process sends to a
/// higher-integrity window: <c>SendInput</c> still reports success — the events entered the input stream —
/// and the elevated application simply never sees them. That is the failure mode where the agent clicks and
/// nothing happens, with "注入成功" in the result and nothing in the world to explain it. Reading the
/// target's integrity level <i>before</i> injecting turns the silence into a named refusal.
/// </para>
/// <para>
/// The check is deliberately <b>fail-open</b>: a level that cannot be read (a protected process, an access
/// denied on the token) leaves the action to proceed. The confirmed cases are the only ones the
/// no-op guarantee holds for — and fail-closed here would refuse legitimate targets on unreadable
/// processes. Refusing visible action to guard against an invisible problem is the failure mode this gate
/// exists to prevent, not to cause.
/// </para>
/// <para>
/// A process's integrity level is fixed for its lifetime, so the answer for one (process, start time) pair
/// is cached: token queries are not free, and every click would otherwise pay for one.
/// </para>
/// </summary>
public sealed class IntegrityGate
{
    private readonly Dictionary<(uint ProcessId, long StartTicks), bool> _cache = new();

    /// <summary>The RID Windows assigns to the standard levels, for the message.</summary>
    private const uint MediumRid = 0x2000;

    /// <summary>
    /// A refusal reason when the target would swallow this process's input, or null when it would not
    /// (or when that cannot be determined).
    /// </summary>
    /// <param name="handle">The window the input is meant for.</param>
    public string? Refusal(nint handle)
    {
        if (NativeMethods.GetWindowThreadProcessId(handle, out var pid) == 0 || pid == 0)
        {
            return null;
        }

        if (!IsElevated(pid))
        {
            return null;
        }

        return ElevatedRefusal((int)pid);
    }

    /// <summary>The refusal text, on its own so it can be asserted without an elevated process to point at.</summary>
    internal static string ElevatedRefusal(int pid) =>
        $"目标以管理员（高完整性，进程 {pid}）运行，Windows 会静默丢弃本进程发出的输入："
        + "请以相同权限重启本应用后再操作这个窗口";

    /// <summary>Whether a process's integrity level is above this process's own. Cached per process start.</summary>
    private bool IsElevated(uint pid)
    {
        var startTicks = ProcessStartTicks(pid);
        var key = (pid, startTicks);

        if (_cache.TryGetValue(key, out var known))
        {
            return known;
        }

        var elevated = QueryIsElevated(pid);
        _cache[key] = elevated;
        return elevated;
    }

    /// <summary>The start time, in system ticks, that keeps a cached answer pinned to this process instance.</summary>
    private static long ProcessStartTicks(uint pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).StartTime.Ticks;
        }
        catch
        {
            // Exited or not queryable (its owner is elevated — the common case): 0 keys a one-shot entry.
            return 0;
        }
    }

    /// <summary>
    /// The confirmed-elevated test: the target's token integrity RID, compared against this process's.
    /// Undetermined answers (process or token could not be opened, a malformed SID) are <b>not</b> elevated,
    /// by the fail-open rule above — only a read whole enough to compare can refuse.
    /// </summary>
    private static bool QueryIsElevated(uint pid)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return false;
        }

        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
        {
            NativeMethods.CloseHandle(process);
            return false;
        }

        var elevated = false;
        try
        {
            if (!NativeMethods.GetTokenInformation(
                    token, NativeMethods.TokenIntegrityLevel, buffer: 0, length: 0, out var returnLength)
                || returnLength <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(returnLength);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token, NativeMethods.TokenIntegrityLevel, buffer, returnLength, out _))
                {
                    return false;
                }

                // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer;
                // the SID itself lives inside the same buffer, which is why the buffer must be unmanaged.
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == 0)
                {
                    return false;
                }

                var countPointer = NativeMethods.GetSidSubAuthorityCount(sid);
                var count = countPointer == 0 ? 0 : Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return false;
                }

                var ridgePointer = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridgePointer == 0)
                {
                    return false;
                }

                var rid = (uint)Marshal.ReadInt32(ridgePointer);
                elevated = rid > OwnIntegrityRid() && rid > MediumRid;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }

        return elevated;
    }

    /// <summary>This process's own token integrity RID, from the same SID structure. 0 when unreadable —
    /// compared against 0, no confirmed-elevated result can come out of this process's own blindness.</summary>
    private static uint OwnIntegrityRid()
    {
        if (!NativeMethods.OpenProcessToken(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                NativeMethods.TokenQuery,
                out var token))
        {
            return 0;
        }

        try
        {
            if (!NativeMethods.GetTokenInformation(
                    token, NativeMethods.TokenIntegrityLevel, buffer: 0, length: 0, out var returnLength)
                || returnLength <= 0)
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal(returnLength);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token, NativeMethods.TokenIntegrityLevel, buffer, returnLength, out _))
                {
                    return 0;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var countPointer = NativeMethods.GetSidSubAuthorityCount(sid);
                var count = countPointer == 0 ? 0 : Marshal.ReadByte(countPointer);
                if (sid == 0 || count == 0)
                {
                    return 0;
                }

                var ridgePointer = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                return ridgePointer == 0 ? 0 : (uint)Marshal.ReadInt32(ridgePointer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
