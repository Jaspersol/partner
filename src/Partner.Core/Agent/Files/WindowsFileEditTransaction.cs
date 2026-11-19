using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Partner.Core.Agent.Files;

// TxF locks the file, not just the default stream, until commit or rollback. This is essential:
// ordinary share modes do not exclude link creation by an already-open metadata handle.
// Unsupported volumes and files must fail closed; never fall back to an ordinary writer.
internal sealed class WindowsFileEditTransaction : IDisposable
{
    private const uint GenericReadWrite = 0xc0000000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ShareReadWrite = 3;
    private const uint DoNotPromote = 1;
    private readonly SafeFileHandle _transaction;
    private bool _committed;

    private WindowsFileEditTransaction(SafeFileHandle transaction, SafeFileHandle file, string volumePath) =>
        (_transaction, File, VolumePath) = (transaction, file, volumePath);

    internal SafeFileHandle File { get; }
    internal string VolumePath { get; }

    internal static WindowsFileEditTransaction Open(SafeFileHandle parent, string name)
    {
        using var authorized = NativeCreate.OpenEditTarget(parent, name);
        var identity = NativeCreate.CheckEditTarget(authorized);
        var volumePath = NativeCreate.FinalPath(authorized, 1);
        if (!volumePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
            || NativeCreate.IsDeniedName(Path.GetFileName(volumePath)))
            throw new UnauthorizedAccessException("The opened file is not an eligible local target.");

        var transaction = CreateTransaction(0, 0, DoNotPromote, 0, 0, 0, 0);
        try
        {
            if (transaction.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var file = CreateFileTransacted(volumePath, GenericReadWrite, ShareReadWrite, 0, OpenExisting,
                OpenReparsePoint, 0, transaction, 0, 0);
            try
            {
                if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                // The path-based open requests no truncation, creation or writes. Only use the
                // TxF writer after proving it refers to the same entry opened relative to the held parent.
                if (NativeCreate.CheckEditTarget(file) != identity || NativeCreate.FinalPath(file, 1) != volumePath)
                    throw new FileEditConflictException("The opened file no longer matches the authorized target.");
                return new WindowsFileEditTransaction(transaction, file, volumePath);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    internal void Commit()
    {
        File.Dispose(); // Close the writer before commit; the transaction still holds the file lock.
        if (!CommitTransaction(_transaction))
            throw new FileCreateUnconfirmedException("The edit commit could not be confirmed.", new Win32Exception(Marshal.GetLastWin32Error()));
        _committed = true;
    }

    public void Dispose()
    {
        File.Dispose();
        if (!_committed && !_transaction.IsClosed)
            _ = RollbackTransaction(_transaction); // Synchronous; closing the last handle also requests rollback.
        _transaction.Dispose();
    }

    [DllImport("ktmw32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateTransaction(nint attributes, nint unitOfWork, uint options, uint isolationLevel,
        uint isolationFlags, uint timeout, nint description);

    [DllImport("ktmw32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeFileHandle transaction);

    [DllImport("ktmw32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RollbackTransaction(SafeFileHandle transaction);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileTransactedW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileTransacted(string path, uint access, uint share, nint attributes,
        uint disposition, uint flags, nint template, SafeFileHandle transaction, nint miniVersion, nint extended);
}
