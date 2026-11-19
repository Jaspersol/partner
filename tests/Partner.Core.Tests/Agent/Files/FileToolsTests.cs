using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Partner.Core.Agent.Files;

namespace Partner.Core.Tests.Agent.Files;

/// <summary>
/// The file tools and the whitelist behind them. The whitelist tests are the ones that matter: a path that
/// merely starts with an allowed root, a <c>..</c> climb, and a link that leads out are each a way to read
/// something that was never allowed.
/// </summary>
public sealed class FileToolsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
    private readonly string _root;
    private readonly string _outside;
    private readonly List<string> _links = [];

    public FileToolsTests()
    {
        _root = Path.Combine(_base, "allowed");
        _outside = Path.Combine(_base, "secret");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "diary.txt"), "不该被读到");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        // A junction is removed as the link it is: a recursive delete walks into it and is refused.
        foreach (var link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link, recursive: false);
        }

        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private FileAccessPolicy Policy(params string[] denied) => new([_root], denied);

    private static Task<Partner.Core.Agent.Tools.ToolResult> Read(FileAccessPolicy policy, string path, string extra = "") =>
        new FileReadTool(policy).InvokeAsync($$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(path)}}{{extra}}}""");

    [Fact]
    public void A_path_inside_a_root_is_allowed_and_relative_paths_start_at_the_first_root()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        Assert.True(Policy().Resolve(Path.Combine(_root, "a.txt")).Allowed);
        Assert.Equal(Path.Combine(_root, "a.txt"), Policy().Resolve("a.txt").FullPath);
    }

    [Fact]
    public async Task A_junction_root_accepts_its_real_path_but_still_protects_denied_directories()
    {
        var link = Path.Combine(_base, "root-link");
        if (!TryMakeJunction(link, _root))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "normal.txt"), "hello");
        var protectedDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(protectedDir);
        File.WriteAllText(Path.Combine(protectedDir, "settings.json"), "secret");
        var policy = new FileAccessPolicy([link], [protectedDir]);

        Assert.True((await Read(policy, Path.Combine(_root, "normal.txt"))).Ok);
        var denied = await Read(policy, Path.Combine(link, "data", "settings.json"));
        Assert.False(denied.Ok);
        Assert.StartsWith("denied_file:", denied.Content);
    }

    [Fact]
    public void Climbing_out_with_dotdot_is_refused()
    {
        var decision = Policy().Resolve(Path.Combine("..", "secret", "diary.txt"));

        Assert.Equal(FileAccessKind.OutsideRoots, decision.Kind);
    }

    [Fact]
    public void A_sibling_directory_that_shares_the_roots_name_as_a_prefix_is_not_inside_it()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "x");

        Assert.Equal(FileAccessKind.OutsideRoots, Policy().Resolve(Path.Combine(sibling, "x.txt")).Kind);
    }

    [Fact]
    public void A_junction_that_leads_outside_the_root_is_refused()
    {
        var link = Path.Combine(_root, "shortcut");
        if (!TryMakeJunction(link, _outside))
        {
            return; // The platform cannot make one; the test has nothing to say.
        }

        var decision = Policy().Resolve(Path.Combine(link, "diary.txt"));

        Assert.Equal(FileAccessKind.OutsideRoots, decision.Kind);
    }

    [Fact]
    public async Task Reading_through_a_junction_that_leads_outside_returns_the_refusal_not_the_file()
    {
        var link = Path.Combine(_root, "shortcut");
        if (!TryMakeJunction(link, _outside))
        {
            return;
        }

        var result = await Read(Policy(), Path.Combine(link, "diary.txt"));

        Assert.False(result.Ok);
        Assert.StartsWith("outside_roots:", result.Content);
        Assert.DoesNotContain("不该被读到", result.Content);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("server.pem")]
    [InlineData("id_rsa")]
    [InlineData("vault.kdbx")]
    public async Task Credential_files_are_refused_even_inside_a_root(string name)
    {
        File.WriteAllText(Path.Combine(_root, name), "TOKEN=abc");

        var result = await Read(Policy(), name);

        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("TOKEN", result.Content);
    }

    [Fact]
    public async Task A_credential_named_symlink_is_refused_even_if_its_target_has_an_ordinary_name()
    {
        var target = Path.Combine(_root, "ordinary.txt");
        var link = Path.Combine(_root, ".env");
        File.WriteAllText(target, "secret");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await Read(Policy(), ".env");

        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("secret", result.Content);
    }

    [Fact]
    public async Task The_hosts_own_data_directory_is_refused_even_when_it_sits_inside_a_root()
    {
        var data = Path.Combine(_root, "settings");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "runtime.json"), "{\"apiKey\":\"sk-123\"}");

        var result = await Read(Policy(data), Path.Combine("settings", "runtime.json"));

        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("sk-123", result.Content);
    }

    [Fact]
    public async Task A_denied_data_directory_junction_also_protects_its_real_target()
    {
        var data = Path.Combine(_root, "real-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "runtime.json"), "secret");
        var alias = Path.Combine(_base, "data-link");
        if (!TryMakeJunction(alias, data)) return;

        var result = await Read(Policy(alias), Path.Combine("real-data", "runtime.json"));

        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("secret", result.Content);
    }

    [Fact]
    public void Opened_file_and_directory_are_rechecked_after_path_replacement()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var branch = Path.Combine(_root, "branch");
        Directory.CreateDirectory(branch);
        File.WriteAllText(Path.Combine(branch, "diary.txt"), "safe");
        var policy = Policy();
        var file = policy.Resolve(Path.Combine(branch, "diary.txt"));
        var directory = policy.Resolve(branch);
        Assert.True(file.Allowed);
        Assert.True(directory.Allowed);
        Directory.Delete(branch, recursive: true);
        if (OperatingSystem.IsWindows())
        {
            if (!TryMakeJunction(branch, _outside)) return;
        }
        else
        {
            Directory.CreateSymbolicLink(branch, _outside);
            _links.Add(branch);
        }

        using var fileHandle = File.OpenHandle(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var directoryHandle = FileAccessPolicy.OpenDirectory(directory.FullPath, enumerate: true);
        Assert.False(directoryHandle.IsInvalid);
        Assert.Equal(FileAccessKind.OutsideRoots, policy.ValidateOpenedFile(fileHandle).Kind);
        Assert.Equal(FileAccessKind.OutsideRoots, policy.ValidateOpenedFile(directoryHandle).Kind);
    }

    [Fact]
    public void Directory_enumeration_uses_the_validated_handle_after_path_replacement()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var branch = Path.Combine(_root, "branch");
        var moved = Path.Combine(_root, "moved");
        Directory.CreateDirectory(branch);
        File.WriteAllText(Path.Combine(branch, "safe.txt"), "safe");
        var policy = Policy();
        using var handle = FileAccessPolicy.OpenDirectory(branch, enumerate: true);
        Assert.False(handle.IsInvalid);
        Assert.True(policy.ValidateOpenedFile(handle).Allowed);
        Directory.Move(branch, moved);
        Directory.CreateDirectory(branch);
        File.WriteAllText(Path.Combine(branch, "secret.txt"), "secret");

        var names = FileListTool.Enumerate(handle).Select(entry => entry.Name).ToArray();
        Assert.Contains("safe.txt", names);
        Assert.DoesNotContain("secret.txt", names);
    }

    [Fact]
    public async Task Linux_read_rejects_fifo_without_opening_it_for_io()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fifo = Path.Combine(_root, "pipe");
        Assert.Equal(0, MkFifo(fifo, 0x180));

        Assert.Throws<IOException>(() => FileAccessPolicy.OpenLinuxRegularFile(fifo));
        var result = await Read(Policy(), fifo);
        Assert.False(result.Ok);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    [Fact]
    public void Linux_listing_does_not_follow_entry_symlinks_for_metadata()
    {
        if (!OperatingSystem.IsLinux()) return;
        var secret = Path.Combine(_outside, "large.txt");
        File.WriteAllText(secret, new string('x', 10000));
        var link = Path.Combine(_root, "link.txt");
        File.CreateSymbolicLink(link, secret);
        using var handle = FileAccessPolicy.OpenDirectory(_root, enumerate: true);
        Assert.True(Policy().ValidateOpenedFile(handle).Allowed);

        var entry = Assert.Single(FileListTool.Enumerate(handle), item => item.Name == "link.txt");
        Assert.Equal(Encoding.UTF8.GetByteCount(secret), entry.Length);
        Assert.NotEqual(10000, entry.Length);
    }

    [Fact]
    public void Linux_retargeted_roots_and_protected_directories_are_checked_by_handle()
    {
        if (!OperatingSystem.IsLinux()) return;
        var rootAlias = Path.Combine(_base, "root-alias");
        var dataAlias = Path.Combine(_base, "data-alias");
        var oldData = Path.Combine(_root, "old-data");
        var newData = Path.Combine(_root, "new-data");
        Directory.CreateDirectory(oldData);
        Directory.CreateDirectory(newData);
        File.WriteAllText(Path.Combine(oldData, "settings.json"), "old secret");
        File.WriteAllText(Path.Combine(newData, "settings.json"), "new secret");
        Directory.CreateSymbolicLink(rootAlias, _root);
        Directory.CreateSymbolicLink(dataAlias, oldData);
        _links.Add(rootAlias);
        _links.Add(dataAlias);
        var policy = new FileAccessPolicy([rootAlias], [dataAlias]);
        Directory.Delete(rootAlias);
        Directory.CreateSymbolicLink(rootAlias, _outside);
        Directory.Delete(dataAlias);
        Directory.CreateSymbolicLink(dataAlias, newData);

        using var outside = File.OpenHandle(Path.Combine(rootAlias, "diary.txt"));
        using var oldHandle = File.OpenHandle(Path.Combine(oldData, "settings.json"));
        using var newHandle = File.OpenHandle(Path.Combine(newData, "settings.json"));
        Assert.Equal(FileAccessKind.OutsideRoots, policy.ValidateOpenedFile(outside).Kind);
        Assert.Equal(FileAccessKind.DeniedFile, policy.ValidateOpenedFile(oldHandle).Kind);
        Assert.Equal(FileAccessKind.DeniedFile, policy.ValidateOpenedFile(newHandle).Kind);
    }

    [Fact]
    public void Validation_rejects_an_unusable_handle_on_every_platform()
    {
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        Assert.Equal(FileAccessKind.OutsideRoots, Policy().ValidateOpenedFile(handle).Kind);
    }

    [Fact]
    public void Windows_opened_handle_is_checked_after_a_directory_is_replaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        var branch = Path.Combine(_root, "branch");
        Directory.CreateDirectory(branch);
        var requested = Path.Combine(branch, "diary.txt");
        File.WriteAllText(requested, "safe");
        var policy = Policy();
        var before = policy.Resolve(requested);
        Assert.True(before.Allowed);

        File.Delete(requested);
        Directory.Delete(branch);
        if (!TryMakeJunction(branch, _outside)) return;
        using var handle = File.OpenHandle(before.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var after = policy.ValidateOpenedFile(handle);

        Assert.Equal(FileAccessKind.OutsideRoots, after.Kind);
    }

    [Fact]
    public async Task Windows_protected_directory_through_an_ancestor_junction_is_checked_by_open_handle()
    {
        if (!OperatingSystem.IsWindows()) return;
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "secret");
        var alias = Path.Combine(_base, "root-alias");
        if (!TryMakeJunction(alias, _root)) return;
        var policy = Policy(Path.Combine(alias, "data"));

        using var handle = File.OpenHandle(Path.Combine(data, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(FileAccessKind.DeniedFile, policy.ValidateOpenedFile(handle).Kind);
        var result = await Read(policy, Path.Combine(data, "settings.json"));
        Assert.False(result.Ok);
        Assert.StartsWith("denied_file:", result.Content);
        Assert.DoesNotContain("secret", result.Content);
    }

    [Fact]
    public void Windows_retargeting_a_root_does_not_authorize_its_new_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        var alias = Path.Combine(_base, "root-link");
        if (!TryMakeJunction(alias, _root)) return;
        var policy = new FileAccessPolicy([alias]);
        Directory.Delete(alias);
        if (!TryMakeJunction(alias, _outside)) return;

        using var handle = File.OpenHandle(Path.Combine(alias, "diary.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(FileAccessKind.OutsideRoots, policy.ValidateOpenedFile(handle).Kind);
    }

    [Fact]
    public void Windows_retargeting_a_protected_directory_protects_both_targets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var oldData = Path.Combine(_root, "old-data");
        var newData = Path.Combine(_root, "new-data");
        Directory.CreateDirectory(oldData);
        Directory.CreateDirectory(newData);
        File.WriteAllText(Path.Combine(oldData, "settings.json"), "old secret");
        File.WriteAllText(Path.Combine(newData, "settings.json"), "new secret");
        var alias = Path.Combine(_base, "data-link");
        if (!TryMakeJunction(alias, oldData)) return;
        var policy = Policy(alias);
        Directory.Delete(alias);
        if (!TryMakeJunction(alias, newData)) return;

        using var oldHandle = File.OpenHandle(Path.Combine(oldData, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var newHandle = File.OpenHandle(Path.Combine(newData, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(FileAccessKind.DeniedFile, policy.ValidateOpenedFile(oldHandle).Kind);
        Assert.Equal(FileAccessKind.DeniedFile, policy.ValidateOpenedFile(newHandle).Kind);
    }

    [Fact]
    public void Windows_validation_rejects_an_unusable_handle()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        Assert.Equal(FileAccessKind.OutsideRoots, Policy().ValidateOpenedFile(handle).Kind);
    }

    [Fact]
    public async Task Windows_alternate_streams_cannot_disguise_credential_names()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_root, ".env"), "secret");
        var result = await Read(Policy(), ".env::$DATA");
        Assert.False(result.Ok);
        Assert.StartsWith("invalid_args:", result.Content);
        Assert.DoesNotContain("secret", result.Content);
    }

    [Fact]
    public async Task A_utf8_file_is_read_in_slices_that_say_how_to_continue()
    {
        File.WriteAllText(Path.Combine(_root, "long.txt"), new string('甲', 100));

        var first = await Read(Policy(), "long.txt", ",\"limit\":40");
        var rest = await Read(Policy(), "long.txt", ",\"offset\":80,\"limit\":40");

        Assert.True(first.Ok);
        Assert.Contains("UTF-8", first.Content);
        Assert.Contains("共 100 字符，本次 0–40", first.Content);
        Assert.Contains("用 offset=40 继续", first.Content);
        Assert.True(rest.Ok);
        Assert.DoesNotContain("继续", rest.Content);
    }

    [Fact]
    public async Task Paging_never_splits_a_supplementary_unicode_character()
    {
        File.WriteAllText(Path.Combine(_root, "unicode.txt"), "a😀b");

        var first = await Read(Policy(), "unicode.txt", ",\"limit\":2");
        var second = await Read(Policy(), "unicode.txt", ",\"offset\":1,\"limit\":2");
        var inside = await Read(Policy(), "unicode.txt", ",\"offset\":2");
        var tooSmall = await Read(Policy(), "unicode.txt", ",\"offset\":1,\"limit\":1");

        Assert.True(first.Ok);
        Assert.Contains("用 offset=1 继续", first.Content);
        Assert.True(second.Ok);
        Assert.Contains("😀", second.Content);
        Assert.Contains("用 offset=3 继续", second.Content);
        Assert.StartsWith("invalid_args:", inside.Content);
        Assert.StartsWith("invalid_args:", tooSmall.Content);
    }

    [Fact]
    public async Task Read_version_describes_the_exact_bytes_even_across_pages()
    {
        var path = Path.Combine(_root, "version.txt");
        var bytes = Encoding.UTF8.GetBytes("abcdef");
        File.WriteAllBytes(path, bytes);
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var first = await Read(Policy(), "version.txt", ",\"limit\":2");
        var second = await Read(Policy(), "version.txt", ",\"offset\":2,\"limit\":2");
        Assert.Contains($"sha256:{version}", first.Content);
        Assert.Contains($"sha256:{version}", second.Content);

        File.WriteAllText(path, "changed");
        var changed = await Read(Policy(), "version.txt");
        Assert.DoesNotContain($"sha256:{version}", changed.Content);
    }

    [Fact]
    public async Task Reading_an_empty_file_with_a_large_offset_returns_eof()
    {
        File.WriteAllText(Path.Combine(_root, "empty.txt"), "");

        var result = await Read(Policy(), "empty.txt", ",\"offset\":2147483647");

        Assert.True(result.Ok);
        Assert.Contains("已经超过文件末尾", result.Content);
    }

    [Fact]
    public async Task A_gbk_file_is_decoded_and_the_answer_names_the_encoding()
    {
        File.WriteAllBytes(Path.Combine(_root, "old.txt"), Encoding.GetEncoding("gb18030").GetBytes("你好，世界"));

        var result = await Read(Policy(), "old.txt");

        Assert.True(result.Ok);
        Assert.Contains("GB18030", result.Content);
        Assert.Contains("你好，世界", result.Content);
    }

    [Fact]
    public async Task Utf8_pages_and_version_remain_correct_across_read_buffer_boundaries()
    {
        var path = Path.Combine(_root, "boundary.txt");
        var text = new string('a', 8188) + "😀汉字" + new string('b', 20);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        File.WriteAllBytes(path, bytes);
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var first = await Read(Policy(), "boundary.txt", ",\"offset\":8187,\"limit\":2");
        var second = await Read(Policy(), "boundary.txt", ",\"offset\":8188,\"limit\":4");
        var inside = await Read(Policy(), "boundary.txt", ",\"offset\":8189");

        Assert.True(first.Ok);
        Assert.Contains($"sha256:{version}，共 {text.Length} 字符，本次 8187–8188", first.Content);
        Assert.Contains("\na\n（还没读完，用 offset=8188 继续）", first.Content);
        Assert.True(second.Ok);
        Assert.Contains("本次 8188–8192", second.Content);
        Assert.Contains("\n😀汉字\n", second.Content);
        Assert.StartsWith("invalid_args:", inside.Content);
    }

    [Fact]
    public async Task Late_invalid_utf8_uses_gb18030_for_the_whole_stream_including_a_utf8_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.ASCII.GetBytes(new string('a', 8188)))
            .Concat(Encoding.GetEncoding("gb18030").GetBytes("你好😀")).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "fallback.txt"), bytes);
        var decoded = Encoding.GetEncoding("gb18030").GetString(bytes);
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var beginning = await Read(Policy(), "fallback.txt", ",\"limit\":8");
        var ending = await Read(Policy(), "fallback.txt", $",\"offset\":{decoded.Length - 4}");

        Assert.True(beginning.Ok);
        Assert.Contains($"GB18030，版本 sha256:{version}，共 {decoded.Length} 字符", beginning.Content);
        Assert.Contains($"\n{decoded[..8]}\n", beginning.Content);
        Assert.True(ending.Ok);
        Assert.EndsWith($"\n{decoded[^4..]}", ending.Content);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF })]
    [InlineData(new byte[] { 0xEF, 0xBB })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xE4 })]
    public async Task Incomplete_utf8_at_eof_falls_back_using_all_original_bytes(byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_root, "incomplete.txt"), bytes);
        var decoded = Encoding.GetEncoding("gb18030").GetString(bytes);
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var result = await Read(Policy(), "incomplete.txt");

        Assert.True(result.Ok);
        Assert.Contains($"GB18030，版本 sha256:{version}，共 {decoded.Length} 字符", result.Content);
        Assert.EndsWith($"\n{decoded}", result.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16_bom_and_incomplete_final_code_unit_keep_the_original_decoding(bool bigEndian)
    {
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(new string('x', 4094) + "😀你好"))
            .Concat(new byte[] { 0x61 }).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "utf16.txt"), bytes);
        var decoded = encoding.GetString(bytes, 2, bytes.Length - 2);
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var page = await Read(Policy(), "utf16.txt", ",\"offset\":4094");
        var inside = await Read(Policy(), "utf16.txt", ",\"offset\":4095");

        Assert.True(page.Ok);
        Assert.Contains($"UTF-16，版本 sha256:{version}，共 {decoded.Length} 字符", page.Content);
        Assert.EndsWith($"\n{decoded[4094..]}", page.Content);
        Assert.StartsWith("invalid_args:", inside.Content);
    }

    [Fact]
    public async Task Binary_detection_checks_exactly_the_first_8192_bytes()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('a', 8193));
        bytes[8192] = 0;
        File.WriteAllBytes(Path.Combine(_root, "nul.txt"), bytes);

        var allowed = await Read(Policy(), "nul.txt", ",\"offset\":8192");
        Assert.True(allowed.Ok);
        Assert.EndsWith("\n\0", allowed.Content);

        bytes[8191] = 0;
        File.WriteAllBytes(Path.Combine(_root, "nul.txt"), bytes);
        var rejected = await Read(Policy(), "nul.txt");
        Assert.False(rejected.Ok);
        Assert.StartsWith("binary:", rejected.Content);
    }

    [Fact]
    public async Task A_file_at_the_size_cap_is_read_and_a_cancelled_read_is_cancelled()
    {
        var path = Path.Combine(_root, "at-cap.txt");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'a', (int)FileReadTool.MaxFileBytes).ToArray());

        var result = await Read(Policy(), "at-cap.txt", $",\"offset\":{FileReadTool.MaxFileBytes - 1}");
        Assert.True(result.Ok);
        Assert.Contains($"共 {FileReadTool.MaxFileBytes} 字符", result.Content);
        Assert.EndsWith("\na", result.Content);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var tool = new FileReadTool(Policy());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync("{\"path\":\"at-cap.txt\"}", cancellation.Token));
    }

    [Fact]
    public async Task A_binary_file_is_refused()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), [1, 2, 0, 3, 0, 0, 255]);

        var result = await Read(Policy(), "a.bin");

        Assert.False(result.Ok);
        Assert.StartsWith("binary:", result.Content);
    }

    [Fact]
    public async Task A_file_over_the_size_cap_is_refused_rather_than_truncated()
    {
        using (var stream = File.Create(Path.Combine(_root, "big.txt")))
        {
            stream.SetLength(FileReadTool.MaxFileBytes + 1);
        }

        var result = await Read(Policy(), "big.txt");

        Assert.False(result.Ok);
        Assert.StartsWith("too_large:", result.Content);
    }

    [Fact]
    public async Task A_missing_file_and_a_directory_are_told_apart()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var missing = await Read(Policy(), "nope.txt");
        var directory = await Read(Policy(), "sub");

        Assert.StartsWith("not_found:", missing.Content);
        Assert.StartsWith("invalid_args:", directory.Content);
        Assert.Contains("file_list", directory.Content);
    }

    [Fact]
    public async Task Listing_shows_directories_first_and_caps_a_huge_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zdir"));
        for (var i = 0; i < FileListTool.MaxEntries + 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i:D4}.txt"), "x");
        }

        var result = await new FileListTool(Policy()).InvokeAsync("{}");

        Assert.True(result.Ok);
        Assert.Contains($"共 {FileListTool.MaxEntries + 6} 项，只列前 {FileListTool.MaxEntries} 项", result.Content);
        var lines = result.Content.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        Assert.Equal("d  zdir/", lines[1]);
    }

    [Fact]
    public async Task Listing_pages_cover_large_relative_directory_in_order()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(sub, "zdir"));
        for (var i = 0; i < 205; i++)
        {
            File.WriteAllText(Path.Combine(sub, $"f{i:D4}.txt"), "x");
        }

        var tool = new FileListTool(Policy());
        var first = await tool.InvokeAsync("""{"path":"sub"}""");
        var second = await tool.InvokeAsync("""{"path":"sub","offset":200,"limit":10}""");
        var pastEnd = await tool.InvokeAsync("""{"path":"sub","offset":999}""");

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.True(pastEnd.Ok);
        Assert.Contains("下一页 offset: 200", first.Content);
        Assert.DoesNotContain("下一页 offset:", second.Content);
        Assert.DoesNotContain("下一页 offset:", pastEnd.Content);
        var entries = (first.Content + "\n" + second.Content).Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("f  ") || line.StartsWith("d  "))
            .ToArray();
        Assert.Equal(206, entries.Length);
        Assert.Equal("d  zdir/", entries[0]);
        Assert.Equal(Enumerable.Range(0, 205).Select(i => $"f  f{i:D4}.txt  1 字节"), entries.Skip(1));
    }

    [Theory]
    [InlineData("{\"offset\":-1}")]
    [InlineData("{\"offset\":\"1\"}")]
    [InlineData("{\"offset\":2147483648}")]
    [InlineData("{\"limit\":0}")]
    [InlineData("{\"limit\":201}")]
    [InlineData("{\"limit\":null}")]
    [InlineData("{\"limit\":1.5}")]
    public async Task Listing_rejects_invalid_paging_arguments(string arguments)
    {
        var result = await new FileListTool(Policy()).InvokeAsync(arguments);
        Assert.False(result.Ok);
        Assert.StartsWith("invalid_args:", result.Content);
    }

    [Fact]
    public async Task Listing_offsets_refer_to_current_directory_not_a_snapshot()
    {
        File.WriteAllText(Path.Combine(_root, "b.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "x");
        var tool = new FileListTool(Policy());
        var first = await tool.InvokeAsync("""{"limit":1}""");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        var second = await tool.InvokeAsync("""{"offset":1,"limit":1}""");

        Assert.Contains("f  b.txt", first.Content);
        Assert.Contains("下一页 offset: 1", first.Content);
        Assert.Contains("f  b.txt", second.Content);
    }

    [Fact]
    public async Task Listing_outside_the_roots_is_refused()
    {
        var result = await new FileListTool(Policy()).InvokeAsync($$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(_outside)}}}""");

        Assert.False(result.Ok);
        Assert.StartsWith("outside_roots:", result.Content);
    }

    private bool TryMakeJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit();
        var made = process.ExitCode == 0 && Directory.Exists(link);
        if (made)
        {
            _links.Add(link);
        }

        return made;
    }
}
