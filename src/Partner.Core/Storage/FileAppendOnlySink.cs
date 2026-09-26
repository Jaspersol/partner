using System.Runtime.InteropServices;
using System.Text;

namespace Partner.Core.Storage;

/// <summary>
/// File-backed <see cref="IAppendOnlySink"/>: buffered appends, explicit flush-to-disk, torn-tail repair.
/// </summary>
public sealed class FileAppendOnlySink : IAppendOnlySink
{
    private readonly string _path;
    private readonly FileStream _stream;
    private readonly List<byte> _buffer = new();
    private bool _disposed;

    public FileAppendOnlySink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            // Single writer by construction; readers may tail the file.
            FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
        DurableLength = _stream.Length;
    }

    public string FilePath => _path;

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
