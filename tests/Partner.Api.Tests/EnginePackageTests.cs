using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Partner.Api.Settings;
using Partner.Api.Voice;

namespace Partner.Api.Tests;

/// <summary>
/// The local-engine module: detection of what is on disk, the download → verify → unzip pipeline, and the
/// relative-path resolution that lets settings survive the app being moved. The archive is served by an
/// in-process handler, so nothing here touches the network.
/// </summary>
public sealed class EnginePackageTests : IDisposable
{
    private static readonly string[] TtsFiles =
    [
        "tts/python/python.exe",
        "tts/tts-server.py",
        "tts/model/qwen3_tts_talker.q5_k.gguf",
        "tts/model/qwen3_tts_predictor.q8_0.gguf",
        "tts/model/qwen3_tts_decoder.fp16.onnx",
        "tts/model/tokenizer.json",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"partner-engines-{Guid.NewGuid():N}");

    public EnginePackageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the temp folder is not worth failing a test over.
        }
    }

    private string AppDir => Path.Combine(_dir, "app");

    private string Workspace => Path.Combine(_dir, "workspace");

    private static byte[] Zip(IEnumerable<string> entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        }

        return stream.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private EnginePackage Package(byte[]? archive, string? sha = null, bool supportRange = false) =>
        new(AppDir, Workspace, [new EnginePackageSource(EnginePackage.TtsId, archive is null ? null : "http://engines.test/tts.zip", sha)],
            new ArchiveHandler(archive ?? [], supportRange));

    private static async Task<EngineDownloadState> WaitForAsync(EnginePackage package)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var state = package.Status().Download;
            if (state.Phase is "done" or "failed" or "cancelled")
            {
                return state;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("the download never finished");
    }

    [Fact]
    public void An_empty_app_directory_has_no_engines_and_names_what_is_missing()
    {
        var status = Package(null).Status();

        Assert.False(status.Tts.Installed);
        Assert.False(status.Kws.Installed);
        Assert.Contains("tts/python/python.exe", status.Tts.Missing);
        Assert.False(status.DownloadAvailable);
        Assert.Equal(Path.Combine(Path.GetFullPath(AppDir), "engines"), status.Root);
    }

    [Fact]
    public async Task Downloading_unpacks_the_engine_and_the_defaults_are_relative_to_the_app()
    {
        var archive = Zip(TtsFiles);
        var package = Package(archive, Sha(archive));

        Assert.True(package.TryStartDownload(out var error), error);
        var state = await WaitForAsync(package);

        Assert.Equal("done", state.Phase);
        var status = package.Status();
        Assert.True(status.Tts.Installed, string.Join(",", status.Tts.Missing));
        Assert.False(status.Kws.Installed);
        Assert.Equal("engines/tts/python/python.exe", status.TtsDefaults.Command);
        Assert.Equal("engines/tts", status.TtsDefaults.WorkingDirectory);
        Assert.Equal(["tts-server.py", "--port", "{port}"], status.TtsDefaults.Arguments);
        Assert.False(Directory.Exists(Path.Combine(AppDir, "engines", ".download")), "the staging folder should be cleaned up");
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_the_download_and_unpacks_nothing()
    {
        var package = Package(Zip(TtsFiles), sha: new string('0', 64));

        Assert.True(package.TryStartDownload(out _));
        var state = await WaitForAsync(package);

        Assert.Equal("failed", state.Phase);
        Assert.Contains("校验", state.Error);
        Assert.False(package.Status().Tts.Installed);
        Assert.False(File.Exists(Path.Combine(AppDir, "engines", "tts", "tts-server.py")));
    }

    [Fact]
    public async Task An_archive_entry_that_escapes_the_root_is_refused()
    {
        var package = Package(Zip(["tts/tts-server.py", "../evil.txt"]));

        Assert.True(package.TryStartDownload(out _));
        var state = await WaitForAsync(package);

        Assert.Equal("failed", state.Phase);
        Assert.False(File.Exists(Path.Combine(AppDir, "evil.txt")));
    }

    [Fact]
    public async Task A_partial_file_is_resumed_with_a_range_request()
    {
        var archive = Zip(TtsFiles);
        var staging = Path.Combine(AppDir, "engines", ".download");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "tts.zip.part"), archive[..10]);

        var package = Package(archive, Sha(archive), supportRange: true);
        Assert.True(package.TryStartDownload(out _));
        var state = await WaitForAsync(package);

        Assert.Equal("done", state.Phase);
        Assert.True(package.Status().Tts.Installed);
    }

    [Fact]
    public void Download_is_refused_with_a_reason_when_no_address_is_published()
    {
        Assert.False(Package(null).TryStartDownload(out var error));
        Assert.Contains("下载地址", error);
    }

    [Fact]
    public async Task Download_is_refused_when_the_engine_is_already_there()
    {
        var package = Package(Zip(TtsFiles));
        package.TryStartDownload(out _);
        await WaitForAsync(package);

        Assert.False(package.TryStartDownload(out var error));
        Assert.Contains("齐全", error);
    }

    [Fact]
    public async Task A_folder_the_user_points_at_is_checked_and_used_with_absolute_defaults()
    {
        var elsewhere = Path.Combine(_dir, "mine");
        foreach (var file in TtsFiles)
        {
            var path = Path.Combine(elsewhere, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "x");
        }

        var package = Package(null);
        Assert.False(package.TrySetRoot(Path.Combine(_dir, "nothing-here"), out var missing));
        Assert.Contains("不存在", missing);

        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        Assert.False(package.TrySetRoot(Path.Combine(_dir, "empty"), out var incomplete));
        Assert.Contains("没有找到完整的引擎", incomplete);

        Assert.True(package.TrySetRoot(elsewhere, out var error), error);
        var status = package.Status();
        Assert.True(status.CustomRoot);
        Assert.True(status.Tts.Installed);
        Assert.Equal(Path.GetFullPath(Path.Combine(elsewhere, "tts", "python", "python.exe")), status.TtsDefaults.Command);

        Assert.True(package.TrySetRoot(null, out _));
        Assert.False(package.Status().CustomRoot);
    }

    [Fact]
    public void Settings_ship_with_the_engine_paths_and_the_quiet_level_filled_in()
    {
        var local = new LocalSettings();

        Assert.Equal("engines/tts/python/python.exe", local.Tts.Command);
        Assert.Equal(["tts-server.py", "--port", "{port}"], local.Tts.Arguments);
        Assert.Equal("engines/tts", local.Tts.WorkingDirectory);
        Assert.Equal("engines/kws-asr/python/python.exe", local.Kws.Command);
        Assert.Equal("engines/kws-asr", local.Kws.WorkingDirectory);
        Assert.False(local.Tts.Enabled, "the paths are defaults, not a reason to start a 3GB engine unasked");
        Assert.Equal(0.003, new InputSettings().SilenceThreshold);
    }

    [Fact]
    public void Stored_paths_resolve_against_the_app_but_bare_commands_and_absolute_paths_do_not()
    {
        var app = Path.GetFullPath(AppDir);

        Assert.Equal(Path.Combine(app, "engines", "tts"), EnginePackage.ResolveStored(app, "engines/tts"));
        Assert.Equal("python", EnginePackage.ResolveStored(app, "python"));
        Assert.Equal(@"C:\tools\python.exe", EnginePackage.ResolveStored(app, @"C:\tools\python.exe"));
        Assert.Null(EnginePackage.ResolveStored(app, null));
    }

    [Fact]
    public async Task One_engine_can_arrive_as_several_archives_and_each_is_unpacked_into_the_engines_root()
    {
        var runtime = Zip(["tts/python/python.exe", "tts/tts-server.py"]);
        var talker = Zip(["tts/model/qwen3_tts_talker.q5_k.gguf", "tts/model/qwen3_tts_predictor.q8_0.gguf"]);
        var rest = Zip(["tts/model/qwen3_tts_decoder.fp16.onnx", "tts/model/tokenizer.json"]);

        var package = new EnginePackage(
            AppDir,
            Workspace,
            [
                new(EnginePackage.TtsId, "http://engines.test/tts-a-runtime.zip", Sha(runtime)),
                new(EnginePackage.TtsId, "http://engines.test/tts-b-talker.zip", Sha(talker)),
                new(EnginePackage.TtsId, "http://engines.test/tts-c-model.zip", Sha(rest)),
            ],
            new ArchiveSetHandler(new Dictionary<string, byte[]>
            {
                ["tts-a-runtime.zip"] = runtime,
                ["tts-b-talker.zip"] = talker,
                ["tts-c-model.zip"] = rest,
            }));

        Assert.True(package.TryStartDownload(out var error), error);
        var state = await WaitForAsync(package);

        Assert.Equal("done", state.Phase);
        var status = package.Status();
        Assert.True(status.Tts.Installed, string.Join(",", status.Tts.Missing));
        Assert.False(status.Kws.Installed);
        Assert.False(Directory.Exists(Path.Combine(AppDir, "engines", ".download")), "the staging folder should be cleaned up");
    }

    [Fact]
    public async Task A_dropped_connection_resumes_from_the_bytes_already_on_disk()
    {
        var runtime = Zip(["tts/python/python.exe", "tts/tts-server.py"]);
        var handler = new DroppingHandler(runtime);

        var package = new EnginePackage(
            AppDir,
            Workspace,
            [new(EnginePackage.TtsId, "http://engines.test/tts-a-runtime.zip", Sha(runtime))],
            handler);

        Assert.True(package.TryStartDownload(out var error), error);
        var state = await WaitForAsync(package);

        Assert.Equal("done", state.Phase);
        Assert.Equal(2, handler.Requests);
        Assert.True(handler.ResumedFrom > 0, "the second request should have asked for the rest of the file");
        Assert.True(File.Exists(Path.Combine(AppDir, "engines", "tts", "python", "python.exe")));
    }

    [Fact]
    public async Task A_failed_part_keeps_the_parts_before_it_and_a_retry_starts_over_cleanly()
    {
        var runtime = Zip(["tts/python/python.exe"]);
        var missing = Zip(["tts/tts-server.py"]);

        var package = new EnginePackage(
            AppDir,
            Workspace,
            [
                new(EnginePackage.TtsId, "http://engines.test/tts-a-runtime.zip", Sha(runtime)),
                new(EnginePackage.TtsId, "http://engines.test/tts-b-missing.zip", Sha(missing)),
            ],
            new ArchiveSetHandler(new Dictionary<string, byte[]> { ["tts-a-runtime.zip"] = runtime }));

        Assert.True(package.TryStartDownload(out var error), error);
        var state = await WaitForAsync(package);

        Assert.Equal("failed", state.Phase);
        // The part that did arrive stays unpacked; the readiness check is what reports the engine as incomplete.
        Assert.True(File.Exists(Path.Combine(AppDir, "engines", "tts", "python", "python.exe")));
        Assert.False(package.Status().Tts.Installed);
    }

    [Fact]
    public void A_missing_packaged_interpreter_falls_back_to_the_venv_that_is_there()
    {
        var app = Path.GetFullPath(AppDir);
        var missing = Path.Combine(app, "engines", "kws-asr", "python", "python.exe");
        Assert.Null(EnginePackage.FallbackInterpreter(missing));

        var venv = Path.Combine(app, "engines", "kws-asr", "sherpa-onnx", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(venv)!);
        File.WriteAllText(venv, "");

        Assert.Equal(venv, EnginePackage.FallbackInterpreter(missing));
        Assert.Null(EnginePackage.FallbackInterpreter(Path.Combine(app, "elsewhere", "python", "python.exe")));
    }

    [Fact]
    public void Speech_clients_default_to_the_local_engines()
    {
        Assert.Equal("http://127.0.0.1:45103", new AsrSettings().BaseUrl);
        Assert.Equal("sensevoice", new AsrSettings().Protocol);
        Assert.Equal("http://127.0.0.1:45102", new TtsSettings().BaseUrl);
        Assert.Equal("local", new TtsSettings().Protocol);
    }

    [Fact]
    public async Task The_host_starts_an_engine_from_a_path_relative_to_the_app()
    {
        var run = Path.Combine(AppDir, "engines", "tts");
        Directory.CreateDirectory(run);
        await using var host = new LocalEngineHost(AppDir);

        var state = await host.ReconfigureAsync(new LocalSettings
        {
            Tts = new LocalEngineSettings
            {
                Enabled = true,
                Command = "cmd",
                Arguments = ["/c", "echo here>where.txt & ping -n 60 127.0.0.1 >nul"],
                WorkingDirectory = "engines/tts",
                Port = ApiFixture.FreePort(),
                ReadyTimeoutSeconds = 3,
            },
        });

        Assert.True(state.Tts.Running, state.Tts.Reason);
        Assert.True(await WaitForFileAsync(Path.Combine(run, "where.txt")), "the engine should have run inside <app>/engines/tts");
    }

    private static async Task<bool> WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>Serves one archive, optionally honouring <c>Range: bytes=N-</c> the way a CDN does.</summary>
    private sealed class ArchiveHandler(byte[] archive, bool supportRange) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (supportRange && from is { } start)
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(archive[(int)start..]) };
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }

    private sealed class DroppingHandler(byte[] archive) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public long ResumedFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (from is { } start)
            {
                ResumedFrom = start;
                var rest = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(archive[(int)start..]) };
                rest.Content.Headers.ContentLength = archive.Length - start;
                return Task.FromResult(rest);
            }

            var truncated = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(archive, 0, archive.Length / 2)),
            };
            truncated.Content.Headers.ContentLength = archive.Length;
            return Task.FromResult(truncated);
        }
    }

    /// <summary>Serves a different archive per file name, and 404s anything not in the set.</summary>
    private sealed class ArchiveSetHandler(Dictionary<string, byte[]> archives) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return Task.FromResult(archives.TryGetValue(name, out var archive)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
