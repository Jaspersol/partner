using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Files;

/// <summary>
/// Lists one directory under the whitelist. Read-only.
/// <para>
/// Failures are answered as <c>&lt;kind&gt;: message</c> (<c>not_found</c>, <c>outside_roots</c>,
/// <c>denied_file</c>, <c>invalid_args</c>) so the job runner can tell "keep looking" from "this is a rule".
/// </para>
/// </summary>
public sealed class FileListTool(FileAccessPolicy policy) : ITool
{
    /// <summary>Maximum number of entries returned per page.</summary>
    public const int MaxEntries = 200;

    public ToolSpec Spec { get; } = new(
        "file_list",
        "列出一个目录里的文件和子目录（只读）。path 可为绝对路径或相对于第一个允许目录；不填则列出第一个允许目录。按目录在前、名称排序，使用 offset 和 limit 翻页；目录变化时位置可能偏移。",
        """{"type":"object","properties":{"path":{"type":"string","description":"要列出的目录"},"offset":{"type":"integer","minimum":0,"description":"从排序后的第几项开始，默认 0"},"limit":{"type":"integer","minimum":1,"maximum":200,"description":"每页最多返回的条目数，默认 200"}}}""");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        string? path;
        var offset = 0;
        var limit = MaxEntries;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            path = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("path", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("offset", out var offsetValue)
                    && (offsetValue.ValueKind != JsonValueKind.Number || !offsetValue.TryGetInt32(out offset) || offset < 0))
                {
                    return Task.FromResult(new ToolResult(false, "invalid_args: offset 必须是非负整数"));
                }

                if (document.RootElement.TryGetProperty("limit", out var limitValue)
                    && (limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out limit) || limit < 1 || limit > MaxEntries))
                {
                    return Task.FromResult(new ToolResult(false, $"invalid_args: limit 必须是 1 到 {MaxEntries} 的整数"));
                }
            }
        }
        catch (JsonException error)
        {
            return Task.FromResult(new ToolResult(false, $"invalid_args: 参数不是合法 JSON（{error.Message}）"));
        }

        var decision = policy.Resolve(path);
        if (!decision.Allowed)
        {
            return Task.FromResult(new ToolResult(false, $"{Name(decision.Kind)}: {decision.Message}"));
        }

        if (File.Exists(decision.FullPath))
        {
            return Task.FromResult(new ToolResult(false, $"invalid_args: '{decision.FullPath}' 是文件，读它用 file_read"));
        }

        if (!Directory.Exists(decision.FullPath))
        {
            return Task.FromResult(new ToolResult(false, $"not_found: 目录不存在：{decision.FullPath}"));
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return Task.FromResult(new ToolResult(false, "outside_roots: 无法安全验证此平台上的目录，按规则拒绝"));

        try
        {
            using var handle = FileAccessPolicy.OpenDirectory(decision.FullPath, enumerate: true);
            if (handle.IsInvalid)
                return Task.FromResult(new ToolResult(false, "not_found: 读不了这个目录"));
            var opened = policy.ValidateOpenedFile(handle);
            if (!opened.Allowed)
                return Task.FromResult(new ToolResult(false, $"{Name(opened.Kind)}: {opened.Message}"));

            var entries = Enumerate(handle)
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();

            var text = new StringBuilder();
            text.Append("目录 ").Append(decision.FullPath).Append("（共 ").Append(entries.Count).Append(" 项");
            text.Line(offset == 0 && entries.Count > limit
                ? $"，只列前 {limit} 项）"
                : $"，从第 {offset} 项起最多列 {limit} 项）");

            foreach (var entry in entries.Skip(offset).Take(limit))
            {
                text.Line(entry.IsDirectory ? $"d  {entry.Name}/" : $"f  {entry.Name}  {entry.Length} 字节");
            }

            if (offset < entries.Count && entries.Count - offset > limit)
            {
                text.Line($"下一页 offset: {offset + limit}");
            }

            return Task.FromResult(new ToolResult(true, text.ToString().TrimEnd()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            return Task.FromResult(new ToolResult(false, $"not_found: 读不了这个目录（{error.Message}）"));
        }
    }

    internal sealed record Entry(string Name, bool IsDirectory, long Length);

    internal static IEnumerable<Entry> Enumerate(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            // Resolve names relative to the held directory, and never follow an entry symlink for metadata.
            var path = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
            var stat = Marshal.AllocHGlobal(256);
            try
            {
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    if (Statx(handle.DangerousGetHandle().ToInt32(), entry.Name, 0x100, 0x201, stat) != 0
                        || (Marshal.ReadInt32(stat, 0) & 0x201) != 0x201)
                        throw new IOException("无法确认目录条目的元数据");
                    var mode = (ushort)Marshal.ReadInt16(stat, 28);
                    yield return new Entry(entry.Name, (mode & 0xf000) == 0x4000, Marshal.ReadInt64(stat, 40));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(stat);
            }
            yield break;
        }

        if (!OperatingSystem.IsWindows()) throw new IOException("无法安全列举此平台上的目录");
        // FILE_ID_BOTH_DIR_INFO is returned directly from the validated directory handle.
        const int capacity = 65536;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var infoClass = 11; // FileIdBothDirectoryRestartInfo
            while (GetFileInformationByHandleEx(handle, infoClass, buffer, capacity))
            {
                infoClass = 10; // FileIdBothDirectoryInfo
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > capacity - 104) throw new IOException("目录条目格式不合法");
                    var item = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(item, 0);
                    var nameBytes = Marshal.ReadInt32(item, 60);
                    if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > capacity - offset - 104
                        || (next != 0 && (next < 104 + nameBytes || next > capacity - offset)))
                        throw new IOException("目录条目格式不合法");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(item, 104), nameBytes / 2)!;
                    if (name is not ("." or ".."))
                        yield return new Entry(name, (Marshal.ReadInt32(item, 56) & 0x10) != 0, Marshal.ReadInt64(item, 40));
                    if (next == 0) break;
                    offset += next;
                }
            }
            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new IOException("读不了这个目录", new Win32Exception(error));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr buffer, int size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, IntPtr stat);

    internal static string Name(FileAccessKind kind) => kind switch
    {
        FileAccessKind.OutsideRoots => "outside_roots",
        FileAccessKind.DeniedFile => "denied_file",
        _ => "invalid_args",
    };
}
