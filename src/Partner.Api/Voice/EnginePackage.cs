using System.IO.Compression;
using System.Security.Cryptography;

namespace Partner.Api.Voice;

/// <summary>One engine's on-disk readiness: installed means every file the engine needs to start is present.</summary>
public sealed record EngineReadiness(bool Installed, IReadOnlyList<string> Missing);

/// <summary>What the settings page fills in for an engine it has just found. Paths are relative to the app directory
/// when the engines live in the default place, absolute when the user pointed at another folder.</summary>
public sealed record EngineDefaults(string Command, IReadOnlyList<string> Arguments, string WorkingDirectory, int Port);

/// <summary>Where one engine's runtime+models archive is fetched from. A null/blank Url means "not published".</summary>
public sealed record EnginePackageSource(string Id, string? Url, string? Sha256);

/// <summary>Phase is one of idle | downloading | extracting | done | failed | cancelled.</summary>
public sealed record EngineDownloadState(string Phase, string? Detail, long BytesDone, long? BytesTotal, string? Error);

public sealed record EnginePackageStatus(
    string Root,
    bool CustomRoot,
    EngineReadiness Tts,
    EngineReadiness Kws,
    EngineDefaults TtsDefaults,
    EngineDefaults KwsDefaults,
    bool DownloadAvailable,
    EngineDownloadState Download);

/// <summary>
/// The local-engine module that lives next to the app instead of inside the installer: detects whether runtimes
/// and models are on disk, fetches them from published archives, and lets the user point at an existing copy.
/// <para>
/// The installer ships only the small server scripts (<c>asr-server.py</c>, <c>tts-server.py</c>); the interpreter,
/// inference packages and models (~3GB) arrive here. An archive contains paths relative to the engines root
/// (<c>tts/…</c> or <c>kws-asr/…</c>), so extraction is a plain unzip into that root.
/// </para>
/// </summary>
public sealed class EnginePackage
{
    public const string TtsId = "tts";
    public const string KwsId = "kws";

    private const int TtsPort = 45102;
    private const int KwsPort = 45103;

    /// <summary>
    /// Published archive locations, in extraction order. An engine may need more than one archive: a GitHub
    /// release asset has to stay under 2 GiB, and the 3 GB TTS runtime plus models do not fit in one file, so
    /// the packer (<c>scripts/pack-engines.ps1</c>) writes several zips that each unpack into the engines root.
    /// Blank URLs mean "not published yet"; <c>PARTNER_ENGINE_TTS_URL</c> / <c>PARTNER_ENGINE_KWS_URL</c> (and
    /// the matching <c>_SHA256</c>) replace the whole list of that engine with one archive, for local testing.
    /// </summary>
    private static readonly (string Url, string Sha256)[] TtsArchives =
    [
        ("", ""),
    ];

    private static readonly (string Url, string Sha256)[] KwsArchives =
    [
        ("", ""),
    ];

    // The interpreter is looked up in order: a relocatable runtime shipped inside the archive wins over a venv,
    // whose pyvenv.cfg holds an absolute path and only works on the machine that built it.
    private static readonly string[] TtsPython = ["tts/python/python.exe", "tts/.venv/Scripts/python.exe"];
    private static readonly string[] KwsPython = ["kws-asr/python/python.exe", "kws-asr/sherpa-onnx/Scripts/python.exe"];

    private static readonly string[] TtsFiles =
    [
        "tts/tts-server.py",
        "tts/model/qwen3_tts_talker.q5_k.gguf",
        "tts/model/qwen3_tts_predictor.q8_0.gguf",
        "tts/model/qwen3_tts_decoder.fp16.onnx",
        "tts/model/tokenizer.json",
    ];

    private static readonly string[] KwsFiles =
    [
        "kws-asr/asr-server.py",
        "kws-asr/models/sensevoice/model.int8.onnx",
        "kws-asr/models/sensevoice/tokens.txt",
        "kws-asr/models/kws/tokens.txt",
    ];

    private readonly string _workspace;
    private readonly IReadOnlyList<EnginePackageSource> _sources;
    private readonly HttpClient _http;
    private readonly object _gate = new();

    private EngineDownloadState _download = new("idle", null, 0, null, null);
    private CancellationTokenSource? _cancel;

    public EnginePackage(
        string appDir,
        string workspace,
        IReadOnlyList<EnginePackageSource>? sources = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        AppDir = Path.GetFullPath(appDir);
        _workspace = workspace;
        _sources = sources ?? DefaultSources();
        // No overall timeout: the archives are gigabytes, and a stalled link is the user's to cancel.
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public string AppDir { get; }

    private string DefaultRoot => Path.Combine(AppDir, "engines");

    private string RootFile => Path.Combine(_workspace, "local-engines-root.txt");

    /// <summary>The folder the engines are read from: the user's choice if there is one, else <c>&lt;app&gt;/engines</c>.</summary>
    public string Root => ReadCustomRoot() ?? DefaultRoot;

    /// <summary>
    /// The directory the app lives in. The shell passes it; without it the API walks up from its own folder to the
    /// first one that has an <c>engines</c> directory (a repo checkout, or the installed layout where the API sits
    /// in <c>api/</c> beside it), and falls back to its own folder.
    /// </summary>
    public static string ResolveAppDir()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("PARTNER_APP_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "engines")))
            {
                return directory.FullName;
            }
        }

        return start.FullName;
    }

    public EnginePackageStatus Status()
    {
        var root = Root;
        var custom = ReadCustomRoot() is not null;
        var tts = Inspect(root, TtsFiles, TtsPython);
        var kws = Inspect(root, KwsFiles, KwsPython);

        EngineDownloadState download;
        lock (_gate)
        {
            download = _download;
        }

        return new EnginePackageStatus(
            root,
            custom,
            tts,
            kws,
            DefaultsFor(root, custom, TtsPython, "tts", "tts-server.py", TtsPort),
            DefaultsFor(root, custom, KwsPython, "kws-asr", "asr-server.py", KwsPort),
            DownloadAvailable(tts, kws),
            download);
    }

    /// <summary>
    /// Points the app at an engines folder the user already has. Null resets to the default location. Returns
    /// false with a reason when the folder holds no complete engine, so a typo never silently replaces a working
    /// location.
    /// </summary>
    public bool TrySetRoot(string? root, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(root))
        {
            if (File.Exists(RootFile))
            {
                File.Delete(RootFile);
            }

            return true;
        }

        var full = Path.GetFullPath(root.Trim());
        if (!Directory.Exists(full))
        {
            error = $"目录不存在：{full}";
            return false;
        }

        var tts = Inspect(full, TtsFiles, TtsPython);
        var kws = Inspect(full, KwsFiles, KwsPython);
        if (!tts.Installed && !kws.Installed)
        {
            error = $"这个目录里没有找到完整的引擎。需要 tts/ 或 kws-asr/ 下的运行时、服务脚本和模型。"
                + $"朗读缺少：{Summarize(tts.Missing)}；识别缺少：{Summarize(kws.Missing)}";
            return false;
        }

        Directory.CreateDirectory(_workspace);
        File.WriteAllText(RootFile, full);
        return true;
    }

    /// <summary>Starts fetching the archives of every engine not yet installed. One download at a time.</summary>
    public bool TryStartDownload(out string error)
    {
        error = string.Empty;

        var root = Root;
        var wanted = _sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .Where(source => !(source.Id == TtsId ? Inspect(root, TtsFiles, TtsPython) : Inspect(root, KwsFiles, KwsPython)).Installed)
            .ToList();

        lock (_gate)
        {
            if (_download.Phase is "downloading" or "extracting")
            {
                error = "已经在下载中";
                return false;
            }

            if (wanted.Count == 0)
            {
                error = _sources.Any(source => !string.IsNullOrWhiteSpace(source.Url))
                    ? "引擎已经齐全，不需要下载"
                    : "还没有配置下载地址。发布版本需要提供引擎包的地址，或设置环境变量 PARTNER_ENGINE_TTS_URL / PARTNER_ENGINE_KWS_URL";
                return false;
            }

            _cancel = new CancellationTokenSource();
            _download = new EngineDownloadState("downloading", null, 0, null, null);
            _ = Task.Run(() => RunDownloadAsync(root, wanted, _cancel.Token));
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
        }
    }

    /// <summary>Resolves a stored path against the app directory; bare command names (looked up on PATH) pass through.</summary>
    public static string? ResolveStored(string appDir, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        return path.IndexOfAny(['/', '\\']) < 0 ? path : Path.GetFullPath(Path.Combine(appDir, path));
    }

    /// <summary>
    /// For a missing <c>&lt;root&gt;/{tts|kws-asr}/python/python.exe</c>, the first existing interpreter among the
    /// venv candidates of that engine; null when the path is not one of the default interpreters or none exists.
    /// </summary>
    public static string? FallbackInterpreter(string missing)
    {
        var folder = Path.GetDirectoryName(Path.GetDirectoryName(missing));
        if (folder is null || !string.Equals(Path.GetFileName(missing), "python.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(missing)), "python", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileName(folder);
        var candidates = name.Equals("tts", StringComparison.OrdinalIgnoreCase) ? TtsPython
            : name.Equals("kws-asr", StringComparison.OrdinalIgnoreCase) ? KwsPython
            : null;
        var parent = Path.GetDirectoryName(folder);
        return candidates is null || parent is null
            ? null
            : candidates.Select(candidate => Path.GetFullPath(Path.Combine(parent, candidate))).FirstOrDefault(File.Exists);
    }

    private bool DownloadAvailable(EngineReadiness tts, EngineReadiness kws) =>
        _sources.Any(source =>
            !string.IsNullOrWhiteSpace(source.Url)
            && !(source.Id == TtsId ? tts : kws).Installed);

    private async Task RunDownloadAsync(string root, IReadOnlyList<EnginePackageSource> sources, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, ".download");
            Directory.CreateDirectory(staging);

            // One engine can arrive as several archives, so the label counts within that engine and the staging
            // file is named after the archive: a shared name would let part 2 resume onto part 1's leftover .part.
            var totals = sources.GroupBy(source => source.Id).ToDictionary(group => group.Key, group => group.Count());
            var seen = new Dictionary<string, int>();

            foreach (var source in sources)
            {
                var engine = source.Id == TtsId ? "朗读引擎" : "唤醒与识别引擎";
                var position = seen[source.Id] = seen.GetValueOrDefault(source.Id) + 1;
                var label = totals[source.Id] > 1 ? $"{engine}（{position}/{totals[source.Id]}）" : engine;
                var archive = Path.Combine(staging, ArchiveName(source));

                await DownloadAsync(source, archive, label, ct).ConfigureAwait(false);

                SetState(new EngineDownloadState("extracting", label, 0, null, null));
                await Task.Run(() => Extract(archive, root, ct), ct).ConfigureAwait(false);
                File.Delete(archive);
            }

            TryDeleteEmpty(staging);
            SetState(new EngineDownloadState("done", null, 0, null, null));
        }
        catch (OperationCanceledException)
        {
            // The .part file stays so the next attempt can resume from it.
            SetState(new EngineDownloadState("cancelled", null, 0, null, null));
        }
        catch (Exception error)
        {
            SetState(new EngineDownloadState("failed", null, 0, null, error.Message));
        }
    }

    private async Task DownloadAsync(EnginePackageSource source, string archive, string label, CancellationToken ct)
    {
        var part = archive + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The part file is longer than (or unrelated to) the remote file; start over.
            File.Delete(part);
            await DownloadAsync(source, archive, label, ct).ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode();

        // 206 appends; a server that ignored the Range header answers 200 with the whole body, replacing it.
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var done = resumed ? have : 0L;
        var total = response.Content.Headers.ContentLength is { } length ? length + done : (long?)null;

        await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var buffer = new byte[256 * 1024];
            var lastReport = Environment.TickCount64;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                if (Environment.TickCount64 - lastReport > 250)
                {
                    SetState(new EngineDownloadState("downloading", label, done, total, null));
                    lastReport = Environment.TickCount64;
                }
            }
        }

        // A truncated body must not look like success: the server's length is the check when it gave one.
        if (total is { } expected && done != expected)
        {
            throw new IOException($"下载不完整：收到 {done} 字节，应为 {expected}。再点一次下载会从断点继续");
        }

        if (!string.IsNullOrWhiteSpace(source.Sha256))
        {
            SetState(new EngineDownloadState("downloading", $"{label} 校验中", done, total, null));
            var actual = await Sha256Async(part, ct).ConfigureAwait(false);
            if (!string.Equals(actual, source.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("下载的文件校验失败（SHA-256 不一致），已删除，请重试");
            }
        }

        File.Move(part, archive, overwrite: true);
    }

    /// <summary>
    /// The staging file name for one archive: the asset name from the URL, so every part keeps its own
    /// <c>.part</c> across retries. Falls back to the engine id for URLs without a usable last segment.
    /// </summary>
    private static string ArchiveName(EnginePackageSource source)
    {
        var name = Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : string.Empty;
        name = string.Concat(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        return name.Length == 0 ? $"{source.Id}.zip" : $"{source.Id}-{name}";
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    /// <summary>Unzips into <paramref name="root"/>, refusing any entry that would land outside it.</summary>
    private static void Extract(string archive, string root, CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.GetFullPath(Path.Combine(rootFull, entry.FullName));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包里有越界路径，已拒绝：{entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private void SetState(EngineDownloadState state)
    {
        lock (_gate)
        {
            _download = state;
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // A leftover empty folder is harmless.
        }
    }

    private string? ReadCustomRoot()
    {
        try
        {
            var text = File.Exists(RootFile) ? File.ReadAllText(RootFile).Trim() : string.Empty;
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static EngineReadiness Inspect(string root, string[] files, string[] pythons)
    {
        var missing = files.Where(file => !File.Exists(Path.Combine(root, file))).ToList();
        if (!pythons.Any(python => File.Exists(Path.Combine(root, python))))
        {
            missing.Insert(0, pythons[0]);
        }

        return new EngineReadiness(missing.Count == 0, missing);
    }

    private EngineDefaults DefaultsFor(string root, bool custom, string[] pythons, string folder, string script, int port)
    {
        var python = pythons.FirstOrDefault(candidate => File.Exists(Path.Combine(root, candidate))) ?? pythons[^1];

        // The default location is stored relative to the app, so moving or reinstalling the app keeps working; a
        // folder the user chose is somewhere else on disk and has to be absolute.
        string Stored(string relative) => custom
            ? Path.GetFullPath(Path.Combine(root, relative))
            : $"engines/{relative}";

        return new EngineDefaults(
            Stored(python),
            [script, "--port", "{port}"],
            Stored(folder),
            port);
    }

    private static string Summarize(IReadOnlyList<string> missing) =>
        missing.Count <= 2 ? string.Join("、", missing) : $"{missing[0]}、{missing[1]} 等 {missing.Count} 项";

    private static IReadOnlyList<EnginePackageSource> DefaultSources() =>
    [
        .. Sources(TtsId, "PARTNER_ENGINE_TTS_URL", "PARTNER_ENGINE_TTS_SHA256", TtsArchives),
        .. Sources(KwsId, "PARTNER_ENGINE_KWS_URL", "PARTNER_ENGINE_KWS_SHA256", KwsArchives),
    ];

    /// <summary>
    /// The published archives of one engine, unless the environment names a single archive to use instead —
    /// that override is what a dev machine uses to test an unpacked build before anything is published.
    /// </summary>
    private static IEnumerable<EnginePackageSource> Sources(
        string id,
        string urlVariable,
        string shaVariable,
        (string Url, string Sha256)[] published)
    {
        if (Environment.GetEnvironmentVariable(urlVariable) is { } overrideUrl && !string.IsNullOrWhiteSpace(overrideUrl))
        {
            yield return new EnginePackageSource(id, overrideUrl, Environment.GetEnvironmentVariable(shaVariable));
            yield break;
        }

        foreach (var (url, sha256) in published)
        {
            yield return new EnginePackageSource(id, url, sha256);
        }
    }
}
