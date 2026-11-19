using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Partner.Core.Agent.Files;

namespace Partner.Core.Tests.Agent.Files;

public sealed class ExactFileEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partner-edit-tests", Guid.NewGuid().ToString("N"));

    public ExactFileEditTests()
    {
        Directory.CreateDirectory(_root);
    }

    private static bool Supported => OperatingSystem.IsWindows()
        && new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    private PreparedFileEdit Prepare(string path, string hash, string oldText, string newText) =>
        ExactFileEdit.Prepare(path, hash, oldText, newText, [_root], [_root], []);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private string Put(byte[] bytes)
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Replaces_default_stream_without_changing_named_streams()
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        File.WriteAllText(path + ":metadata", "preserve me");
        using var edit = Prepare(path, Hash(bytes), "old", "new");
        edit.Commit();
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("preserve me", File.ReadAllText(path + ":metadata"));
    }

    [Fact]
    public void Rejects_hard_link_added_after_approval_preparation()
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        using var edit = Prepare(path, Hash(bytes), "old", "new");
        var alias = Path.Combine(Path.GetDirectoryName(_root)!, Guid.NewGuid().ToString("N") + ".txt");
        Assert.True(CreateHardLink(alias, path, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        try
        {
            Assert.Contains("hard links", Assert.Throws<IOException>(() => edit.Commit()).Message);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes, File.ReadAllBytes(alias));
        }
        finally { File.Delete(alias); }
    }

    [Fact]
    public void Transaction_blocks_existing_metadata_handle_until_rollback()
    {
        if (!Supported) return;
        var path = Put("old"u8.ToArray());
        const string beforeWrite = "before-write.txt";
        const string afterWrite = "after-write.txt";
        var handles = new List<SafeFileHandle>();
        try
        {
            var name = Path.GetFileName(path);
            var parent = NativeCreate.OpenAuthorizedParent(path, name, [_root], [_root], [], handles);
            var control = Path.Combine(_root, "control-source.txt");
            File.WriteAllText(control, "control");
            using (var controlHandle = NativeCreate.OpenEditTarget(parent, Path.GetFileName(control)))
                Assert.True(LinkByHandle(controlHandle, "control.txt") >= 0);
            File.Delete(Path.Combine(_root, "control.txt"));
            using var existing = NativeCreate.OpenEditTarget(parent, name);
            using (var transaction = WindowsFileEditTransaction.Open(parent, name))
            {
                Assert.True(LinkByHandle(existing, beforeWrite) < 0);
                RandomAccess.Write(transaction.File, "new"u8, 0);
                transaction.File.Dispose();
                Assert.True(LinkByHandle(existing, afterWrite) < 0);
            }
            Assert.False(File.Exists(Path.Combine(_root, beforeWrite)));
            Assert.False(File.Exists(Path.Combine(_root, afterWrite)));
            // The denied namespace operations need not become valid on an old NTFS handle.
            // Verify the rollback on the file itself and that a fresh approved edit can proceed.
            Assert.Equal("old", File.ReadAllText(path));
            using var edit = Prepare(path, Hash("old"u8.ToArray()), "old", "next");
            edit.Commit();
            Assert.Equal("next", File.ReadAllText(path));
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    private static int LinkByHandle(SafeFileHandle file, string name)
    {
        var offset = IntPtr.Size == 8 ? 20 : 12;
        var bytes = Encoding.Unicode.GetBytes(name);
        var info = new byte[(IntPtr.Size == 8 ? 24 : 16) + bytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(offset - 4), bytes.Length);
        bytes.CopyTo(info.AsSpan(offset));
        return NtSetInformationFile(file, out _, info, info.Length, 11); // FileLinkInformation
    }

    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public nint Status, Information; }

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, byte[] info, int size, int infoClass);

    [Fact]
    public void Rejects_hard_linked_file_before_reading_or_editing()
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        var alias = Path.Combine(Path.GetDirectoryName(_root)!, Guid.NewGuid().ToString("N") + ".txt");
        Assert.True(CreateHardLink(alias, path, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        try
        {
            var error = Assert.Throws<IOException>(() => Prepare(path, Hash(bytes), "old", "new"));
            Assert.Contains("hard links", error.Message);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(bytes, File.ReadAllBytes(alias));
            Assert.Single(Directory.GetFiles(_root));
        }
        finally { File.Delete(alias); }
    }

    [Fact]
    public void Preserves_protected_dacl_and_stream_on_success()
    {
        if (!OperatingSystem.IsWindows() || !Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        File.WriteAllText(path + ":metadata", "preserve me");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Current user has no SID.");
        Assert.True(ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;FA;;;{sid.Value})", 1, out var descriptor, out _),
            $"Converting security descriptor failed: {Marshal.GetLastWin32Error()}");
        try
        {
            Assert.True(SetFileSecurity(path, 0x80000004, descriptor), $"Setting protected DACL failed: {Marshal.GetLastWin32Error()}");
        }
        finally { LocalFree(descriptor); }
        var before = ReadDacl(path);

        using var edit = Prepare(path, Hash(bytes), "old", "new");
        edit.Commit();
        Assert.Equal(before, ReadDacl(path));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("preserve me", File.ReadAllText(path + ":metadata"));
    }

    private static byte[] ReadDacl(string path)
    {
        Assert.False(GetFileSecurity(path, 4, null, 0, out var needed));
        Assert.Equal(122, Marshal.GetLastWin32Error());
        var descriptor = new byte[checked((int)needed)];
        Assert.True(GetFileSecurity(path, 4, descriptor, needed, out _), $"Reading DACL failed: {Marshal.GetLastWin32Error()}");
        return descriptor;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr securityAttributes);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string path, uint information, IntPtr descriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(string path, uint information, byte[]? descriptor, uint length, out uint needed);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replaces_once_preserving_utf8_bom_and_reports_new_version(bool bom)
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("开始\r\nold 内容\r\n结束");
        if (bom) bytes = Encoding.UTF8.Preamble.ToArray().Concat(bytes).ToArray();
        var path = Put(bytes);
        using var edit = Prepare(path, Hash(bytes).ToUpperInvariant(), "old 内容", "新内容");
        var receipt = edit.Commit();
        var expected = Encoding.UTF8.GetBytes("开始\r\n新内容\r\n结束");
        if (bom) expected = Encoding.UTF8.Preamble.ToArray().Concat(expected).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(Hash(expected), receipt.Version);
        Assert.Equal(expected.Length, receipt.ByteCount);
        Assert.Single(Directory.GetFiles(_root));
        Assert.Throws<InvalidOperationException>(() => edit.Commit());
    }

    [Theory]
    [InlineData("nothing", "missing")]
    [InlineData("abc abc", "abc")]
    [InlineData("aaa", "aa")]
    public void Requires_exactly_one_including_overlapping_matches(string text, string oldText)
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        var path = Put(bytes);
        Assert.Throws<FileEditMatchException>(() => Prepare(path, Hash(bytes), oldText, "replacement"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Rejects_stale_version_and_rechecks_before_commit_without_retry()
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        Assert.Throws<FileEditConflictException>(() => Prepare(path, new string('0', 64), "old", "new"));
        using var edit = Prepare(path, Hash(bytes), "old", "new");
        File.WriteAllText(path, "external");
        Assert.Throws<FileEditConflictException>(() => edit.Commit());
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
        Assert.Throws<InvalidOperationException>(() => edit.Commit());
    }

    [Fact]
    public async Task Enforces_encoded_result_limit_and_reads_a_file_at_the_limit()
    {
        if (!Supported) return;
        var bytes = new byte[FileReadTool.MaxFileBytes - 1];
        bytes[0] = 0xef; bytes[1] = 0xbb; bytes[2] = 0xbf;
        Array.Fill(bytes, (byte)'a', 3, bytes.Length - 3);
        bytes[3] = (byte)'x';
        var path = Put(bytes);
        using (var edit = Prepare(path, Hash(bytes), "x", "é"))
        {
            Assert.Equal(FileReadTool.MaxFileBytes, edit.ByteCount);
            edit.Commit();
        }
        var result = File.ReadAllBytes(path);
        Assert.Equal(FileReadTool.MaxFileBytes, result.Length);
        var read = await new FileReadTool(new FileAccessPolicy([_root])).InvokeAsync(
            System.Text.Json.JsonSerializer.Serialize(new { path, limit = 1 }));
        Assert.True(read.Ok, read.Content);
        Assert.Contains("é", read.Content, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Prepare(path, Hash(result), "é", "éa"));
        Assert.Equal(result, File.ReadAllBytes(path));
    }

    [Fact]
    public void Rejects_multibyte_result_one_byte_over_limit_before_approval()
    {
        if (!Supported) return;
        var bytes = new byte[FileReadTool.MaxFileBytes - 1];
        bytes[0] = 0xef; bytes[1] = 0xbb; bytes[2] = 0xbf;
        Array.Fill(bytes, (byte)'a', 3, bytes.Length - 3);
        bytes[3] = (byte)'x';
        var path = Put(bytes);
        var error = Assert.Throws<InvalidDataException>(() => Prepare(path, Hash(bytes), "x", "€"));
        Assert.Contains("size limit", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Rejects_file_that_grows_over_limit_after_preparation()
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        using var edit = Prepare(path, Hash(bytes), "old", "new");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength(FileReadTool.MaxFileBytes + 1L);
        Assert.Throws<InvalidDataException>(() => edit.Commit());
        Assert.Equal(FileReadTool.MaxFileBytes + 1L, new FileInfo(path).Length);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Refuses_invalid_utf8_binary_and_invalid_arguments()
    {
        if (!Supported) return;
        var invalid = new byte[] { 0xff, 0xfe, 0x61, 0 };
        var path = Put(invalid);
        Assert.Throws<InvalidDataException>(() => Prepare(path, Hash(invalid), "a", "b"));
        invalid = [0xc3, 0x28];
        File.WriteAllBytes(path, invalid);
        Assert.Throws<InvalidDataException>(() => Prepare(path, Hash(invalid), "a", "b"));
        Assert.Throws<ArgumentException>(() => Prepare(path, "bad", "a", "b"));
        Assert.Throws<ArgumentException>(() => Prepare(path, Hash(invalid), "", "b"));
    }

    [Fact]
    public void Enforces_read_policy_and_cancellation()
    {
        if (!Supported) return;
        var bytes = Encoding.UTF8.GetBytes("old");
        var path = Put(bytes);
        Assert.Throws<UnauthorizedAccessException>(() => ExactFileEdit.Prepare(path, Hash(bytes), "old", "new", [_root], [_root], [_root]));
        Assert.Throws<UnauthorizedAccessException>(() => ExactFileEdit.Prepare(path, Hash(bytes), "old", "new", [_root], [], []));
        var protectedPath = Path.Combine(_root, ".env");
        File.WriteAllBytes(protectedPath, bytes);
        Assert.Throws<UnauthorizedAccessException>(() => Prepare(protectedPath, Hash(bytes), "old", "new"));
        File.Delete(protectedPath);
        using var edit = Prepare(path, Hash(bytes), "old", "new");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => edit.Commit(cancellation.Token));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Holds_parent_directory_and_refuses_reparse_targets()
    {
        if (!Supported) return;
        var parent = Path.Combine(_root, "parent");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "file.txt");
        var bytes = Encoding.UTF8.GetBytes("old");
        File.WriteAllBytes(path, bytes);
        using (var edit = Prepare(path, Hash(bytes), "old", "new"))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(parent, Path.Combine(_root, "moved")));
            edit.Commit();
        }
        var link = Path.Combine(_root, "link.txt");
        try { File.CreateSymbolicLink(link, path); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        var rejection = Assert.Throws<IOException>(() => Prepare(link, Hash(Encoding.UTF8.GetBytes("new")), "new", "x"));
        Assert.Contains("Reparse points", rejection.Message);
        File.Delete(link);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
