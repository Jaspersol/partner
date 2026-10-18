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
    /// <summary>More entries than this are summarised, not listed: a directory of thousands of files is noise.</summary>
    public const int MaxEntries = 200;

    public ToolSpec Spec { get; } = new(
        "file_list",
        "列出一个目录里的文件和子目录（只读）。path 可以是绝对路径，或相对于第一个允许目录的路径；不填就是第一个允许目录。",
        """{"type":"object","properties":{"path":{"type":"string","description":"要列出的目录"}}}""");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        string? path;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            path = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("path", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
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

        try
        {
            var entries = new DirectoryInfo(decision.FullPath)
                .EnumerateFileSystemInfos()
                .OrderByDescending(entry => entry is DirectoryInfo)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var text = new StringBuilder();
            text.Append("目录 ").Append(decision.FullPath).Append("（共 ").Append(entries.Count).Append(" 项");
            text.Line(entries.Count > MaxEntries ? $"，只列前 {MaxEntries} 项）" : "）");

            foreach (var entry in entries.Take(MaxEntries))
            {
                text.Line(entry is FileInfo file ? $"f  {file.Name}  {file.Length} 字节" : $"d  {entry.Name}/");
            }

            return Task.FromResult(new ToolResult(true, text.ToString().TrimEnd()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ToolResult(false, $"not_found: 读不了这个目录（{error.Message}）"));
        }
    }

    internal static string Name(FileAccessKind kind) => kind switch
    {
        FileAccessKind.OutsideRoots => "outside_roots",
        FileAccessKind.DeniedFile => "denied_file",
        _ => "invalid_args",
    };
}
