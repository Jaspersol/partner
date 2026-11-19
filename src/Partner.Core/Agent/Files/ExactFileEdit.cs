using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;

namespace Partner.Core.Agent.Files;

/// <summary>
/// Prepares an exact UTF-8 edit on local Windows NTFS, under both read and write roots.
/// The caller must separately authorize and approve each operation before Commit.
/// </summary>
public static class ExactFileEdit
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static PreparedFileEdit Prepare(string path, string expectedSha256, string oldText, string newText,
        IEnumerable<string> readRoots, IEnumerable<string> writeRoots, IEnumerable<string> deniedDirectories)
    {
        ArgumentNullException.ThrowIfNull(readRoots);
        ArgumentNullException.ThrowIfNull(writeRoots);
        ArgumentNullException.ThrowIfNull(deniedDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        if (expectedSha256 is null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected SHA-256 must be 64 hexadecimal characters.", nameof(expectedSha256));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Only local Windows NTFS is supported.");
        var full = NativeCreate.ValidatePath(path);
        var name = Path.GetFileName(full);
        NativeCreate.ValidateLeaf(name);
        var handles = new List<SafeFileHandle>();
        try
        {
            var parent = NativeCreate.OpenAuthorizedParent(full, name, readRoots, writeRoots, deniedDirectories, handles);
            byte[] bytes;
            string targetPath;
            string volumePath;
            using (var transaction = WindowsFileEditTransaction.Open(parent, name))
            {
                targetPath = NativeCreate.FinalPath(transaction.File, 0);
                volumePath = transaction.VolumePath;
                bytes = ReadChecked(transaction.File);
            }

            var expected = Convert.FromHexString(expectedSha256);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(bytes, hash);
            if (!hash.SequenceEqual(expected))
                throw new FileEditConflictException("The file version does not match the expected SHA-256.");

            var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            if (bytes.AsSpan().Contains((byte)0))
                throw new InvalidDataException("Binary or non-UTF-8 files cannot be edited.");
            string text;
            try { text = Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("The file is not valid UTF-8.", error); }
            var index = text.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0 || text.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0)
                throw new FileEditMatchException("The old text must occur exactly once.");
            // Valid UTF-8 text cannot match only half a surrogate pair. Encode into one result buffer.
            _ = Utf8.GetByteCount(oldText);
            var prefix = text.AsSpan(0, index);
            var suffix = text.AsSpan(index + oldText.Length);
            var preamble = bom ? Encoding.UTF8.Preamble : ReadOnlySpan<byte>.Empty;
            var byteCount = (long)preamble.Length + Utf8.GetByteCount(prefix) + Utf8.GetByteCount(newText) + Utf8.GetByteCount(suffix);
            if (byteCount > FileReadTool.MaxFileBytes)
                throw new InvalidDataException("Edited file exceeds the read size limit.");
            var content = GC.AllocateUninitializedArray<byte>((int)byteCount);
            preamble.CopyTo(content);
            var written = preamble.Length;
            written += Utf8.GetBytes(prefix, content.AsSpan(written));
            written += Utf8.GetBytes(newText, content.AsSpan(written));
            Utf8.GetBytes(suffix, content.AsSpan(written));
            return new PreparedFileEdit(targetPath, volumePath, name, parent, handles, expected, content);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private static byte[] ReadChecked(SafeFileHandle handle)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(CheckedLength(handle));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new FileEditConflictException("The file ended before its reported length.");
            offset += count;
        }
        return bytes;
    }

    internal static int CheckedLength(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        if (length > FileReadTool.MaxFileBytes) throw new InvalidDataException("File exceeds the read size limit.");
        return (int)length;
    }
}

public sealed class FileEditConflictException(string message) : IOException(message) { }
public sealed class FileEditMatchException(string message) : InvalidOperationException(message) { }
public sealed record FileEditReceipt(string Path, int ByteCount, string Version);

/// <summary>Single-use edit holding the checked directory chain until commit or disposal.</summary>
public sealed class PreparedFileEdit : IDisposable
{
    private readonly string _volumePath;
    private readonly string _name;
    private readonly SafeFileHandle _parent;
    private readonly byte[] _expected;
    private readonly List<SafeFileHandle> _handles;
    private readonly byte[] _content;
    private bool _used;
    private bool _disposed;

    internal PreparedFileEdit(string targetPath, string volumePath, string name, SafeFileHandle parent,
        List<SafeFileHandle> handles, byte[] expected, byte[] content)
    {
        _volumePath = volumePath;
        _name = name;
        _parent = parent;
        _expected = expected;
        _handles = handles;
        TargetPath = targetPath;
        _content = content;
        Version = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    public string TargetPath { get; }
    public int ByteCount => _content.Length;
    public string Version { get; }

    public FileEditReceipt Commit(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_used) throw new InvalidOperationException("This edit has already been used.");
        _used = true;
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = WindowsFileEditTransaction.Open(_parent, _name);
        if (transaction.VolumePath != _volumePath || !VersionMatches(transaction.File, cancellationToken))
            throw new FileEditConflictException("The file changed after it was prepared.");
        cancellationToken.ThrowIfCancellationRequested();
        RandomAccess.Write(transaction.File, _content, 0);
        RandomAccess.SetLength(transaction.File, _content.Length);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new FileEditReceipt(TargetPath, ByteCount, Version);
    }

    private bool VersionMatches(SafeFileHandle handle, CancellationToken ct)
    {
        var length = ExactFileEdit.CheckedLength(handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> chunk = stackalloc byte[8192];
        var offset = 0;
        while (offset < length)
        {
            ct.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(handle, chunk[..Math.Min(chunk.Length, length - offset)], offset);
            if (count == 0) return false;
            hash.AppendData(chunk[..count]);
            offset += count;
        }
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return digest.SequenceEqual(_expected);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var handle in _handles) handle.Dispose();
    }
}
