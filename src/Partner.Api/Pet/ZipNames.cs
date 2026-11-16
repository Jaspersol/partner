using System.Buffers.Binary;
using System.Text;

namespace Partner.Api.Pet;

/// <summary>
/// The entry names of a zip, read from the central directory as raw bytes. <c>ZipArchiveEntry.FullName</c> decodes
/// every name as UTF-8 even when the UTF-8 flag is clear, which turns GBK names written by Chinese Windows tools
/// into replacement characters. The names are paired with <c>ZipArchive.Entries</c> by index: both enumerate in
/// central-directory order.
/// </summary>
internal static class ZipNames
{
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint CentralFileHeader = 0x02014b50;

    /// <summary>Bit 11 of the general-purpose flags: the name is UTF-8.</summary>
    private const ushort Utf8Flag = 0x800;

    /// <summary>The code page Chinese Windows tools write when they do not claim UTF-8.</summary>
    private const int GbkCodePage = 936;

    static ZipNames()
    {
        // GBK lives in the code-pages provider; the runtime does not register it by itself.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Every entry name, in central-directory order. Returns an empty list when the archive is not a plain zip
    /// (zip64 or a truncated tail), which the caller treats as "cannot read this archive" rather than guessing.
    /// </summary>
    public static IReadOnlyList<string> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static IReadOnlyList<string> Read(Stream stream)
    {
        var eocd = FindEndOfCentralDirectory(stream);
        if (eocd is null)
        {
            return [];
        }

        var (position, count, directoryOffset) = eocd.Value;
        if (count == 0)
        {
            return [];
        }

        if (position < directoryOffset)
        {
            return [];
        }

        stream.Position = directoryOffset;
        var names = new List<string>(count);
        var header = new byte[46];

        for (var index = 0; index < count; index++)
        {
            if (!ReadExactly(stream, header, 46)
                || BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralFileHeader)
            {
                return [];
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));

            var nameBytes = new byte[nameLength];
            if (!ReadExactly(stream, nameBytes, nameLength))
            {
                return [];
            }

            names.Add(Decode(nameBytes, flags));

            // Skip the extra field and the comment; the next header follows them.
            stream.Seek(extraLength + commentLength, SeekOrigin.Current);
        }

        return names;
    }

    /// <summary>
    /// UTF-8 when the entry says so, GBK when it does not — the flag being clear means "the local code page",
    /// and for the archives this feature receives that is 936. A name that is neither is still decoded as GBK,
    /// with replacement characters, rather than dropped: the import then fails on the name instead of silently
    /// missing a file the model references.
    /// </summary>
    private static string Decode(byte[] name, ushort flags) =>
        (flags & Utf8Flag) != 0
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(name)
            : Encoding.GetEncoding(GbkCodePage).GetString(name);

    /// <summary>
    /// Locates the end-of-central-directory record. The comment after it is unbounded in the format but capped at
    /// 65535 bytes in practice, so the scan starts that far back and walks forward.
    /// </summary>
    private static (long Position, int Count, long DirectoryOffset)? FindEndOfCentralDirectory(Stream stream)
    {
        var length = stream.Length;
        var window = (int)Math.Min(length, 65_535 + 22);
        if (window < 22)
        {
            return null;
        }

        var buffer = new byte[window];
        stream.Position = length - window;
        if (!ReadExactly(stream, buffer, window))
        {
            return null;
        }

        for (var offset = window - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)) != EndOfCentralDirectory)
            {
                continue;
            }

            var count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 10));
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 16));

            // 0xFFFFFFFF means the value lives in a zip64 record, which this reader does not follow.
            if (directoryOffset == uint.MaxValue)
            {
                return null;
            }

            return (length - window + offset, count, directoryOffset);
        }

        return null;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var chunk = stream.Read(buffer, read, count - read);
            if (chunk <= 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }
}