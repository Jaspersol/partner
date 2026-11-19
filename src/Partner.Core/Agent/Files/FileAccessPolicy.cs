using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
    private readonly IReadOnlyList<string?> _canonicalRoots;
    private readonly IReadOnlyList<string?> _canonicalDeniedDirectories;

    public FileAccessPolicy(IEnumerable<string> roots, IEnumerable<string>? deniedDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _roots = roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

        // A root may itself be a link. Files reached through it resolve to the real directory, so the real
        // location is accepted as well — but only for roots the person wrote.
        _realRoots = _roots.Select(root => ResolveLinks(root) ?? root).ToList();
        _deniedDirectories = (deniedDirectories ?? Array.Empty<string>())
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))
            .SelectMany(directory => new[] { directory, ResolveLinks(directory) ?? directory })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        // Capture targets at policy creation, not after opening a requested object.
        // A subsequently retargeted root must not grant access to its new target.
        _canonicalRoots = _roots.Select(CanonicalDirectory).ToList();
        _canonicalDeniedDirectories = _deniedDirectories.Select(CanonicalDirectory).ToList();
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

        // Alternate data streams can disguise a protected file name (for example .env::$DATA).
        if (OperatingSystem.IsWindows() && full[Path.GetPathRoot(full)!.Length..].Contains(':'))
        {
            return new(FileAccessKind.Invalid, full, "不支持 Windows 备用数据流路径");
        }

        if (!IsUnderAnyRoot(full))
        {
            return Outside(full);
        }
        if (DeniedReason(full) is { } requestedDenied)
        {
            return new(FileAccessKind.DeniedFile, full, requestedDenied);
        }


        // A configured root and its resolved target are both accepted as input paths.
        var root = _roots.Concat(_realRoots).First(candidate => IsUnder(full, candidate));
        var current = ResolveLinks(root);
        if (current is null)
        {
            return new(FileAccessKind.OutsideRoots, full, $"无法确认 '{root}' 这个链接指向哪里，按规则拒绝");
        }
        if (!IsUnderAnyRoot(current))
        {
            return Outside(current, viaLink: true);
        }

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

    /// <summary>Checks the object actually opened before reading it; hard links remain aliases.</summary>
    internal FileAccessDecision ValidateOpenedFile(SafeFileHandle handle)
    {
        var actual = handle.IsInvalid || handle.IsClosed ? null : FinalPath(handle);
        if (actual is null || _canonicalRoots.Any(root => root is null)
            || _canonicalDeniedDirectories.Any(directory => directory is null))
        {
            return new(FileAccessKind.OutsideRoots, string.Empty, "无法确认打开文件或受保护目录的真实位置，按规则拒绝");
        }

        if (!_canonicalRoots.Any(root => IsUnder(actual, root!))) return Outside(actual, viaLink: true);
        if (DeniedReason(actual) is { } denied) return new(FileAccessKind.DeniedFile, actual, denied);
        // Check both the original targets and current targets, so a changed directory junction cannot
        // unprotect the original target or hide its current target behind a different input path.
        for (var i = 0; i < _deniedDirectories.Count; i++)
        {
            var current = CanonicalDirectory(_deniedDirectories[i]);
            if (current is null)
                return new(FileAccessKind.OutsideRoots, actual, "无法确认受保护目录的真实位置，按规则拒绝");
            if (IsUnder(actual, _canonicalDeniedDirectories[i]!) || IsUnder(actual, current))
                return new(FileAccessKind.DeniedFile, actual, "这个位置是本程序自己的数据目录（里面有设置和密钥），不允许读取");
        }

        return new(FileAccessKind.Ok, actual, string.Empty);
    }

    private static string? CanonicalDirectory(string directory)
    {
        // Missing directories are anchored at their nearest existing ancestor, so they remain protected
        // if created later. Other errors fail closed instead of guessing a canonical name.
        var missing = new Stack<string>();
        var current = directory;
        while (true)
        {
            using var handle = OpenDirectory(current, enumerate: false);
            if (!handle.IsInvalid)
            {
                var final = FinalPath(handle);
                if (final is null) return null;
                while (missing.Count > 0) final = Path.Combine(final, missing.Pop());
                return Path.TrimEndingDirectorySeparator(final);
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3) || (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())) return null;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) return null;
            missing.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var target = new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}").LinkTarget;
                return target is not null && Path.IsPathFullyQualified(target) && !target.EndsWith(" (deleted)", StringComparison.Ordinal)
                    ? Path.TrimEndingDirectorySeparator(target) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        if (!OperatingSystem.IsWindows()) return null;

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return null;
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        else
            return null;
        return Path.TrimEndingDirectorySeparator(path);
    }

    // Open the directory itself, not a path later reopened for enumeration.
    internal static SafeFileHandle OpenDirectory(string path, bool enumerate)
    {
        if (OperatingSystem.IsWindows())
            return CreateFile(path, enumerate ? 1u : 0u, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (OperatingSystem.IsLinux())
            return new SafeFileHandle((IntPtr)Open(path, 0x200000 | 0x10000 | 0x80000), ownsHandle: true);
        return new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
    }

    // O_PATH inspects the inode without opening a FIFO or device for I/O. Reopen the held inode,
    // not the original pathname, only after proving that it is a regular file.
    internal static SafeFileHandle OpenLinuxRegularFile(string path)
    {
        using var inspected = new SafeFileHandle((IntPtr)Open(path, 0x200000 | 0x80000), ownsHandle: true);
        if (inspected.IsInvalid) throw new IOException("无法打开文件");
        var stat = Marshal.AllocHGlobal(256);
        try
        {
            if (Statx(inspected.DangerousGetHandle().ToInt32(), "", 0x1000, 1, stat) != 0
                || (Marshal.ReadInt32(stat, 0) & 1) == 0
                || ((ushort)Marshal.ReadInt16(stat, 28) & 0xf000) != 0x8000)
                throw new IOException("只支持读取普通文件");
        }
        finally
        {
            Marshal.FreeHGlobal(stat);
        }

        var handle = new SafeFileHandle((IntPtr)Open($"/proc/self/fd/{inspected.DangerousGetHandle().ToInt64()}",
            0x800 | 0x80000), ownsHandle: true);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new IOException("无法打开文件");
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, IntPtr stat);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

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
