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
    /// <summary>The largest file read into memory to be sliced. Beyond it the file is refused, not truncated.</summary>
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

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
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
            return Task.FromResult(new ToolResult(false, $"invalid_args: 参数不是合法 JSON（{error.Message}）"));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(new ToolResult(false, "invalid_args: 需要 path"));
        }

        var decision = policy.Resolve(path);
        if (!decision.Allowed)
        {
            return Task.FromResult(new ToolResult(false, $"{FileListTool.Name(decision.Kind)}: {decision.Message}"));
        }

        if (Directory.Exists(decision.FullPath))
        {
            return Task.FromResult(new ToolResult(false, $"invalid_args: '{decision.FullPath}' 是目录，列它用 file_list"));
        }

        if (!File.Exists(decision.FullPath))
        {
            return Task.FromResult(new ToolResult(false, $"not_found: 文件不存在：{decision.FullPath}"));
        }

        try
        {
            var size = new FileInfo(decision.FullPath).Length;
            if (size > MaxFileBytes)
            {
                return Task.FromResult(new ToolResult(
                    false, $"too_large: 文件 {size} 字节，超过上限 {MaxFileBytes} 字节，不读"));
            }

            var bytes = File.ReadAllBytes(decision.FullPath);
            var decoded = Decode(bytes);
            if (decoded is null)
            {
                return Task.FromResult(new ToolResult(false, "binary: 这是二进制文件，不读"));
            }

            var (text, encoding) = decoded.Value;
            return Task.FromResult(new ToolResult(true, Slice(decision.FullPath, text, encoding, offset, limit)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ToolResult(false, $"not_found: 读不了这个文件（{error.Message}）"));
        }
    }

    private static string Slice(string path, string text, string encoding, int offset, int limit)
    {
        if (offset >= text.Length && text.Length > 0)
        {
            return $"文件 {path}（{encoding}，共 {text.Length} 字符）：offset={offset} 已经超过文件末尾，没有内容。";
        }

        var end = Math.Min(text.Length, offset + limit);
        var head = $"文件 {path}（{encoding}，共 {text.Length} 字符，本次 {offset}–{end}）";
        var body = text[offset..end];
        return end < text.Length
            ? $"{head}\n{body}\n（还没读完，用 offset={end} 继续）"
            : $"{head}\n{body}";
    }

    /// <summary>Text and the name of the encoding that produced it, or null for a binary file.</summary>
    private static (string Text, string Encoding)? Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            // A UTF-16 byte-order mark: the NULs in it are encoding, not binary.
            var utf16 = bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            return (utf16.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        }

        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            return null;
        }

        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding("gb18030").GetString(bytes), "GB18030");
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
