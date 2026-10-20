namespace Partner.Core.Agent.Files;

/// <summary>Why a path was or was not reachable. The name is also the prefix of the tool's failure text.</summary>
public enum FileAccessKind
{
    Ok,
    OutsideRoots,
    DeniedFile,
    Invalid,
}

/// <summary>The answer for one path: where it really is, or why it may not be touched.</summary>
public sealed record FileAccessDecision(FileAccessKind Kind, string FullPath, string Message)
{
    public bool Allowed => Kind == FileAccessKind.Ok;
}

/// <summary>
/// Where the file tools may go: a whitelist of directories, with links followed to where they really lead.
/// <para>
/// A path that merely <i>starts with</i> an allowed root is not enough. <c>..</c> segments are collapsed first,
/// and every directory or file on the way down is checked for being a link (symlink or junction). A link that
/// cannot be resolved is refused — the check fails closed.
/// </para>
/// <para>
/// On top of the whitelist sits a short list of things that are never readable even inside it: key and
/// credential files by name, and whole directories the host names (its own settings hold the model's API key).
/// </para>
/// </summary>
public sealed class FileAccessPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly string[] DeniedExtensions = [".pem", ".key", ".pfx", ".p12", ".ppk", ".kdbx"];
    private static readonly string[] DeniedNamePrefixes = ["id_rsa", "id_ed25519", "id_ecdsa", "id_dsa"];

    private readonly IReadOnlyList<string> _roots;
    private readonly IReadOnlyList<string> _realRoots;
    private readonly IReadOnlyList<string> _deniedDirectories;

    public FileAccessPolicy(IEnumerable<string> roots, IEnumerable<string>? deniedDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _roots = roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A root may itself be a link. Files reached through it resolve to the real directory, so the real
        // location is accepted as well — but only for roots the person wrote.
        _realRoots = _roots.Select(root => ResolveLinks(root) ?? root).ToList();
        _deniedDirectories = (deniedDirectories ?? Array.Empty<string>())
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))
            .ToList();
    }

    /// <summary>The configured roots, in the order given. The first one anchors relative paths.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Decides whether <paramref name="path"/> may be touched. Relative paths start at the first root.</summary>
    public FileAccessDecision Resolve(string? path)
    {
        if (_roots.Count == 0)
        {
            return new(FileAccessKind.OutsideRoots, string.Empty, "没有配置允许读取的目录");
        }

        string full;
        try
        {
            var text = string.IsNullOrWhiteSpace(path) ? _roots[0] : path.Trim();
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(_roots[0], text)));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(FileAccessKind.Invalid, string.Empty, $"路径不合法：{error.Message}");
        }

        if (!IsUnderAnyRoot(full))
        {
            return Outside(full);
        }

        // Walk down from the root that contains it, following every link on the way.
        var root = _roots.First(candidate => IsUnder(full, candidate));
        var current = root;
        foreach (var segment in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            var real = ResolveLinks(current);
            if (real is null)
            {
                return new(FileAccessKind.OutsideRoots, full, $"无法确认 '{current}' 这个链接指向哪里，按规则拒绝");
            }

            if (!IsUnderAnyRoot(real))
            {
                return Outside(real, viaLink: true);
            }

            current = real;
        }

        return DeniedReason(current) is { } denied
            ? new(FileAccessKind.DeniedFile, current, denied)
            : new(FileAccessKind.Ok, current, string.Empty);
    }

    private FileAccessDecision Outside(string path, bool viaLink = false) => new(
        FileAccessKind.OutsideRoots,
        path,
        (viaLink ? $"'{path}' 是链接的真实位置，" : $"'{path}' ")
        + $"不在允许读取的目录内。允许的目录：{string.Join("；", _roots)}");

    private string? DeniedReason(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || DeniedExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            || DeniedNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{name}' 属于密钥或凭据类文件，不允许读取";
        }

        return _deniedDirectories.Any(directory => IsUnder(path, directory))
            ? "这个位置是本程序自己的数据目录（里面有设置和密钥），不允许读取"
            : null;
    }

    private bool IsUnderAnyRoot(string path) =>
        _roots.Any(root => IsUnder(path, root)) || _realRoots.Any(root => IsUnder(path, root));

    private static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, PathComparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>The real location of a path whose leaf may be a link; null when a link cannot be resolved.</summary>
    private static string? ResolveLinks(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                return path;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? null : Path.TrimEndingDirectorySeparator(target.FullName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
