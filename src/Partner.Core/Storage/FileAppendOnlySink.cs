using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace Partner.Core.Storage;

/// <summary>
/// File-backed <see cref="IAppendOnlySink"/>: buffered appends, explicit flush-to-disk, torn-tail repair.
/// </summary>
public sealed class FileAppendOnlySink : IAppendOnlySink
{
    private readonly string _path;
    private FileStream _stream;
    private readonly List<byte> _buffer = new();
    private bool _disposed;
    private readonly Action<string, string>? _replaceForTest;
    private readonly Func<string, FileStream>? _reopenForTest;

    public FileAppendOnlySink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _stream = OpenStream(path);
        DurableLength = _stream.Length;
    }

    internal FileAppendOnlySink(string path, Action<string, string> replace, Func<string, FileStream> reopen) : this(path)
    {
        _replaceForTest = replace;
        _reopenForTest = reopen;
    }

    /// <summary>Single writer by construction; readers may tail the file.</summary>
    private static FileStream OpenStream(string path, bool create = true)
    {
        var stream = new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    public long Length => DurableLength + _buffer.Count;

    public long DurableLength { get; private set; }

    public void Append(string record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        // A record must be a single line; embedded newlines would corrupt the framing.
        if (record.Contains('\n') || record.Contains('\r'))
        {
            throw new ArgumentException("Record must not contain newline characters.", nameof(record));
        }

        var bytes = Encoding.UTF8.GetBytes(record);
        _buffer.AddRange(bytes);
        _buffer.Add((byte)'\n');
    }

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_buffer.Count > 0)
        {
            _stream.Write(CollectionsMarshal.AsSpan(_buffer));
            _buffer.Clear();
        }

        // Flush(true) == FlushFileBuffers: the durability barrier.
        _stream.Flush(flushToDisk: true);
        DurableLength = _stream.Length;
    }

    public IReadOnlyList<string> ReadLines()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var lines = new List<string>();
        if (_stream.Length == 0)
        {
            return lines;
        }

        using var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader, Encoding.UTF8);

        string? line;
        while ((line = text.ReadLine()) is not null)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>Streams complete UTF-8 lines from a byte offset at a line boundary.</summary>
    public IEnumerable<(long Offset, string Line)> ReadLinesFrom(long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        reader.Seek(offset, SeekOrigin.Begin);
        using var line = new MemoryStream();
        var buffer = new byte[8192];
        var lineOffset = offset;
        long position = offset;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                var length = checked((int)line.Length);
                var bytes = line.GetBuffer();
                if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                var first = lineOffset == 0 && length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                if (length > first) yield return (lineOffset, Encoding.UTF8.GetString(bytes, first, length - first));
                line.SetLength(0);
                lineOffset = position + i + 1;
                start = i + 1;
            }
            line.Write(buffer, start, count - start);
            position += count;
        }
    }

    public void TruncateTo(long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _buffer.Clear();
        _stream.SetLength(length);
        _stream.Flush(flushToDisk: true);
        _stream.Seek(0, SeekOrigin.End);
        DurableLength = _stream.Length;
    }

    /// <summary>
    /// Drops an unterminated final line (crash during append) and returns how many bytes were removed.
    /// Returns 0 when the file already ends on a line boundary.
    /// </summary>
    public long RepairTail()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stream.Length == 0)
        {
            return 0;
        }

        using var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[reader.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = reader.Read(bytes, read, bytes.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        var lastNewline = -1;
        for (var i = read - 1; i >= 0; i--)
        {
            if (bytes[i] == (byte)'\n')
            {
                lastNewline = i;
                break;
            }
        }

        var goodLength = lastNewline + 1L;
        if (goodLength == read)
        {
            return 0;
        }

        var removed = read - goodLength;
        TruncateTo(goodLength);
        return removed;
    }

    /// <summary>
    /// Writes the survivors to a sibling file and swaps it in. Truncate-then-rewrite would be shorter, but a
    /// crash between the two would leave a mix of old and new lines — each still a valid record, so nothing
    /// downstream could tell the log had been corrupted.
    /// </summary>
    public void Rewrite(IReadOnlyList<string> records)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.Contains('\n') || record.Contains('\r'))
            {
                throw new ArgumentException("Record must not contain newline characters.", nameof(records));
            }
        }

        var temp = _path + ".rewrite";

        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var record in records)
            {
                file.Write(Encoding.UTF8.GetBytes(record));
                file.WriteByte((byte)'\n');
            }

            file.Flush(flushToDisk: true);
        }

        _stream.Dispose();

        try
        {
            // Windows POSIX rename preserves readers of the previous file generation.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (_replaceForTest is not null) _replaceForTest(temp, _path);
                    else if (OperatingSystem.IsWindows()) ReplaceOpenFileOnWindows(temp, _path);
                    else File.Move(temp, _path, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
                catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(25); }
            }
        }
        catch (Exception replaceError)
        {
            try
            {
                _stream = _reopenForTest?.Invoke(_path) ?? OpenStream(_path, create: false);
                if (_stream.Length != DurableLength)
                {
                    _stream.Dispose();
                    throw new IOException("The original session file changed during replacement.");
                }
                _stream.Seek(0, SeekOrigin.End);
            }
            catch (Exception reopenError)
            {
                throw new AggregateException("Session replacement and reopening the original file failed.", replaceError, reopenError);
            }

            throw new RewriteRecoveredException(replaceError);
        }

        // After replacement, a failed reopen leaves the outcome committed but unusable; the store must fault.
        _stream = _reopenForTest?.Invoke(_path) ?? OpenStream(_path, create: false);
        _buffer.Clear();
        DurableLength = _stream.Length;
    }

    private static void ReplaceOpenFileOnWindows(string source, string destination)
    {
        const uint delete = 0x00010000;
        const uint genericWrite = 0x40000000;
        const uint shareReadWriteDelete = 7;
        const uint openExisting = 3;
        using var handle = CreateFile(source, delete | genericWrite, shareReadWriteDelete,
            IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Could not open replacement file for rename.", new Win32Exception());
        if (!FlushFileBuffers(handle))
            throw new IOException("Could not flush replacement session file.", new Win32Exception());

        var name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var nameOffset = (int)Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileName));
        var size = checked(nameOffset + name.Length + sizeof(char));
        var info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(info, 3); // FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS
            Marshal.WriteIntPtr(info, (int)Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.RootDirectory)), IntPtr.Zero);
            Marshal.WriteInt32(info, (int)Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileNameLength)), name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
            Marshal.WriteInt16(info, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, 22, info, (uint)size)) // FileRenameInfoEx
                throw new IOException("Could not atomically replace session file.", new Win32Exception());
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}

internal sealed class RewriteRecoveredException(Exception inner) : IOException("Session replacement failed; the original file was reopened.", inner);
