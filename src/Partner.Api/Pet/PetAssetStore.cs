using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Partner.Api.Settings;

namespace Partner.Api.Pet;

/// <summary>Whether the Cubism Core is on disk, which SDK it came from, and the hash of the file that will be loaded.</summary>
public sealed record PetCoreStatus(bool Installed, string Sdk, string? Sha256);

/// <summary>One playable motion: the group it belongs to, its index inside that group, and the name the UI shows.</summary>
public sealed record PetMotionInfo(string Group, int No, string Name);

/// <summary>A parameter declared by display info; this is descriptive metadata, not proof of a runtime parameter.</summary>
public sealed record PetParameterInfo(string Id, string Name, string? GroupId);

/// <summary>A display-info parameter group and its optional parent group.</summary>
public sealed record PetParameterGroupInfo(string Id, string Name, string? GroupId);

/// <summary>
/// One imported model. <see cref="Model3"/> is the absolute path the pet window loads; everything else is what
/// the settings page needs to offer the model, its motions and its expressions.
/// </summary>
public sealed record PetModelInfo(
    string Id,
    string Name,
    string Model3,
    IReadOnlyList<PetMotionInfo> Motions,
    IReadOnlyList<string> Expressions,
    bool HasHitAreas,
    long Bytes,
    IReadOnlyList<PetParameterInfo> Parameters,
    IReadOnlyList<PetParameterGroupInfo> ParameterGroups,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The outcome of an import: either the model, or the model3 files found inside the input so the caller can pick
/// one. Both being empty never happens — an input with no model3 is an error.
/// </summary>
public sealed record PetImportResult(PetModelInfo? Imported, IReadOnlyList<string> Candidates);

/// <summary>One finalized assistant reply, as the pet window shows it above the model.</summary>
public sealed record PetReply(long Seq, string Body);

/// <summary>
/// A 「预览」 request from the settings page. <paramref name="Seq"/> lets the window apply each one exactly once;
/// <paramref name="Action"/> is an abstract action name and <paramref name="Expression"/> an expression name, both
/// empty when that half is not being previewed.
/// </summary>
public sealed record PetPreview(long Seq, string Action, string Expression);

/// <summary>
/// Everything the pet window polls for, in one response so a tick costs one request. <see cref="LastSeq"/> is the
/// highest event the server considered, not the highest reply: the window sends it back as <c>after</c>, so events
/// that can never be replies are not walked again on every tick.
/// </summary>
public sealed record PetFeed(
    bool Speaking,
    int SettingsRevision,
    IReadOnlyList<PetReply> Replies,
    string? LastError,
    long LastSeq,
    PetPreview? Preview);

/// <summary>The import failed for a reason the user can act on; the message is what the endpoint returns.</summary>
public sealed class PetImportException : Exception
{
    public PetImportException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The desktop pet's assets under <c>%LOCALAPPDATA%</c>: the user-supplied Cubism Core and the imported models.
/// Both are licensed per user, so nothing here is checked in. The pet window loads these files over the custom
/// protocol, so an import must never write outside its own directory or bring a script; that is why imports get
/// their own extraction, allowlist and manifest instead of being trusted because the user picked the path.
/// </summary>
public sealed class PetAssetStore
{
    /// <summary>The one SDK archive this build understands, by whole-file hash.</summary>
    public const string SdkVersion = "5-r.5";

    public const string SdkArchiveSha256 =
        "67064a7fb1812cf502f5c4a03bfe12cc638c75a621bb4acf06bb28763df06ba0";

    /// <summary>A model is small; a "model" larger than this is a mis-picked directory, not a model.</summary>
    private const long MaxModelBytes = 200L * 1024 * 1024;

    private const int MaxFiles = 2000;

    // Display metadata is optional and should not consume the whole model import budget during JSON parsing.
    private const long MaxDisplayInfoBytes = 4L * 1024 * 1024;

    /// <summary>Only what Live2D reads. <c>.js</c> is excluded on purpose — see <see cref="ValidateName"/>.</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".moc3", ".png", ".jpg", ".webp", ".wav", ".mp3", ".ogg", ".flac",
    };

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly string _models;

    /// <summary>Serializes imports and deletes. Reads are plain directory scans, so they do not take it.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _errorGate = new();
    private string? _lastError;

    /// <summary>The settings page's most recent preview request, consumed by the pet window's next feed poll.</summary>
    private readonly object _previewGate = new();
    private PetPreview? _pendingPreview;
    private long _previewSeq;

    /// <param name="root">
    /// Overrides the storage root. Tests point it at a throwaway directory; production leaves it null so the
    /// assets live under <c>%LOCALAPPDATA%\Partner\pet</c>, beside the settings but outside the workspace.
    /// </param>
    public PetAssetStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Partner", "pet"));
        _models = Path.Combine(_root, "models");
    }

    /// <summary>The last problem the pet runtime reported, or null once it has been acknowledged.</summary>
    public string? LastError
    {
        get
        {
            lock (_errorGate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>
    /// Records what the pet window last failed at. The page cannot read the webview's console, so a broken model
    /// has to travel back through the API or it is invisible.
    /// </summary>
    public void Report(string? error)
    {
        lock (_errorGate)
        {
            _lastError = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        }
    }

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// Queues a preview for the pet window. Depth one on purpose: a person clicking 「预览」 three times wants the
    /// third, not a queue of three motions playing over each other, and the window may not be open at all.
    /// </summary>
    public PetPreview RequestPreview(string action, string expression)
    {
        lock (_previewGate)
        {
            _previewSeq += 1;
            _pendingPreview = new PetPreview(_previewSeq, action, expression);
            return _pendingPreview;
        }
    }

    /// <summary>
    /// The outstanding preview with its sequence, or null when there is none or it was already applied. The window
    /// passes the last sequence it saw so a request cannot be played twice by two polls racing.
    /// </summary>
    public PetPreview? PendingPreview(long after)
    {
        lock (_previewGate)
        {
            return _pendingPreview is { } preview && preview.Seq > after ? preview : null;
        }
    }

    // ---------------------------------------------------------------- core

    /// <summary>
    /// Reads the installed Core. The reported hash is the hash of the <c>.min.js</c> that will actually be loaded,
    /// not of the archive it came from: two archive hashes can yield the same runtime, and it is the runtime the
    /// page executes.
    /// </summary>
    public PetCoreStatus Status()
    {
        var directory = Path.Combine(_root, "core");
        var runtime = Path.Combine(directory, "live2dcubismcore.min.js");
        var licence = Path.Combine(directory, "LICENSE.md");
        if (!File.Exists(runtime) || !File.Exists(licence))
        {
            return new PetCoreStatus(false, SdkVersion, null);
        }

        using var stream = File.OpenRead(runtime);
        return new PetCoreStatus(true, SdkVersion, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    /// <summary>
    /// Installs the Core from the one SDK archive this build accepts. Anything else is refused before it is read:
    /// an arbitrary zip would put arbitrary bytes where the page expects a runtime.
    /// </summary>
    /// <exception cref="PetImportException">The archive is not the expected SDK, or holds no Core.</exception>
    public PetCoreStatus ImportCore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new PetImportException($"找不到文件：{path}");
        }

        _gate.Wait();
        try
        {
            using (var stream = File.OpenRead(path))
            {
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actual, SdkArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PetImportException(
                        $"这不是 {SdkVersion} 的官方压缩包：SHA-256 是 {actual}，应为 {SdkArchiveSha256}");
                }
            }

            // Extracted into a directory of its own first, then swapped in: a crash mid-extraction leaves the
            // previous, working Core in place rather than a half-written runtime the page would fail on.
            var staging = Path.Combine(_root, ".core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                ExtractCore(path, staging);

                var target = Path.Combine(_root, "core");
                var retired = Path.Combine(_root, ".core-old-" + Guid.NewGuid().ToString("N"));
                var hadPrevious = Directory.Exists(target);
                if (hadPrevious)
                {
                    Directory.Move(target, retired);
                }

                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    if (hadPrevious)
                    {
                        Directory.Move(retired, target);
                    }

                    throw;
                }

                if (hadPrevious)
                {
                    TryDelete(retired);
                }
            }
            finally
            {
                TryDelete(staging);
            }

            return Status();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Copies the two entries out of the SDK archive. The SDK's top-level folder name changes between releases, so
    /// the entries are matched by tail rather than by a fixed prefix.
    /// </summary>
    private static void ExtractCore(string archivePath, string target)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var names = ZipNames.Read(archivePath);
        if (names.Count != archive.Entries.Count)
        {
            throw new PetImportException("无法读取压缩包目录");
        }

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/Core/live2dcubismcore.min.js"] = "live2dcubismcore.min.js",
            ["/Core/LICENSE.md"] = "LICENSE.md",
        };

        var found = 0;
        for (var index = 0; index < names.Count; index++)
        {
            var entry = archive.Entries[index];
            var name = names[index];
            if (name.EndsWith('/'))
            {
                continue;
            }

            var match = wanted.FirstOrDefault(pair => name.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is null)
            {
                continue;
            }

            using var input = entry.Open();
            using var output = File.Create(Path.Combine(target, match.Value));
            input.CopyTo(output);
            found++;
        }

        if (found != wanted.Count)
        {
            throw new PetImportException($"压缩包里没有 Cubism Core 的 {wanted.Count} 个文件");
        }
    }

    // ---------------------------------------------------------------- models

    /// <summary>Every imported model, read off disk. A directory without a readable manifest is skipped.</summary>
    public IReadOnlyList<PetModelInfo> List()
    {
        if (!Directory.Exists(_models))
        {
            return [];
        }

        var models = new List<PetModelInfo>();
        foreach (var directory in Directory.EnumerateDirectories(_models))
        {
            if (ReadManifest(directory) is { } manifest)
            {
                models.Add(ToInfo(directory, manifest));
            }
        }

        return models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ThenBy(model => model.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The ids only, which is what settings validation asks for on every save.</summary>
    public IReadOnlyCollection<string> ModelIds() => List().Select(model => model.Id).ToList();

    /// <summary>
    /// Imports a model folder or zip. With <paramref name="model3"/> null, an input holding several
    /// <c>*.model3.json</c> is reported as candidates: guessing which one the person meant would load the wrong
    /// character.
    /// </summary>
    /// <exception cref="PetImportException">The input is not a usable model; the message names what is wrong.</exception>
    public PetImportResult Import(string path, string? model3 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _gate.Wait();
        try
        {
            var isArchive = File.Exists(path);
            var isDirectory = Directory.Exists(path);
            if (!isArchive && !isDirectory)
            {
                throw new PetImportException($"找不到要导入的路径：{path}");
            }

            if (isArchive && !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new PetImportException("只支持 .zip 压缩包或文件夹");
            }

            // Created only once the input is known to be there: a typo in a path must not leave a models
            // directory behind on a machine that has never imported anything.
            Directory.CreateDirectory(_models);

            var staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string name;
                if (isArchive)
                {
                    ExtractZip(path, staging);
                    name = Path.GetFileNameWithoutExtension(path);
                }
                else
                {
                    ValidateDirectoryTree(path);
                    name = new DirectoryInfo(Path.GetFullPath(path)).Name;
                }

                var source = isArchive ? staging : Path.GetFullPath(path);

                var candidates = Directory
                    .EnumerateFiles(source, "*.model3.json", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(source, file).Replace('\\', '/'))
                    .OrderBy(relative => relative, StringComparer.Ordinal)
                    .ToList();

                string chosen;
                if (model3 is null)
                {
                    if (candidates.Count == 0)
                    {
                        throw new PetImportException("这个文件夹里没有 model3.json");
                    }

                    if (candidates.Count > 1)
                    {
                        return new PetImportResult(null, candidates);
                    }

                    chosen = candidates[0];
                }
                else
                {
                    chosen = SelectModel3(source, model3);
                }

                // A zip's display name is the model's own folder inside it; with several candidates the choice
                // already tells the page which folder it picked, and with the model3 at the archive root the
                // archive's own name is the only name there is.
                if (isArchive && chosen.Contains('/'))
                {
                    name = chosen[..chosen.IndexOf('/')];
                }

                var model3Full = Path.GetFullPath(Path.Combine(source, chosen.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(model3Full))
                {
                    throw new PetImportException($"找不到 model3：{chosen}");
                }

                var modelRoot = Path.GetDirectoryName(model3Full)!;
                var model3Rel = Path.GetRelativePath(modelRoot, model3Full).Replace('\\', '/');

                var parsed = ParseModel3(model3Full);

                // Relative path → staging file, for the model3 and every reference that survives.
                var files = new Dictionary<string, string>(StringComparer.Ordinal) { [model3Rel] = model3Full };
                foreach (var reference in parsed.Required)
                {
                    var full = Resolve(modelRoot, reference.Path)
                        ?? throw new PetImportException(
                            $"模型引用的文件不在模型目录内（{reference.Label}）：{reference.Path}");

                    if (!File.Exists(full))
                    {
                        throw new PetImportException($"模型引用的文件不存在（{reference.Label}）：{reference.Path}");
                    }

                    RejectReparse(full, reference.Path);
                    files[Path.GetRelativePath(modelRoot, full).Replace('\\', '/')] = full;
                }

                var warnings = new List<string>();
                var parameters = new List<PetParameterInfo>();
                var parameterGroups = new List<PetParameterGroupInfo>();
                foreach (var reference in parsed.Optional)
                {
                    var full = Resolve(modelRoot, reference.Path);
                    if (full is null)
                    {
                        // A sound or a display name pointing outside the model is common in models that were
                        // shipped as a set sharing one folder; it is not worth failing an otherwise usable model.
                        warnings.Add($"已跳过模型目录外的可选文件（{reference.Label}）：{reference.Path}");
                        continue;
                    }

                    if (!File.Exists(full))
                    {
                        warnings.Add($"已跳过缺失的可选文件（{reference.Label}）：{reference.Path}");
                        continue;
                    }

                    if (reference.Label == "fileReferences.displayInfo")
                    {
                        if (!reference.Path.EndsWith(".cdi3.json", StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add($"已跳过格式不受支持的显示信息：{reference.Path}");
                            continue;
                        }

                        RejectReparse(full, reference.Path);
                        if (new FileInfo(full).Length > MaxDisplayInfoBytes)
                        {
                            warnings.Add($"已跳过过大的显示信息：{reference.Path}");
                            continue;
                        }

                        files[Path.GetRelativePath(modelRoot, full).Replace('\\', '/')] = full;
                        ReadDisplayInfo(full, reference.Path, parameters, parameterGroups, warnings);
                    }
                    else if (IsSoundPath(reference.Path))
                    {
                        RejectReparse(full, reference.Path);
                        files[Path.GetRelativePath(modelRoot, full).Replace('\\', '/')] = full;
                    }
                    else
                    {
                        warnings.Add($"已跳过格式不受支持的声音文件：{reference.Path}");
                    }
                }

                var hash = HashTree(files);
                var id = Slug(name) + "-" + hash[..8];
                var target = Path.Combine(_models, id);

                if (ReadManifest(target) is { } existing)
                {
                    // The id carries eight hex of the content hash, so the same tree imported twice lands on the
                    // same directory, and its files are already on disk.
                    return new PetImportResult(ToInfo(target, existing), []);
                }

                var staged = Path.Combine(_models, ".import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staged);
                try
                {
                    foreach (var (relative, full) in files)
                    {
                        var destination = Path.Combine(staged, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(full, destination, overwrite: true);
                    }

                    var manifest = new ModelManifest
                    {
                        Id = id,
                        Name = name,
                        Model3Rel = model3Rel,
                        Motions = parsed.Motions,
                        Expressions = parsed.Expressions,
                        HasHitAreas = parsed.HasHitAreas,
                        Parameters = parameters,
                        ParameterGroups = parameterGroups,
                        Warnings = warnings,
                    };

                    File.WriteAllText(
                        Path.Combine(staged, ManifestName),
                        JsonSerializer.Serialize(manifest, ManifestJson));

                    if (Directory.Exists(target))
                    {
                        // A directory that exists without a readable manifest is what a crash mid-import leaves;
                        // it cannot be loaded, so it is replaced rather than kept for ever.
                        TryDelete(target);
                    }

                    Directory.Move(staged, target);
                }
                finally
                {
                    TryDelete(staged);
                }

                return new PetImportResult(ToInfo(target, ReadManifest(target)!), []);
            }
            finally
            {
                TryDelete(staging);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes one imported model. The <c>models</c> directory itself stays, so a later import has a home.</summary>
    public bool Delete(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        _gate.Wait();
        try
        {
            var directory = Path.Combine(_models, id);
            if (!Directory.Exists(directory))
            {
                return false;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- import helpers

    private const string ManifestName = "pet-model.json";

    /// <summary>
    /// Resolves the caller's model3 choice inside the staging tree. The parameter is a path the client sent, so it
    /// gets the same treatment as an archive member: relative, no escape, and actually a model3.
    /// </summary>
    private static string SelectModel3(string source, string model3)
    {
        var relative = model3.Trim();
        if (relative.Length == 0)
        {
            throw new PetImportException("model3 不能为空");
        }

        if (Resolve(source, relative) is not { } full || !File.Exists(full))
        {
            throw new PetImportException($"model3 不在导入的文件里：{relative}");
        }

        if (!full.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new PetImportException($"model3 必须以 .model3.json 结尾：{relative}");
        }

        return Path.GetRelativePath(source, full).Replace('\\', '/');
    }

    /// <summary>
    /// The full path of <paramref name="reference"/> under <paramref name="root"/>, or null when it would land
    /// outside. Every name is checked before it is joined: a reference is data from a downloaded file, not a path
    /// the user typed.
    /// </summary>
    private static string? Resolve(string root, string reference)
    {
        var normalized = reference.Replace('\\', '/').Trim();
        if (normalized.Length == 0
            || normalized.StartsWith('/')
            || normalized.Contains(':')
            || normalized.IndexOf('\0') >= 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return null;
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, parts)));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        return IsUnder(root, full) ? full : null;
    }

    private static bool IsUnder(string root, string path)
    {
        var full = Path.GetFullPath(root);
        if (!full.EndsWith(Path.DirectorySeparatorChar))
        {
            full += Path.DirectorySeparatorChar;
        }

        return path.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparse(string path, string display)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PetImportException($"不支持重分析点（{display}）：{path}");
        }
    }

    /// <summary>
    /// Counts and sizes an input directory without following a single reparse point. A junction anywhere in the
    /// tree is refused rather than traversed: it would let a "model folder" reach arbitrary files.
    /// </summary>
    private static void ValidateDirectoryTree(string path)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(path));

        long bytes = 0;
        var files = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PetImportException($"不支持重分析点：{directory}");
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PetImportException($"不支持重分析点：{child}");
                }

                pending.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PetImportException($"不支持重分析点：{file}");
                }

                if (++files > MaxFiles)
                {
                    throw new PetImportException($"文件太多：最多 {MaxFiles} 个");
                }

                bytes += new FileInfo(file).Length;
                if (bytes > MaxModelBytes)
                {
                    throw new PetImportException($"文件太大：最多 {MaxModelBytes / (1024 * 1024)} MB");
                }
            }
        }
    }

    /// <summary>
    /// Extracts a model archive into <paramref name="staging"/>. Every entry is validated first — names, types,
    /// total size, attributes — so a malicious archive is refused before anything is written.
    /// </summary>
    private static void ExtractZip(string path, string staging)
    {
        using var archive = ZipFile.OpenRead(path);
        var names = ZipNames.Read(path);
        if (names.Count != archive.Entries.Count)
        {
            throw new PetImportException("无法读取压缩包目录");
        }

        var targets = new List<string>(names.Count);
        long declared = 0;
        var files = 0;

        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];
            if (name.EndsWith('/'))
            {
                continue;
            }

            ValidateName(name);
            if ((archive.Entries[index].ExternalAttributes & ReparseAttribute) != 0)
            {
                throw new PetImportException($"不支持重分析点：{name}");
            }

            var target = Resolve(staging, name) ?? throw new PetImportException($"压缩包内有不安全的路径：{name}");
            targets.Add(target);

            if (++files > MaxFiles)
            {
                throw new PetImportException($"文件太多：最多 {MaxFiles} 个");
            }

            declared += archive.Entries[index].Length;
            if (declared > MaxModelBytes)
            {
                throw new PetImportException($"文件太大：最多 {MaxModelBytes / (1024 * 1024)} MB");
            }
        }

        var next = 0;
        long written = 0;
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index].EndsWith('/'))
            {
                continue;
            }

            var destination = targets[next++];
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = archive.Entries[index].Open();
            using var output = File.Create(destination);
            var buffer = ArrayPool<byte>.Shared.Rent(81_920);
            try
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // The declared length is only a claim; the bytes actually written are what fills the disk.
                    // Counted apart from the declared total: adding both to one counter would halve the real cap.
                    written += read;
                    if (written > MaxModelBytes)
                    {
                        throw new PetImportException($"文件太大：最多 {MaxModelBytes / (1024 * 1024)} MB");
                    }

                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// The per-entry checks: no absolute path, no parent reference, a known extension, and no script. <c>.js</c>
    /// gets its own message because the answer is not "unsupported format" — the pet page's asset origin lives in
    /// the webview's <c>script-src</c>, so a shipped script would run with the page's privileges.
    /// </summary>
    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name.IndexOf('\0') >= 0)
        {
            throw new PetImportException("压缩包内有空文件名");
        }

        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(name))
        {
            throw new PetImportException($"压缩包内有绝对路径：{name}");
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment == ".."))
        {
            throw new PetImportException($"压缩包内有上级目录引用：{name}");
        }

        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            throw new PetImportException($"不支持脚本文件（资产来源受 script-src 约束）：{name}");
        }

        if (!AllowedExtensions.Contains(extension))
        {
            throw new PetImportException($"不支持的文件类型：{name}");
        }
    }

    /// <summary>`FILE_ATTRIBUTE_REPARSE_POINT` in the high word a Windows-created zip stores its attributes in.</summary>
    private const int ReparseAttribute = 0x400 << 16;

    // ---------------------------------------------------------------- model3 reading

    private sealed record ModelReference(string Path, string Label);

    private sealed record ParsedModel3(
        IReadOnlyList<ModelReference> Required,
        IReadOnlyList<ModelReference> Optional,
        IReadOnlyList<PetMotionInfo> Motions,
        IReadOnlyList<string> Expressions,
        bool HasHitAreas);

    /// <summary>
    /// Reads the references a model3 declares. Required: the moc, every texture, motion and expression file, and
    /// physics/pose/userData when declared. Optional: motion sounds (motion sound is off by default) and display
    /// info; a missing optional reference is a warning, because failing the import would reject models that work.
    /// </summary>
    private static ParsedModel3 ParseModel3(string model3Path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(model3Path));
        }
        catch (JsonException error)
        {
            throw new PetImportException($"无法解析 model3：{error.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("FileReferences", out var references)
                || references.ValueKind != JsonValueKind.Object)
            {
                throw new PetImportException("model3 缺少 FileReferences");
            }

            var required = new List<ModelReference>();
            var optional = new List<ModelReference>();

            void AddRequired(JsonElement owner, string property, string label)
            {
                if (owner.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    required.Add(new ModelReference(value.GetString()!, label));
                }
            }

            if (!references.TryGetProperty("Moc", out var moc) || moc.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(moc.GetString()))
            {
                throw new PetImportException("model3 缺少 FileReferences.Moc");
            }

            required.Add(new ModelReference(moc.GetString()!, "fileReferences.moc"));

            if (references.TryGetProperty("Textures", out var textures) && textures.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var texture in textures.EnumerateArray())
                {
                    if (texture.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(texture.GetString()))
                    {
                        required.Add(new ModelReference(texture.GetString()!, $"fileReferences.textures[{index}]"));
                    }

                    index++;
                }
            }

            AddRequired(references, "Physics", "fileReferences.physics");
            AddRequired(references, "Pose", "fileReferences.pose");
            AddRequired(references, "UserData", "fileReferences.userData");

            if (references.TryGetProperty("DisplayInfo", out var displayInfo)
                && displayInfo.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(displayInfo.GetString()))
            {
                optional.Add(new ModelReference(displayInfo.GetString()!, "fileReferences.displayInfo"));
            }

            var expressions = new List<string>();
            if (references.TryGetProperty("Expressions", out var expressionArray)
                && expressionArray.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var expression in expressionArray.EnumerateArray())
                {
                    var label = $"fileReferences.expressions[{index}]";
                    if (expression.ValueKind == JsonValueKind.Object)
                    {
                        if (expression.TryGetProperty("File", out var file)
                            && file.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(file.GetString()))
                        {
                            required.Add(new ModelReference(file.GetString()!, label + ".file"));
                        }

                        var name = expression.TryGetProperty("Name", out var nameValue)
                            && nameValue.ValueKind == JsonValueKind.String
                                ? nameValue.GetString()
                                : null;

                        if (string.IsNullOrWhiteSpace(name)
                            && expression.TryGetProperty("File", out var forName)
                            && forName.ValueKind == JsonValueKind.String)
                        {
                            name = Stem(forName.GetString()!);
                        }

                        expressions.Add(string.IsNullOrWhiteSpace(name) ? $"expression{index}" : name!);
                    }

                    index++;
                }
            }

            var motions = new List<PetMotionInfo>();
            if (references.TryGetProperty("Motions", out var motionGroups) && motionGroups.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in motionGroups.EnumerateObject())
                {
                    if (group.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var index = 0;
                    foreach (var motion in group.Value.EnumerateArray())
                    {
                        if (motion.ValueKind == JsonValueKind.Object)
                        {
                            var label = $"fileReferences.motions.{group.Name}[{index}]";
                            if (motion.TryGetProperty("File", out var file)
                                && file.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(file.GetString()))
                            {
                                required.Add(new ModelReference(file.GetString()!, label + ".file"));
                            }

                            if (motion.TryGetProperty("Sound", out var sound)
                                && sound.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(sound.GetString()))
                            {
                                optional.Add(new ModelReference(sound.GetString()!, label + ".sound"));
                            }
                        }

                        motions.Add(new PetMotionInfo(group.Name, index, $"{group.Name}_{index}"));
                        index++;
                    }
                }
            }

            var hasHitAreas = root.TryGetProperty("HitAreas", out var hitAreas)
                && hitAreas.ValueKind == JsonValueKind.Array
                && hitAreas.GetArrayLength() > 0;

            return new ParsedModel3(required, optional, motions, expressions, hasHitAreas);
        }
    }

    private static void ReadDisplayInfo(
        string path, string reference, List<PetParameterInfo> parameters,
        List<PetParameterGroupInfo> groups, List<string> warnings)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var data = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (data.Length + count > MaxDisplayInfoBytes)
                    {
                        warnings.Add($"已跳过过大的显示信息：{reference}");
                        return;
                    }

                    data.Write(buffer, 0, count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            data.Position = 0;
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"无法解析显示信息：{reference}");
                return;
            }

            ReadEntries("Parameters", (id, name, group) => parameters.Add(new PetParameterInfo(id, name, group)));
            ReadEntries("ParameterGroups", (id, name, group) => groups.Add(new PetParameterGroupInfo(id, name, group)));

            void ReadEntries(string property, Action<string, string, string?> add)
            {
                if (!root.TryGetProperty(property, out var entries)) return;
                if (entries.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add($"显示信息中的 {property} 不是数组：{reference}");
                    return;
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries.EnumerateArray())
                {
                    var id = StringProperty(entry, "Id");
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    {
                        warnings.Add($"已跳过无效或重复的 {property} 项：{reference}");
                        continue;
                    }

                    var name = StringProperty(entry, "Name");
                    var group = StringProperty(entry, "GroupId");
                    add(id, string.IsNullOrWhiteSpace(name) ? id : name, string.IsNullOrWhiteSpace(group) ? null : group);
                }
            }
        }
        catch (JsonException)
        {
            warnings.Add($"无法解析显示信息：{reference}");
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Whether an optional reference is a motion sound. The model3's only other optional reference is display
    /// info (<c>.cdi3.json</c>), so the extension is what decides: a file it cannot decode is not a sound.
    /// </summary>
    private static bool IsSoundPath(string reference)
    {
        var extension = Path.GetExtension(reference.Replace('\\', '/'));
        return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>expressions/F01.exp3.json</c> → <c>F01</c>: an unnamed expression still needs a label to offer.</summary>
    private static string Stem(string file)
    {
        var name = Path.GetFileName(file.Replace('\\', '/'));
        if (name.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^".exp3.json".Length];
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    // ---------------------------------------------------------------- manifest

    private sealed record ModelManifest
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Model3Rel { get; init; } = string.Empty;

        public IReadOnlyList<PetMotionInfo> Motions { get; init; } = [];

        public IReadOnlyList<string> Expressions { get; init; } = [];

        public bool HasHitAreas { get; init; }

        public IReadOnlyList<PetParameterInfo> Parameters { get; init; } = [];

        public IReadOnlyList<PetParameterGroupInfo> ParameterGroups { get; init; } = [];

        /// <summary>Optional references or metadata that could not be used during import.</summary>
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    private static ModelManifest? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(path), ManifestJson);
            if (manifest is null
                || string.IsNullOrWhiteSpace(manifest.Id)
                || string.IsNullOrWhiteSpace(manifest.Model3Rel)
                || !File.Exists(Path.Combine(directory, manifest.Model3Rel.Replace('/', Path.DirectorySeparatorChar))))
            {
                return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static PetModelInfo ToInfo(string directory, ModelManifest manifest) => new(
        manifest.Id,
        manifest.Name,
        Path.Combine(directory, manifest.Model3Rel.Replace('/', Path.DirectorySeparatorChar)),
        manifest.Motions,
        manifest.Expressions,
        manifest.HasHitAreas,
        Bytes(directory),
        manifest.Parameters,
        manifest.ParameterGroups,
        manifest.Warnings);

    private static long Bytes(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    // ---------------------------------------------------------------- ids and hashing

    /// <summary>
    /// The ascii part of a model id: letters and digits, runs of anything else folded to one dash. Chinese folder
    /// names become an empty prefix and fall back to <c>model</c>, because the id is a directory name first.
    /// </summary>
    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "model" : slug;
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    /// <summary>SHA-256 over the sorted relative paths and their bytes: the model's identity, independent of where it was unpacked.</summary>
    private static string HashTree(IReadOnlyDictionary<string, string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, full) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));

            using var stream = File.OpenRead(full);
            var buffer = ArrayPool<byte>.Shared.Rent(81_920);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover staging directory is harmless; the next import uses a fresh name.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}