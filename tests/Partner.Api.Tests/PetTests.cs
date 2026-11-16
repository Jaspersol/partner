using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Partner.Api.Pet;
using Partner.Api.Settings;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Api.Tests;

/// <summary>
/// The pet's asset pipeline, exercised against a real store on a throwaway root.
/// <para>
/// Importing is the one place where a downloaded, unverifiable tree becomes something the webview loads, so these
/// checks are about what is <b>refused</b>: an entry that would land outside the model directory, a script, a
/// reference that does not exist. The positive cases exist to prove the refusals are not just failing everything.
/// </para>
/// </summary>
public sealed class PetAssetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "partner-pet-store-tests", Guid.NewGuid().ToString("N"));

    private PetAssetStore Store() => new(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A reparse point left behind by a failed test would make cleanup walk out of the temp directory; the
            // next test uses its own root anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- model fixtures

    /// <summary>The smallest tree the importer accepts: one moc, one texture, one motion, one expression.</summary>
    private static string MakeModel(string parent, string folder = "cat")
    {
        var root = Path.Combine(parent, folder);
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        Directory.CreateDirectory(Path.Combine(root, "motions"));
        Directory.CreateDirectory(Path.Combine(root, "expressions"));

        File.WriteAllBytes(Path.Combine(root, "cat.moc3"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "textures", "texture_00.png"), [0x89, 0x50, 0x4e, 0x47]);
        File.WriteAllText(Path.Combine(root, "motions", "idle.motion3.json"), """{"Version":3}""");
        File.WriteAllText(Path.Combine(root, "expressions", "smile.exp3.json"), """{"Version":3}""");
        File.WriteAllText(Path.Combine(root, $"{folder}.model3.json"), Model3(
            moc: "cat.moc3",
            motions: "{\"Idle\":[{\"File\":\"motions/idle.motion3.json\"}]}",
            expressions: "[{\"Name\":\"smile\",\"File\":\"expressions/smile.exp3.json\"}]"));

        return root;
    }

    private static string Model3(
        string moc = "cat.moc3",
        string textures = "[\"textures/texture_00.png\"]",
        string motions = "{\"Idle\":[{\"File\":\"motions/idle.motion3.json\"}]}",
        string expressions = "[]",
        string? extra = null,
        bool hitAreas = true)
    {
        // Built by concatenation rather than a raw literal: `extra` carries JSON fragments, and nesting one raw
        // string inside another is unreadable and error-prone.
        var hit = hitAreas ? "[{\"Id\":\"HitArea\",\"Name\":\"Head\"}]" : "[]";
        return "{"
            + "\"Version\":3,"
            + "\"FileReferences\":{"
            + "\"Moc\":\"" + moc + "\","
            + "\"Textures\":" + textures + ","
            + "\"Motions\":" + motions + ","
            + "\"Expressions\":" + expressions
            + (extra ?? string.Empty)
            + "},"
            + "\"HitAreas\":" + hit
            + "}";
    }

    /// <summary>Writes a zip with the given members, all stored as plain UTF-8 (the flag set) unless told otherwise.</summary>
    private static string Zip(string parent, string name, params (string Name, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, bytes) in entries)
        {
            using var stream = archive.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---------------------------------------------------------------- core

    [Fact]
    public void An_uninstalled_core_reports_not_installed()
    {
        var status = Store().Status();

        Assert.False(status.Installed);
        Assert.Equal("5-r.5", status.Sdk);
        Assert.Null(status.Sha256);
    }

    [Fact]
    public void Importing_an_archive_that_is_not_the_expected_sdk_is_refused()
    {
        var path = Zip(_root, "wrong.zip", ("SomeSdk/Core/live2dcubismcore.min.js", Utf8("// nope")));

        var error = Assert.Throws<PetImportException>(() => Store().ImportCore(path));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(Store().Status().Installed);
    }

    // ---------------------------------------------------------------- zip safety

    [Fact]
    public void An_entry_escaping_the_staging_root_is_refused()
    {
        var path = Zip(_root, "slip.zip", ("../evil.model3.json", Utf8("{}")));

        var error = Assert.Throws<PetImportException>(() => Store().Import(path));
        Assert.Contains("上级目录", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "evil.model3.json")));
    }

    [Fact]
    public void An_entry_inside_a_dotdot_segment_is_refused()
    {
        var path = Zip(_root, "dots.zip", ("m/n/../../../x.model3.json", Utf8("{}")));

        Assert.Throws<PetImportException>(() => Store().Import(path));
    }

    [Fact]
    public void An_absolute_entry_is_refused()
    {
        var path = Zip(_root, "absolute.zip", ("C:/Windows/System32/drivers/etc/hosts", Utf8("x")));

        var error = Assert.Throws<PetImportException>(() => Store().Import(path));
        Assert.Contains("绝对路径", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_entry_is_refused_with_the_reason_it_would_run()
    {
        var path = Zip(_root, "script.zip", ("cat/cat.model3.json", Utf8("{}")), ("cat/boot.js", Utf8("alert(1)")));

        var error = Assert.Throws<PetImportException>(() => Store().Import(path));
        Assert.Contains("脚本", error.Message, StringComparison.Ordinal);
        Assert.Contains("script-src", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disallowed_extension_is_refused()
    {
        var path = Zip(_root, "exe.zip", ("cat/cat.model3.json", Utf8("{}")), ("cat/run.exe", Utf8("MZ")));

        Assert.Throws<PetImportException>(() => Store().Import(path));
    }

    [Fact]
    public void Too_many_entries_are_refused()
    {
        var entries = new List<(string, byte[])> { ("cat/cat.model3.json", Utf8("{}")) };
        for (var i = 0; i < 2000; i++)
        {
            entries.Add(($"cat/f{i}.json", Utf8("{}")));
        }

        var path = Zip(_root, "many.zip", [.. entries]);

        var error = Assert.Throws<PetImportException>(() => Store().Import(path));
        Assert.Contains("文件太多", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_archive_larger_than_the_limit_is_refused()
    {
        // Written as a compressed stream of zeros: the archive is tiny, the declared and extracted size is not.
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "huge.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("cat/cat.moc3", CompressionLevel.SmallestSize).Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 201; i++)
            {
                stream.Write(chunk, 0, chunk.Length);
            }
        }

        var error = Assert.Throws<PetImportException>(() => Store().Import(path));
        Assert.Contains("太大", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_archive_just_over_half_the_limit_imports()
    {
        // Declared sizes and bytes written are counted separately: one shared counter would halve the real cap,
        // and 101 MB would then be refused.
        const int Megabytes = 101;
        var model3 = "{\"Version\":3,\"FileReferences\":{\"Moc\":\"cat.moc3\",\"Textures\":[],\"Motions\":{},\"Expressions\":[]}}";
        var path = Path.Combine(_root, "big.zip");
        Directory.CreateDirectory(_root);

        var chunk = new byte[1024 * 1024];
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var header = archive.CreateEntry("cat/cat.model3.json").Open())
            {
                var headerBytes = Utf8(model3);
                header.Write(headerBytes, 0, headerBytes.Length);
            }

            using var body = archive.CreateEntry("cat/cat.moc3", CompressionLevel.SmallestSize).Open();
            for (var i = 0; i < Megabytes; i++)
            {
                body.Write(chunk, 0, chunk.Length);
            }
        }

        var imported = Store().Import(path).Imported;

        Assert.NotNull(imported);
        var moc = new FileInfo(Path.Combine(Path.GetDirectoryName(imported!.Model3)!, "cat.moc3"));
        Assert.Equal(Megabytes * 1024L * 1024, moc.Length);
    }

    [Fact]
    public void A_reparse_point_in_a_directory_import_is_refused()
    {
        var model = MakeModel(_root);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "not a model file");

        // A junction rather than a symbolic link: creating a symlink needs a privilege a plain user account does
        // not have, while a junction always works — and both are the same reparse-point case the importer refuses.
        if (!MakeJunction(Path.Combine(model, "linked"), outside))
        {
            // Nothing to assert where the OS will not create the reparse point; the archive-attribute variant
            // below covers the same check unconditionally.
            return;
        }

        var error = Assert.Throws<PetImportException>(() => Store().Import(model));
        Assert.Contains("重分析点", error.Message, StringComparison.Ordinal);

        // A junction is removed as a link, not as a tree: leaving it for the recursive cleanup below would walk
        // into the target instead.
        Directory.Delete(Path.Combine(model, "linked"));
    }

    [Fact]
    public void An_entry_carrying_the_reparse_attribute_is_refused()
    {
        // Hand-built so the attribute bit is set the way Windows writes it: high word of ExternalAttributes.
        var path = Zip(_root, "reparse.zip", ("cat/cat.model3.json", Utf8("{}")), ("cat/link.json", Utf8("{}")));
        MarkReparseEntry(path, "cat/link.json");

        var error = Assert.Throws<PetImportException>(() => Store().Import(path));
        Assert.Contains("重分析点", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- GBK names

    [Fact]
    public void A_zip_with_gbk_names_imports()
    {
        // "经典小键盘/cat.model3.json" with the UTF-8 flag clear: exactly what a Chinese Windows tool writes.
        var folder = "经典小键盘";
        var model3 = "{\"Version\":3,\"FileReferences\":{\"Moc\":\"cat.moc3\",\"Textures\":[],\"Motions\":{},\"Expressions\":[]}}";
        var path = Path.Combine(_root, "gbk.zip");
        Directory.CreateDirectory(_root);

        WriteRawZip(path, [
            (Encoding.GetEncoding(936).GetBytes($"{folder}/cat.model3.json"), Utf8(model3)),
            (Encoding.GetEncoding(936).GetBytes($"{folder}/cat.moc3"), [1, 2, 3]),
        ]);

        var result = Store().Import(path);

        Assert.NotNull(result.Imported);

        // The display name is the folder name as written: that is the point of decoding the raw bytes. The
        // directory, by contrast, is the ascii id, which for a Chinese-only name falls back to "model-<hash>".
        Assert.Equal(folder, result.Imported!.Name);
        Assert.Equal("cat.model3.json", Path.GetFileName(result.Imported.Model3));
        Assert.StartsWith("model-", Path.GetFileName(Path.GetDirectoryName(result.Imported.Model3)), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.Imported.Model3)!, "cat.moc3")));
    }

    // ---------------------------------------------------------------- references

    [Fact]
    public void A_missing_reference_is_an_error_naming_the_path()
    {
        var model = MakeModel(_root);
        File.Delete(Path.Combine(model, "textures", "texture_00.png"));

        var error = Assert.Throws<PetImportException>(() => Store().Import(model));
        Assert.Contains("textures/texture_00.png", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_reference_escaping_the_model_root_is_an_error()
    {
        var model = MakeModel(_root);
        File.WriteAllText(Path.Combine(model, "cat.model3.json"), Model3(moc: "../other/cat.moc3"));

        var error = Assert.Throws<PetImportException>(() => Store().Import(model));
        Assert.Contains("不在模型目录内", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_references_that_are_missing_are_recorded_as_warnings()
    {
        var model = MakeModel(_root);

        // One sound that genuinely does not exist, and a display-info file that is also absent. The moc, texture,
        // motion and expression all exist, so only the two optional references should be reported.
        File.WriteAllText(Path.Combine(model, "cat.model3.json"), Model3(
            motions: "{\"Idle\":[{\"File\":\"motions/idle.motion3.json\",\"Sound\":\"../shared/sounds/tap.mp3\"}]}",
            extra: ",\"DisplayInfo\":\"cat.cdi3.json\""));

        var result = Store().Import(model);

        Assert.NotNull(result.Imported);
        var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.Imported!.Model3)!, "pet-model.json")));
        var warnings = manifest.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!).ToList();

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("cat.cdi3.json", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("tap.mp3", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_model3_files_are_reported_as_candidates()
    {
        var root = Path.Combine(_root, "bundle");
        MakeModel(root, "one");
        MakeModel(root, "two");

        var result = Store().Import(root);

        Assert.Null(result.Imported);
        Assert.Equal(["one/one.model3.json", "two/two.model3.json"], result.Candidates);
    }

    [Fact]
    public void A_candidate_escapes_is_refused_when_used_as_the_choice()
    {
        var root = Path.Combine(_root, "bundle");
        MakeModel(root, "one");

        var error = Assert.Throws<PetImportException>(() => Store().Import(root, "../escape/one.model3.json"));
        Assert.Contains("model3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chosen_model3_must_end_with_the_model3_suffix()
    {
        var root = Path.Combine(_root, "bundle");
        MakeModel(root, "one");

        Assert.Throws<PetImportException>(() => Store().Import(root, "one/cat.moc3"));
    }

    // ---------------------------------------------------------------- import results

    [Fact]
    public void A_motion_sound_inside_the_model_root_is_copied()
    {
        var model = MakeModel(_root);
        Directory.CreateDirectory(Path.Combine(model, "sounds"));
        File.WriteAllBytes(Path.Combine(model, "sounds", "tap.mp3"), [0x49, 0x44, 0x33, 0x04]);
        File.WriteAllText(Path.Combine(model, "cat.model3.json"), Model3(
            motions: "{\"Idle\":[{\"File\":\"motions/idle.motion3.json\",\"Sound\":\"sounds/tap.mp3\"}]}"));

        var imported = Store().Import(model).Imported;

        Assert.NotNull(imported);
        var directory = Path.GetDirectoryName(imported!.Model3)!;
        Assert.True(File.Exists(Path.Combine(directory, "sounds", "tap.mp3")));

        // A copied sound is not a skipped one: the warning list exists for what the model references and the
        // import did not bring.
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pet-model.json")));
        Assert.Equal(0, manifest.RootElement.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public void An_imported_model_reports_its_motions_expressions_and_hit_areas()
    {
        var model = MakeModel(_root);

        var imported = Store().Import(model).Imported;

        Assert.NotNull(imported);
        Assert.True(imported!.HasHitAreas);
        Assert.Equal(["smile"], imported.Expressions);
        var motion = Assert.Single(imported.Motions);
        Assert.Equal(("Idle", 0, "Idle_0"), (motion.Group, motion.No, motion.Name));
        Assert.True(File.Exists(imported.Model3));
        Assert.True(imported.Bytes > 0);
    }

    [Fact]
    public void Importing_the_same_tree_twice_reuses_the_existing_directory()
    {
        var model = MakeModel(_root);
        var first = Store().Import(model).Imported!;
        var second = Store().Import(model).Imported!;

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Store().List());
        Assert.Equal([first.Id], Store().ModelIds());
    }

    [Fact]
    public void Deleting_the_active_model_clears_it_from_the_saved_document()
    {
        var model = MakeModel(_root);
        var imported = Store().Import(model).Imported!;
        Assert.True(Store().Delete(imported.Id));

        var path = Path.Combine(_root, "runtime.json");
        var settings = new SettingsStore(path);
        var stored = settings.Load();
        settings.Save(stored with
        {
            Pet = stored.Pet with
            {
                ActiveModelId = imported.Id,
                Profiles = new Dictionary<string, PetModelProfile>(StringComparer.Ordinal)
                {
                    [imported.Id] = new(),
                    ["other-12345678"] = new(),
                },
            },
        });

        Partner.Api.Endpoints.PetEndpoints.ForgetDeletedModel(settings, imported.Id);

        // Read back off disk rather than from the return value: the point is what a restarted process sees.
        var document = JsonDocument.Parse(File.ReadAllText(path));
        var pet = document.RootElement.GetProperty("pet");

        // Null is omitted from the document rather than written out, so absence is what "cleared" looks like.
        Assert.False(pet.TryGetProperty("activeModelId", out _));
        var profiles = pet.GetProperty("profiles");
        Assert.False(profiles.TryGetProperty(imported.Id, out _));
        Assert.True(profiles.TryGetProperty("other-12345678", out _));
    }

    [Fact]
    public void Deleting_a_model_removes_only_its_directory()
    {
        var model = MakeModel(_root);
        var imported = Store().Import(model).Imported!;

        Assert.True(Store().Delete(imported.Id));
        Assert.False(Store().Delete(imported.Id));
        Assert.Empty(Store().List());
    }

    // ---------------------------------------------------------------- live tree

    /// <summary>
    /// A real model when one happens to be unpacked on this machine — <c>PARTNER_PET_SAMPLE_MODEL</c> if set,
    /// otherwise a scratch copy under the temp directory. The tree is never committed and is copied only into the
    /// throwaway store, so the check is a no-op on a clean checkout.
    /// </summary>
    [Fact]
    public void A_real_model_with_escaping_sound_references_imports()
    {
        var live = Environment.GetEnvironmentVariable("PARTNER_PET_SAMPLE_MODEL")
            ?? Path.Combine(Path.GetTempPath(), "l2dmodel", "haru");
        // Present is not enough: a partly downloaded or partly cleaned tree would fail the import for a reason
        // that says nothing about the importer. The model3 and its moc are the two files the check needs, so
        // their absence means "no sample here", exactly like a missing directory.
        if (!File.Exists(Path.Combine(live, "haru_greeter_t03.model3.json"))
            || !File.Exists(Path.Combine(live, "haru_greeter_t03.moc3")))
        {
            return;
        }

        var store = Store();
        var result = store.Import(live);

        Assert.NotNull(result.Imported);
        Assert.Equal(2, result.Imported!.Motions.Select(m => m.Group).Distinct().Count());
        Assert.Equal(8, result.Imported.Expressions.Count);
        Assert.True(result.Imported.HasHitAreas);

        var directory = Path.GetDirectoryName(result.Imported.Model3)!;
        Assert.True(File.Exists(Path.Combine(directory, "haru_greeter_t03.2048", "texture_00.png")));
        Assert.True(File.Exists(Path.Combine(directory, "motion", "haru_g_idle.motion3.json")));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pet-model.json")));
        Assert.Equal(3, manifest.RootElement.GetProperty("warnings").GetArrayLength());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Creates a directory junction. A symlink needs SeCreateSymbolicLinkPrivilege (developer mode or elevation),
    /// a junction does not — and for the importer both are the same reparse point.
    /// </summary>
    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });

            result?.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the reparse attribute on one entry without extracting: the same bit
    /// <see cref="ZipArchiveEntry.ExternalAttributes"/> exposes, written into the central directory.
    /// </summary>
    private static void MarkReparseEntry(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        var wanted = Encoding.UTF8.GetBytes(name);

        for (var offset = 0; offset + 46 <= bytes.Length; offset++)
        {
            if (bytes[offset] != 0x50 || bytes[offset + 1] != 0x4b || bytes[offset + 2] != 0x01
                || bytes[offset + 3] != 0x02)
            {
                continue;
            }

            var nameLength = BitConverter.ToUInt16(bytes, offset + 28);
            if (nameLength != wanted.Length || offset + 46 + nameLength > bytes.Length)
            {
                continue;
            }

            if (!bytes.AsSpan(offset + 46, nameLength).SequenceEqual(wanted))
            {
                continue;
            }

            // External attributes live at offset 38 of the central header: 0x400 << 16 is the reparse bit.
            BitConverter.GetBytes(0x400 << 16).CopyTo(bytes, offset + 38);
        }

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Builds a stored-mode zip byte by byte with the UTF-8 flag clear, so the name bytes are exactly what a GBK
    /// writer emits. <see cref="ZipArchive"/> always sets the flag and would hide the bug this covers.
    /// </summary>
    private static void WriteRawZip(string path, IReadOnlyList<(byte[] Name, byte[] Bytes)> entries)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var central = new List<(byte[] Name, byte[] Bytes, uint Crc, uint Offset)>();

        foreach (var (name, bytes) in entries)
        {
            var offset = (uint)stream.Position;
            var crc = Crc32(bytes);

            writer.Write(0x04034b50u);
            writer.Write((ushort)20);   // version needed
            writer.Write((ushort)0);    // flags: UTF-8 clear
            writer.Write((ushort)0);    // stored
            writer.Write((ushort)0);    // time
            writer.Write((ushort)0x21); // date
            writer.Write(crc);
            writer.Write((uint)bytes.Length);
            writer.Write((uint)bytes.Length);
            writer.Write((ushort)name.Length);
            writer.Write((ushort)0);
            writer.Write(name);
            writer.Write(bytes);

            central.Add((name, bytes, crc, offset));
        }

        var directoryOffset = (uint)stream.Position;
        foreach (var (name, bytes, crc, offset) in central)
        {
            writer.Write(0x02014b50u);
            writer.Write((ushort)20);   // version made by
            writer.Write((ushort)20);   // version needed
            writer.Write((ushort)0);    // flags
            writer.Write((ushort)0);    // stored
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(crc);
            writer.Write((uint)bytes.Length);
            writer.Write((uint)bytes.Length);
            writer.Write((ushort)name.Length);
            writer.Write((ushort)0);    // extra
            writer.Write((ushort)0);    // comment
            writer.Write((ushort)0);    // disk
            writer.Write((ushort)0);    // internal attributes
            writer.Write(0u);           // external attributes
            writer.Write(offset);
            writer.Write(name);
        }

        var directorySize = (uint)stream.Position - directoryOffset;
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)central.Count);
        writer.Write((ushort)central.Count);
        writer.Write(directorySize);
        writer.Write(directoryOffset);
        writer.Write((ushort)0);
    }

    private static uint Crc32(byte[] bytes)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// The pet surface over HTTP: the feed the window polls, and the settings bounds it round-trips through the
/// settings endpoint.
/// </summary>
public sealed class PetEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "pet";

    protected override string ReadinessPath => "/api/settings";

    [Fact]
    public async Task An_empty_feed_reports_no_replies_and_the_current_revision()
    {
        var feed = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");

        Assert.False(feed.GetProperty("speaking").GetBoolean());
        Assert.Equal(_context.Settings.Revision, feed.GetProperty("settingsRevision").GetInt32());
        Assert.Empty(feed.GetProperty("replies").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task The_feed_reports_what_the_pet_window_reported()
    {
        var post = await _http.PostAsJsonAsync("/api/pet/status", new { error = "模型加载失败：moc3" });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var feed = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");
        Assert.Equal("模型加载失败：moc3", feed.GetProperty("lastError").GetString());

        // An empty report is how the window says "the problem is gone", not how it says "forget I said anything".
        await _http.PostAsJsonAsync("/api/pet/status", new { error = "" });
        Assert.Equal(JsonValueKind.Null, (await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed"))
            .GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task The_feed_carries_only_finalized_assistant_replies()
    {
        // Seeded through the store rather than the endpoint: there is no "write an event" endpoint by design.
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "hello",
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = "first reply",
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Tool,
            Source = InputSource.Text,
            Body = "tool output",
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = "second reply",
        });

        var feed = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");
        var replies = feed.GetProperty("replies").EnumerateArray()
            .Select(reply => (Seq: reply.GetProperty("seq").GetInt64(), Body: reply.GetProperty("body").GetString()!))
            .ToList();

        Assert.Equal(["first reply", "second reply"], replies.Select(reply => reply.Body));
        Assert.All(replies, reply => Assert.True(reply.Seq > 0));
    }

    [Fact]
    public async Task The_feed_keeps_the_last_five_and_truncates_a_very_long_reply()
    {
        for (var i = 0; i < 7; i++)
        {
            await _context.Session.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.Assistant,
                Source = InputSource.Text,
                Body = $"reply {i}",
            });
        }

        var feed = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");
        var bodies = feed.GetProperty("replies").EnumerateArray()
            .Select(reply => reply.GetProperty("body").GetString()!)
            .ToList();

        Assert.Equal(["reply 2", "reply 3", "reply 4", "reply 5", "reply 6"], bodies);

        var longReply = new string('長', 5000);
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = longReply,
        });

        var after = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");
        var last = after.GetProperty("replies").EnumerateArray().Last();
        Assert.Equal(4000, last.GetProperty("body").GetString()!.Length);
    }

    [Fact]
    public async Task The_feed_can_be_read_from_a_cursor()
    {
        var first = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = "old",
        });
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = "new",
        });

        var feed = await _http.GetFromJsonAsync<JsonElement>($"/api/pet/feed?after={first}");
        var bodies = feed.GetProperty("replies").EnumerateArray()
            .Select(reply => reply.GetProperty("body").GetString()!)
            .ToList();

        Assert.Equal(["new"], bodies);
    }

    [Fact]
    public async Task The_feed_returns_the_cursor_past_events_that_can_never_be_replies()
    {
        await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = "reply",
        });
        var toolSeq = await _context.Session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Tool,
            Source = InputSource.Text,
            Body = "tool output",
        });

        var feed = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");
        var lastSeq = feed.GetProperty("lastSeq").GetInt64();

        // The tool event after the reply has to be covered by the cursor; otherwise every poll would re-read it
        // and answer with the same reply again.
        Assert.True(lastSeq >= toolSeq);

        var caughtUp = await _http.GetFromJsonAsync<JsonElement>($"/api/pet/feed?after={lastSeq}");
        Assert.Equal(lastSeq, caughtUp.GetProperty("lastSeq").GetInt64());
        Assert.Empty(caughtUp.GetProperty("replies").EnumerateArray());
    }

    [Fact]
    public async Task Settings_are_visible_to_the_feed_and_the_revision_moves_on_save()
    {
        var before = (await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed")).GetProperty("settingsRevision").GetInt32();

        var save = await _http.PutAsJsonAsync("/api/settings", new { pet = new { enabled = true } });
        save.EnsureSuccessStatusCode();

        var after = (await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed")).GetProperty("settingsRevision").GetInt32();
        Assert.Equal(before + 1, after);

        var view = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(view.GetProperty("pet").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task An_import_without_a_path_is_refused()
    {
        var response = await _http.PostAsJsonAsync("/api/pet/models/import", new { path = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Importing_a_path_that_does_not_exist_reports_the_server_message()
    {
        var response = await _http.PostAsJsonAsync("/api/pet/models/import", new { path = @"C:\no\such\model" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("找不到", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_an_unknown_model_is_not_an_error()
    {
        var response = await _http.DeleteAsync("/api/pet/models/nothing-here");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetBoolean());
    }

    // ---------------------------------------------------------------- settings bounds

    private async Task<List<string>> PetErrorsAsync(object pet)
    {
        var response = await _http.PutAsJsonAsync("/api/settings", new { pet });
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return [];
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors").EnumerateArray().Select(error => error.GetString()!).ToList();
    }

    [Fact]
    public async Task A_scale_below_half_is_refused()
    {
        var errors = await PetErrorsAsync(new { scale = 0.4 });

        Assert.Contains(errors, error => error.Contains("pet.scale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_idle_range_in_which_the_minimum_exceeds_the_maximum_is_refused()
    {
        var errors = await PetErrorsAsync(new { idleMinSeconds = 100, idleMaxSeconds = 60 });

        Assert.Contains(errors, error => error.Contains("idleMinSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_greeting_time_that_is_not_a_wall_clock_time_is_refused()
    {
        var errors = await PetErrorsAsync(new { greetingTimes = new[] { "09:00", "25:61" } });

        Assert.Contains(errors, error => error.Contains("greetingTimes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task More_than_twelve_greeting_times_are_refused()
    {
        var times = Enumerable.Range(0, 13).Select(hour => $"{hour:00}:00").ToArray();

        var errors = await PetErrorsAsync(new { greetingTimes = times });

        Assert.Contains(errors, error => error.Contains("greetingTimes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_keyword_weight_outside_one_to_ten_and_a_blank_keyword_are_refused()
    {
        var errors = await PetErrorsAsync(new
        {
            emotions = new
            {
                keywords = new Dictionary<string, object[]>
                {
                    ["smile"] = [new { text = " ", weight = 1 }],
                    ["angry"] = [new { text = "哼", weight = 0 }],
                    ["cry"] = [new { text = "哭", weight = 11 }],
                },
            },
        });

        Assert.Contains(errors, error => error.Contains("keywords", StringComparison.Ordinal) || error.Contains("关键词", StringComparison.Ordinal));
        Assert.Equal(2, errors.Count(error => error.Contains("weight", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_unknown_emotion_key_saves_and_is_dropped()
    {
        var save = await _http.PutAsJsonAsync("/api/settings", new
        {
            pet = new
            {
                emotions = new
                {
                    keywords = new Dictionary<string, object[]>
                    {
                        ["jealous"] = [new { text = "吃醋", weight = 3 }],
                        ["smile"] = [new { text = "哈哈", weight = 3 }],
                    },
                },
            },
        });

        save.EnsureSuccessStatusCode();
        var keywords = (await save.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("settings").GetProperty("pet").GetProperty("emotions").GetProperty("keywords");

        Assert.True(keywords.TryGetProperty("smile", out _));
        Assert.False(keywords.TryGetProperty("jealous", out _));
    }

    [Fact]
    public async Task An_active_model_that_was_never_imported_is_refused()
    {
        var errors = await PetErrorsAsync(new { activeModelId = "not-imported" });

        Assert.Contains(errors, error => error.Contains("activeModelId", StringComparison.Ordinal));
    }
}
public sealed class PetPreviewTests : ApiFixture
{
    protected override string WorkspaceName => "pet-preview";

    protected override string ReadinessPath => "/api/settings";

    [Fact]
    public async Task A_preview_is_answered_once_and_stays_answered()
    {
        var first = await _http.PostAsJsonAsync("/api/pet/preview", new { action = "nod", expression = "" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var seq = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("seq").GetInt64();

        var feed = await _http.GetFromJsonAsync<JsonElement>($"/api/pet/feed?previewAfter={seq - 1}");
        var preview = feed.GetProperty("preview");
        Assert.Equal(seq, preview.GetProperty("seq").GetInt64());
        Assert.Equal("nod", preview.GetProperty("action").GetString());

        // The window sends back the sequence it applied; the same request must not play a second time.
        var again = await _http.GetFromJsonAsync<JsonElement>($"/api/pet/feed?previewAfter={seq}");
        Assert.Equal(JsonValueKind.Null, again.GetProperty("preview").ValueKind);
    }

    [Fact]
    public async Task A_second_preview_replaces_the_first_before_it_is_seen()
    {
        // Depth one on purpose: three clicks mean the third motion, not three motions queued over each other.
        await _http.PostAsJsonAsync("/api/pet/preview", new { action = "nod", expression = "" });
        await _http.PostAsJsonAsync("/api/pet/preview", new { action = "", expression = "smile" });

        var feed = await _http.GetFromJsonAsync<JsonElement>("/api/pet/feed");
        var preview = feed.GetProperty("preview");
        Assert.Equal(string.Empty, preview.GetProperty("action").GetString());
        Assert.Equal("smile", preview.GetProperty("expression").GetString());
    }
}
