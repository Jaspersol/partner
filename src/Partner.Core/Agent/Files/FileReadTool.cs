using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Files;

/// <summary>
/// Reads a slice of one text file under the whitelist. Read-only.
/// <para>
/// A slice, not the file: <c>offset</c>/<c>limit</c> count characters of the decoded text, and the answer says
/// how far it got so the model can ask for the next piece.
/// </para>
/// <para>
/// UTF-8 is tried strictly first; text that is not valid UTF-8 is decoded as GB18030, which Chinese Windows
/// tools still write. The answer names the encoding it used. Binary files are refused.
/// </para>
/// </summary>
public sealed class FileReadTool(FileAccessPolicy policy) : ITool
{
    /// <summary>The largest file accepted, in bytes. Beyond it the file is refused, not truncated.</summary>
    public const long MaxFileBytes = 8 * 1024 * 1024;

    public const int DefaultLimit = 12_000;
    public const int MaxLimit = 40_000;

    static FileReadTool()
    {
        // GB18030 lives in the code-pages provider; the runtime does not register it by itself.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ToolSpec Spec { get; } = new(
        "file_read",
        "读一个文本文件的一段内容（只读）。大文件用 offset/limit 分段读；结果会告诉你读到了哪里、怎么接着读。",
        """{"type":"object","properties":{"path":{"type":"string","description":"文件路径"},"offset":{"type":"integer","description":"从第几个字符开始，默认 0"},"limit":{"type":"integer","description":"最多读多少字符，默认 12000，上限 40000"}},"required":["path"]}""");

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        string? path;
        int offset;
        int limit;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            path = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            offset = Number(root, "offset", 0);
            limit = Math.Clamp(Number(root, "limit", DefaultLimit), 1, MaxLimit);
        }
        catch (JsonException error)
        {
            return new ToolResult(false, $"invalid_args: 参数不是合法 JSON（{error.Message}）");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ToolResult(false, "invalid_args: 需要 path");
        }

        var decision = policy.Resolve(path);
        if (!decision.Allowed)
        {
            return new ToolResult(false, $"{FileListTool.Name(decision.Kind)}: {decision.Message}");
        }

        if (Directory.Exists(decision.FullPath))
        {
            return new ToolResult(false, $"invalid_args: '{decision.FullPath}' 是目录，列它用 file_list");
        }

        if (!File.Exists(decision.FullPath))
        {
            return new ToolResult(false, $"not_found: 文件不存在：{decision.FullPath}");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            const int bufferSize = 8192;
            using var linuxHandle = OperatingSystem.IsLinux() ? FileAccessPolicy.OpenLinuxRegularFile(decision.FullPath) : null;
            using var stream = linuxHandle is not null
                ? new FileStream(linuxHandle, FileAccess.Read, bufferSize, isAsync: false)
                : new FileStream(decision.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var opened = policy.ValidateOpenedFile(stream.SafeFileHandle);
            if (!opened.Allowed)
            {
                return new ToolResult(false, $"{FileListTool.Name(opened.Kind)}: {opened.Message}");
            }

            var size = stream.Length;
            if (size > MaxFileBytes)
            {
                return TooLarge(size);
            }

            var bytes = new byte[bufferSize];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // A short read is not necessarily EOF. Collect enough bytes to recognize either BOM before decoding.
            var count = 0;
            while (count < 3)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count, bufferSize - count), ct).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }

            var utf16 = count >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
            var utf8Bom = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var gb = Encoding.GetEncoding("gb18030");
            var primaryEncoding = utf16 ? (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode) : utf8;
            var chars = new char[Math.Max(primaryEncoding.GetMaxCharCount(bufferSize), gb.GetMaxCharCount(bufferSize))];
            var primary = new TextSlice(primaryEncoding, offset, limit);
            // Both decoders consume the same bytes: invalid UTF-8 can occur after any previously read page.
            var fallback = utf16 ? null : new TextSlice(gb, offset, limit);
            var primaryValid = true;
            var binary = false;
            long totalBytes = 0;
            var first = true;
            while (count > 0)
            {
                totalBytes += count;
                if (totalBytes > MaxFileBytes) return TooLarge(totalBytes);
                hash.AppendData(bytes, 0, count);
                if (!utf16 && totalBytes - count < 8192
                    && bytes.AsSpan(0, (int)Math.Min(count, 8192 - (totalBytes - count))).Contains((byte)0))
                {
                    binary = true;
                }

                if (!binary)
                {
                    if (primaryValid)
                    {
                        try
                        {
                            var skip = first ? (utf16 ? 2 : utf8Bom ? 3 : 0) : 0;
                            primary.Append(bytes.AsSpan(skip, count - skip), chars, flush: false);
                        }
                        catch (DecoderFallbackException)
                        {
                            primaryValid = false;
                        }
                    }
                    fallback?.Append(bytes.AsSpan(0, count), chars, flush: false);
                }
                first = false;
                count = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            }

            if (binary) return new ToolResult(false, "binary: 这是二进制文件，不读");
            if (primaryValid)
            {
                try
                {
                    primary.Append(ReadOnlySpan<byte>.Empty, chars, flush: true);
                }
                catch (DecoderFallbackException)
                {
                    primaryValid = false;
                }
            }
            fallback?.Append(ReadOnlySpan<byte>.Empty, chars, flush: true);
            var text = primaryValid ? primary : fallback!;
            var encoding = utf16 ? "UTF-16" : primaryValid ? "UTF-8" : "GB18030";
            return text.Result(decision.FullPath, encoding, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            return new ToolResult(false, $"not_found: 读不了这个文件（{error.Message}）");
        }
    }

    private static ToolResult TooLarge(long size) =>
        new(false, $"too_large: 文件 {size} 字节，超过上限 {MaxFileBytes} 字节，不读");

    /// <summary>Counts decoded UTF-16 characters while retaining only the requested page and its boundaries.</summary>
    private sealed class TextSlice(Encoding encoding, int offset, int limit)
    {
        private readonly Decoder _decoder = encoding.GetDecoder();
        private readonly StringBuilder _page = new();
        private readonly int _start = Math.Max(0, offset - 1);
        private int _length;

        public void Append(ReadOnlySpan<byte> bytes, char[] buffer, bool flush)
        {
            var count = _decoder.GetChars(bytes, buffer, flush);
            var from = Math.Max((long)_start - _length, 0);
            var to = Math.Min((long)count, (long)offset + limit + 1 - _length);
            if (to > from) _page.Append(buffer, (int)from, (int)(to - from));
            _length += count;
        }

        public ToolResult Result(string path, string encoding, string version)
        {
            if (offset >= _length && offset > 0)
            {
                return new ToolResult(true, $"文件 {path}（{encoding}，版本 sha256:{version}，共 {_length} 字符）：offset={offset} 已经超过文件末尾，没有内容。");
            }

            var start = offset - _start;
            if (offset > 0 && offset < _length && char.IsLowSurrogate(_page[start]) && char.IsHighSurrogate(_page[start - 1]))
            {
                return new ToolResult(false, "invalid_args: offset 位于 Unicode 字符中间，请使用上次返回的 offset");
            }
            if (offset < _length && limit == 1 && char.IsHighSurrogate(_page[start])
                && offset + 1 < _length && char.IsLowSurrogate(_page[start + 1]))
            {
                return new ToolResult(false, "invalid_args: limit 太小，无法返回完整的 Unicode 字符，请至少使用 2");
            }

            var end = offset + Math.Min(limit, _length - offset);
            if (end > offset && end < _length && char.IsHighSurrogate(_page[end - 1 - _start]) && char.IsLowSurrogate(_page[end - _start]))
            {
                end--;
            }
            var head = $"文件 {path}（{encoding}，版本 sha256:{version}，共 {_length} 字符，本次 {offset}–{end}）";
            var body = _page.ToString(start, end - offset);
            return new ToolResult(true, end < _length
                ? $"{head}\n{body}\n（还没读完，用 offset={end} 继续）"
                : $"{head}\n{body}");
        }
    }

    private static int Number(JsonElement root, string name, int fallback) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? Math.Max(0, number)
            : fallback;
}
