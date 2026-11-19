using System.Security.Cryptography;
using System.Text;
using Partner.Core.Agent.Files;

namespace Partner.Core.Tests.Agent.Files;

public sealed class WindowsSafeFileCreateTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "partner-create-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;

    public WindowsSafeFileCreateTests()
    {
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    private static bool Supported => OperatingSystem.IsWindows()
        && new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    private PreparedFileCreate Prepare(string path, byte[]? content = null, string[]? denied = null) =>
        WindowsSafeFileCreate.Prepare(path, content ?? Encoding.UTF8.GetBytes("hello 世界"), [_root], [_root], denied ?? []);

    [Fact]
    public void Creates_exact_bytes_and_receipt_without_bom()
    {
        if (!Supported) return;
        var path = Path.Combine(_root, "result.txt");
        var bytes = Encoding.UTF8.GetBytes("hello 世界");
        using var operation = Prepare(path, bytes);
        bytes[0] = 0;
        var receipt = operation.Commit();
        var expected = Encoding.UTF8.GetBytes("hello 世界");
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(path, receipt.Path, ignoreCase: true);
        Assert.Equal(expected.Length, receipt.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), receipt.Version);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Existing_or_concurrently_created_destination_is_never_overwritten()
    {
        if (!Supported) return;
        var path = Path.Combine(_root, "existing.txt");
        using var operation = Prepare(path);
        File.WriteAllText(path, "external");
        Assert.Throws<FileCreateConflictException>(() => operation.Commit());
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Cancellation_leaves_no_target_or_temporary_file()
    {
        if (!Supported) return;
        var path = Path.Combine(_root, "cancel.txt");
        using var operation = Prepare(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => operation.Commit(cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Both_roots_and_protected_names_and_directories_are_enforced()
    {
        if (!Supported) return;
        var path = Path.Combine(_root, "target.txt");
        Assert.Throws<UnauthorizedAccessException>(() => WindowsSafeFileCreate.Prepare(path, [], [_root], [_outside], []));
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(Path.Combine(_outside, "target.txt")));
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(Path.Combine(_root, ".env.production")));
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(path, denied: [_root]));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Rejects_relative_special_and_missing_parent_paths()
    {
        if (!Supported) return;
        Assert.Throws<ArgumentException>(() => Prepare("relative.txt"));
        Assert.Throws<ArgumentException>(() => Prepare(Path.Combine(_root, "file.txt:stream")));
        Assert.Throws<ArgumentException>(() => Prepare(Path.Combine(_root, "..", "other.txt")));
        var error = Assert.Throws<System.ComponentModel.Win32Exception>(() => Prepare(Path.Combine(_root, "missing", "file.txt")));
        Assert.Equal(2, error.NativeErrorCode);
    }

    [Fact]
    public void Held_parent_cannot_be_moved_before_commit()
    {
        if (!Supported) return;
        var parent = Path.Combine(_root, "parent");
        Directory.CreateDirectory(parent);
        using var operation = Prepare(Path.Combine(parent, "file.txt"));
        Assert.ThrowsAny<IOException>(() => Directory.Move(parent, Path.Combine(_outside, "moved")));
        operation.Commit();
        Assert.True(File.Exists(Path.Combine(parent, "file.txt")));
    }

    [Fact]
    public void Rejects_directory_symbolic_links_even_when_they_point_inside_a_root()
    {
        if (!Supported) return;
        var target = Path.Combine(_root, "real");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        var rejection = Assert.Throws<IOException>(() => Prepare(Path.Combine(link, "file.txt")));
        Assert.Contains("Reparse points", rejection.Message);
        Assert.Empty(Directory.GetFiles(target));
        Directory.Delete(link);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }
}
