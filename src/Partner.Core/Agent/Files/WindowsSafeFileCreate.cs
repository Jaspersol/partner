using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Partner.Core.Agent.Files;

/// <summary>Windows local NTFS create-only operation. This is not an authorization or approval mechanism.</summary>
public static class WindowsSafeFileCreate
{
    public static PreparedFileCreate Prepare(string path, byte[] content, IEnumerable<string> readRoots,
        IEnumerable<string> writeRoots, IEnumerable<string> deniedDirectories)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(readRoots);
        ArgumentNullException.ThrowIfNull(writeRoots);
        ArgumentNullException.ThrowIfNull(deniedDirectories);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Only local Windows NTFS is supported.");
        var full = NativeCreate.ValidatePath(path);
        var name = Path.GetFileName(full);
        NativeCreate.ValidateLeaf(name);
        var handles = new List<SafeFileHandle>();
        try
        {
            var parent = NativeCreate.OpenAuthorizedParent(full, name, readRoots, writeRoots, deniedDirectories, handles);
            var actual = NativeCreate.FinalPath(parent, 0);
            var bytes = (byte[])content.Clone();
            return new PreparedFileCreate(Path.Combine(actual, name), name, bytes, parent, handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }
}

public sealed record FileCreateReceipt(string Path, int ByteCount, string Version);
public sealed class FileCreateConflictException(string message, Exception? inner = null) : IOException(message, inner) { }
public sealed class FileCreateUnconfirmedException(string message, Exception? inner = null) : IOException(message, inner) { }

/// <summary>Holds the checked directory chain until committed or disposed. Use under the caller's authorization lock.</summary>
public sealed class PreparedFileCreate : IDisposable
{
    private readonly string _name;
    private readonly byte[] _content;
    private readonly SafeFileHandle _parent;
    private readonly List<SafeFileHandle> _handles;
    private bool _used;
    private bool _disposed;

    internal PreparedFileCreate(string path, string name, byte[] content, SafeFileHandle parent, List<SafeFileHandle> handles)
    {
        TargetPath = path;
        _name = name;
        _content = content;
        _parent = parent;
        _handles = handles;
        Version = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    public string TargetPath { get; }
    public int ByteCount => _content.Length;
    public string Version { get; }

    public FileCreateReceipt Commit(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_used) throw new InvalidOperationException("This operation has already been attempted.");
        _used = true;
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryName = ".partner-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var temporary = NativeCreate.CreateTemporary(_parent, temporaryName);
        var renameAttempted = false;
        try
        {
            RandomAccess.Write(temporary, _content, 0);
            NativeCreate.Flush(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                renameAttempted = true;
                NativeCreate.Rename(temporary, _name);
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 80 or 183)
            {
                throw new FileCreateConflictException("The destination already exists.", error);
            }
            catch (Win32Exception error)
            {
                throw new FileCreateUnconfirmedException("The publication result could not be confirmed.", error);
            }
            return new FileCreateReceipt(TargetPath, ByteCount, Version);
        }
        catch (Exception error)
        {
            NativeCreate.CleanupTemporary(temporary, _parent, temporaryName, renameAttempted, error,
                error is not FileCreateConflictException);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var handle in _handles) handle.Dispose();
    }
}

internal static class NativeCreate
{
    private const uint ShareReadWrite = 3;
    private const uint OpenReparse = 0x00200000;
    private const uint DirectoryFile = 1;
    private const uint NonDirectoryFile = 0x40;
    private const uint Synchronous = 0x20;
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x00100000;
    private const uint Delete = 0x00010000;
    private const uint GenericWrite = 0x40000000;
    private const uint ObjCaseInsensitive = 0x40;

    internal readonly record struct Identity(string Volume, uint Serial, uint High, uint Low, int HandleIndex)
    {
        public bool Equals(Identity other) => Volume == other.Volume && Serial == other.Serial && High == other.High && Low == other.Low;
        public override int GetHashCode() => HashCode.Combine(Volume, Serial, High, Low);
    }

    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('/'))
            throw new ArgumentException("An ordinary absolute drive path is required.", nameof(path));
        var parts = path[3..].Split('\\');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')
            || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || p.Any(char.IsControl)))
            throw new ArgumentException("Special or ambiguous path components are not supported.", nameof(path));
        return path;
    }

    internal static void ValidateLeaf(string name)
    {
        var stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
            throw new ArgumentException("Device names are not supported.");
    }

    internal static bool IsDeniedName(string name) => name.Equals(".env", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
        || new[] { ".pem", ".key", ".pfx", ".p12", ".ppk", ".kdbx" }.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase))
        || new[] { "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa" }.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    internal static SafeFileHandle OpenAuthorizedParent(string full, string name, IEnumerable<string> readRoots,
        IEnumerable<string> writeRoots, IEnumerable<string> deniedDirectories, List<SafeFileHandle> handles)
    {
        var parents = OpenChain(Path.GetDirectoryName(full)!, handles);
        var read = OpenRoots(readRoots, handles);
        var write = OpenRoots(writeRoots, handles);
        var denied = OpenRoots(deniedDirectories, handles);
        if (!parents.Any(read.Contains) || !parents.Any(write.Contains))
            throw new UnauthorizedAccessException("The parent is not inside both authorized roots.");
        if (parents.Any(denied.Contains) || IsDeniedName(name))
            throw new UnauthorizedAccessException("The destination is protected.");
        return handles[parents[^1].HandleIndex];
    }

    internal static void CleanupTemporary(SafeFileHandle temporary, SafeFileHandle parent, string name,
        bool renameAttempted, Exception error, bool uncertainOnCleanupFailure)
    {
        try
        {
            if (!renameAttempted || IsTemporary(temporary, parent, name)) SetDelete(temporary, true);
        }
        catch (Exception cleanupError) when (cleanupError is Win32Exception or IOException)
        {
            if (renameAttempted && uncertainOnCleanupFailure)
                throw new FileCreateUnconfirmedException("The publication or temporary-file cleanup could not be confirmed.", new AggregateException(error, cleanupError));
            throw new IOException("Temporary-file cleanup failed.", new AggregateException(error, cleanupError));
        }
    }

    internal static HashSet<Identity> OpenRoots(IEnumerable<string> paths, List<SafeFileHandle> handles)
    {
        var result = new HashSet<Identity>();
        foreach (var path in paths)
        {
            var full = path.Length == 3 && path[1] == ':' && path[2] == '\\' ? path : ValidatePath(path.TrimEnd('\\'));
            var chain = OpenChain(full, handles);
            result.Add(chain[^1]);
        }
        return result;
    }

    internal static List<Identity> OpenChain(string path, List<SafeFileHandle> handles)
    {
        var root = Path.GetPathRoot(path)!;
        if (GetDriveType(root) != 3) throw new IOException("Only local fixed drives are supported.");
        var handle = CreateFile(root, ReadAttributes | Synchronize | 0x20, ShareReadWrite, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        handles.Add(handle);
        CheckDirectory(handle);
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationByHandle(handle, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, fs, fs.Capacity)
            || !fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new IOException("The volume is not confirmed as NTFS.");
        var volumePath = FinalPath(handle, 1);
        var end = volumePath.IndexOf('}');
        if (!volumePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
            throw new IOException("Cannot determine the volume identity.");
        var volume = volumePath[..(end + 1)].ToUpperInvariant();
        var chain = new List<Identity> { GetIdentity(handle, volume, handles.Count - 1) };
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateLeaf(segment);
            handle = OpenRelative(handle, segment, ReadAttributes | Synchronize | 0x20, ShareReadWrite, 1, DirectoryFile | Synchronous | OpenReparse);
            handles.Add(handle);
            CheckDirectory(handle);
            chain.Add(GetIdentity(handle, volume, handles.Count - 1));
        }
        return chain;
    }

    private static Identity GetIdentity(SafeFileHandle handle, string volume, int index)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Identity(volume, info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow, index);
    }

    private static void CheckDirectory(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
            throw new IOException("Reparse points and non-directories are not supported.");
    }

    internal static string FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot identify the opened directory.");
        var result = buffer.ToString();
        if (flags == 0)
        {
            if (!result.StartsWith(@"\\?\", StringComparison.Ordinal) || result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Not a local DOS path.");
            result = result[4..];
        }
        return result;
    }

    internal static SafeFileHandle CreateTemporary(SafeFileHandle parent, string name) =>
        OpenRelative(parent, name, GenericWrite | Delete | Synchronize, 0, 2, NonDirectoryFile | Synchronous | OpenReparse);

    internal static SafeFileHandle OpenEditTarget(SafeFileHandle parent, string name) =>
        OpenRelative(parent, name, ReadAttributes | Synchronize, ShareReadWrite, 1,
            NonDirectoryFile | Synchronous | OpenReparse);

    internal static (uint VolumeSerial, uint IndexHigh, uint IndexLow) CheckEditTarget(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (0x400u | 0x10u)) != 0)
            throw new IOException("Reparse points and directories cannot be edited.");
        if (info.NumberOfLinks != 1)
            throw new IOException("Files with multiple or unconfirmed hard links cannot be edited.");
        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    internal static void Flush(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool IsTemporary(SafeFileHandle file, SafeFileHandle parent, string name)
    {
        try { return string.Equals(FinalPath(file, 0), Path.Combine(FinalPath(parent, 0), name), StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }
    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options)

    {
        var chars = Marshal.StringToHGlobalUni(name);
        var unicodePtr = IntPtr.Zero;
        try
        {
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = chars };
            unicodePtr = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePtr, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodePtr, Attributes = ObjCaseInsensitive };
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80, share, disposition, options, IntPtr.Zero, 0);
            if (status < 0) { handle?.Dispose(); throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))); }
            return handle;
        }
        finally { if (unicodePtr != IntPtr.Zero) Marshal.FreeHGlobal(unicodePtr); Marshal.FreeHGlobal(chars); }
    }

    internal static void SetDelete(SafeFileHandle handle, bool value)
    {
        var disposition = value ? (byte)1 : (byte)0;
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void Rename(SafeFileHandle file, string name, bool replace = false)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var offset = IntPtr.Size == 8 ? 20 : 12;
        // Native FILE_RENAME_INFORMATION: sizeof(struct) plus the filename bytes.
        var size = checked((IntPtr.Size == 8 ? 24 : 16) + nameBytes.Length);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            if (replace) Marshal.WriteByte(buffer, 0, 1);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + offset, nameBytes.Length);
            // The source was created relative to the held parent. A null RootDirectory and
            // simple name select that same parent, not the process working directory.
            var status = NtSetInformationFile(file, out _, buffer, (uint)size, 10);
            if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status; public IntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime; public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, StringBuilder? volume, int volumeLength, IntPtr serial, IntPtr maxComponent, IntPtr flags, StringBuilder filesystem, int filesystemLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);
}
