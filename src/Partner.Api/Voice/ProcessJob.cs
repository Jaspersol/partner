using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Partner.Api.Voice;

/// <summary>
/// Windows job object with kill-on-job-close. Every managed engine's process tree is assigned to one, and the
/// handle lives as long as this API process: if the API dies without cleanup (crash, hard kill — paths where
/// LocalEngineHost.DisposeAsync never runs), the kernel releases the handle and terminates the whole tree.
/// Without it engines linger as orphans holding GPU memory and ports.
/// <para>
/// Kill-on-close fires when the *last* handle to the job closes. The handle is never duplicated or made
/// inheritable, so the only handle is this process's: engine children and grandchildren (tts-server's decoder
/// subprocesses) are in the job but cannot keep it alive on their own.
/// </para>
/// </summary>
internal sealed class EngineJob : IDisposable
{
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: releasing the job handle kills the tree.
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    private EngineJob(IntPtr handle) => _handle = handle;

    /// <summary>Assign to a job or report why not. Failure is not fatal: engines still run, only the hard-kill
    /// cleanup is lost (e.g. a sandbox host that already put us inside a job it forbids breaking out of). The
    /// failure stays visible in the reason string.</summary>
    public static EngineJob? Create(Process process, string name)
    {
        try
        {
            var handle = CreateJobObjectW(lpJobAttributes: IntPtr.Zero, lpName: null);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            var limits = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose };
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = limits };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref extended, size))
            {
                var setFailed = new Win32Exception();
                CloseHandle(handle);
                throw setFailed;
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                var assignFailed = new Win32Exception();
                CloseHandle(handle);
                throw assignFailed;
            }

            return new EngineJob(handle);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"LocalEngineHost: {name} 进程树未纳入作业对象（异常退出不会连带杀引擎）：{error.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero && CloseHandle(handle))
        {
            // kill-on-close fired synchronously here; nothing to await.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, uint JobObjectExtendedLimitInformation,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectExtendedLimitInformation, int cbAllocationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
