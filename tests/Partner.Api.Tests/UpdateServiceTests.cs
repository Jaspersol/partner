using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Partner.Api.Updates;

namespace Partner.Api.Tests;

/// <summary>
/// The update check and download against a scripted GitHub. Two paths are exercised: the GitHub REST API is the
/// primary (JSON release + per-installer <c>.exe.sha256</c>), and the plain release pages are the fallback
/// (<c>releases/latest</c> redirect + <c>SHA256SUMS.txt</c> + <c>releases/latest/download/&lt;name&gt;</c>).
///
/// <para>
/// The one rule that makes executing a remote installer acceptable is unchanged: whichever path supplies the
/// bytes, they must match the release's SHA-256 before the shell is handed a file.
/// </para>
/// </summary>
public sealed class UpdateServiceTests
{
    private const string Repo = "Jaspersol/partner";
    private const string Tag = "v0.2.0";
    private const string ApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string LatestUrl = $"https://github.com/{Repo}/releases/latest";
    private const string InstallerName = "Partner_0.2.0_x64-setup.exe";
    private const string BrowserUrl = $"https://github.com/{Repo}/releases/download/{Tag}/{InstallerName}";
    private const string ApiAssetUrl = $"https://api.github.com/repos/{Repo}/releases/assets/42";
    private const string ChecksumUrl = $"https://github.com/{Repo}/releases/download/{Tag}/{InstallerName}.sha256";
    private const string SumsUrl = $"https://github.com/{Repo}/releases/latest/download/SHA256SUMS.txt";
    private const string DirectUrl = $"https://github.com/{Repo}/releases/latest/download/{InstallerName}";

    // The download checks the MZ header, so the scripted installer starts like a real Windows executable.
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("MZ installer bytes");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string[] ValidSums() => [$"{Sha(Payload)}  {InstallerName}"];

    private sealed record Call(string Method, string Url, string? Accept);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        public IEnumerable<string> Requests => Calls.Select(call => call.Url);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var accept = request.Headers.Accept.ToString();
            Calls.Add(new Call(
                request.Method.Method,
                request.RequestUri!.ToString(),
                accept.Length > 0 ? accept : null));
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Everything a test can change about the scripted release.</summary>
    private sealed class Scenario
    {
        public string Tag { get; set; } = UpdateServiceTests.Tag;
        public byte[] Payload { get; set; } = UpdateServiceTests.Payload;
        public string? Sha { get; set; }
        public string? ApiBody { get; set; }
        public bool ApiOffline { get; set; }
        public bool ApiRateLimited { get; set; }
        public bool ApiRetryAfter { get; set; }
        public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;
        public bool DirectOffline { get; set; }
        public HttpStatusCode DirectStatus { get; set; } = HttpStatusCode.Found;
        public bool NoChecksumAsset { get; set; }
        public bool NoSums { get; set; }
        public string? Sums { get; set; }
        public bool ForeignAssetUrl { get; set; }
        public HashSet<string> FailingDownloads { get; } = [];
    }

    private static UpdateService Create(StubHandler handler, out string root, TimeProvider? clock = null)
    {
        root = Path.Combine(Path.GetTempPath(), "partner-update-tests", Guid.NewGuid().ToString("N"));
        return new UpdateService(new HttpClient(handler), root, _ => { }, clock);
    }

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ApiJson(Scenario scenario)
    {
        var installerUrl = scenario.ForeignAssetUrl ? "https://evil.example.com/Partner.exe" : BrowserUrl;
        var assets = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["name"] = InstallerName,
                ["size"] = scenario.Payload.Length,
                ["browser_download_url"] = installerUrl,
                ["url"] = ApiAssetUrl,
            },
        };
        if (!scenario.NoChecksumAsset)
        {
            assets.Add(new Dictionary<string, object?>
            {
                ["name"] = InstallerName + ".sha256",
                ["size"] = 65,
                ["browser_download_url"] = ChecksumUrl,
                ["url"] = ApiAssetUrl + "1",
            });
        }

        assets.Add(new Dictionary<string, object?>
        {
            ["name"] = "SHA256SUMS.txt",
            ["size"] = 80,
            ["browser_download_url"] = SumsUrl,
            ["url"] = ApiAssetUrl + "2",
        });

        return JsonSerializer.Serialize(new
        {
            tag_name = scenario.Tag,
            name = $"Partner {scenario.Tag}",
            html_url = $"https://github.com/{Repo}/releases/tag/{scenario.Tag}",
            assets,
        });
    }

    /// <summary>The API's release JSON, the per-asset checksum, the release pages and the asset downloads.</summary>
    private static HttpResponseMessage Answer(HttpRequestMessage request, Scenario scenario)
    {
        var url = request.RequestUri!.ToString();

        if (url == ApiUrl)
        {
            if (scenario.ApiOffline)
            {
                throw new HttpRequestException("offline");
            }

            if (scenario.ApiRateLimited)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                limited.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
                return limited;
            }

            if (scenario.ApiRetryAfter)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return limited;
            }

            return scenario.ApiStatus == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, scenario.ApiBody ?? ApiJson(scenario))
                : Json(scenario.ApiStatus, "{\"message\": \"failure\"}");
        }

        if (url == LatestUrl)
        {
            if (scenario.DirectOffline)
            {
                throw new HttpRequestException("offline");
            }

            if (scenario.DirectStatus != HttpStatusCode.Found)
            {
                return new HttpResponseMessage(scenario.DirectStatus);
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://github.com/{Repo}/releases/tag/{scenario.Tag}");
            return redirect;
        }

        if (url == ChecksumUrl && !scenario.NoChecksumAsset)
        {
            return Text($"{scenario.Sha ?? Sha(scenario.Payload)} *{InstallerName}\n");
        }

        if (url == SumsUrl && !scenario.NoSums)
        {
            return Text(scenario.Sums ?? string.Join('\n', ValidSums()));
        }

        var isInstaller = url is BrowserUrl or ApiAssetUrl or DirectUrl;
        if (isInstaller && request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            head.Content.Headers.ContentLength = scenario.Payload.Length;
            return head;
        }

        if (isInstaller && !scenario.FailingDownloads.Contains(url))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(scenario.Payload) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static StubHandler Handler(Scenario scenario) =>
        new(request => Answer(request, scenario));

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Tests that download through a service share this tail: wait for the background task to settle.</summary>
    private static async Task<UpdateDownloadState> WaitForDownloadAsync(UpdateService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var state = service.Download();
            if (state.Phase is "ready" or "failed" or "cancelled")
            {
                return state;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"download stayed in '{service.Download().Phase}'");
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("v1.9.9", "1.10.0")]
    [InlineData("1.0.0-beta.2", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0+local", "1.0.1")]
    public void Versions_order_the_way_tags_do(string older, string newer)
    {
        Assert.True(AppVersion.TryParse(older, out var left));
        Assert.True(AppVersion.TryParse(newer, out var right));
        Assert.True(left.CompareTo(right) < 0);
        Assert.True(right.CompareTo(left) > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.x")]
    [InlineData("latest")]
    public void Malformed_versions_are_rejected_rather_than_ordered(string text) =>
        Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public async Task The_api_is_the_primary_path_and_pins_its_own_checksum()
    {
        var handler = Handler(new Scenario());
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("api", check.Source);
            Assert.Equal("0.1.0", check.Current);
            Assert.Equal("0.2.0", check.Latest);
            Assert.Equal(InstallerName, check.InstallerName);
            Assert.Equal((long)Payload.Length, check.SizeBytes);
            Assert.Contains(ApiUrl, handler.Requests);
            Assert.Contains(ChecksumUrl, handler.Requests);
            // The primary path never needs the release pages or the fallback manifest.
            Assert.DoesNotContain(LatestUrl, handler.Requests);
            Assert.DoesNotContain(SumsUrl, handler.Requests);
            Assert.Contains(handler.Calls, call => call.Url == ApiUrl && call.Accept == "application/vnd.github+json");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Being_up_to_date_needs_no_checksum_fetch_on_either_path()
    {
        var handler = Handler(new Scenario { Tag = "v0.1.0" });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.False(check.Available);
            Assert.Equal("0.1.0", check.Latest);
            Assert.Equal("api", check.Source);
            Assert.DoesNotContain(ChecksumUrl, handler.Requests);
            Assert.DoesNotContain(SumsUrl, handler.Requests);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_rate_limited_api_falls_back_to_the_direct_link()
    {
        var handler = Handler(new Scenario { ApiRateLimited = true });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("direct", check.Source);
            Assert.Equal(InstallerName, check.InstallerName);
            Assert.Contains(LatestUrl, handler.Requests);
            Assert.Contains(SumsUrl, handler.Requests);
            // The fallback itself never spends another API call.
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_429_from_the_api_is_rate_limited_and_falls_back()
    {
        var handler = Handler(new Scenario { ApiStatus = HttpStatusCode.TooManyRequests });
        var clock = new FakeClock();
        var service = Create(handler, out var root, clock);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("direct", check.Source);
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));

            // A 429 is a spent quota, not a transient HTTP error: keep the direct path for the full
            // cooldown instead of retrying the API every two minutes.
            clock.Advance(TimeSpan.FromMinutes(3));
            var again = await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.Equal("direct", again.Source);
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_403_with_retry_after_is_rate_limited()
    {
        var handler = Handler(new Scenario { ApiRetryAfter = true });
        var clock = new FakeClock();
        var service = Create(handler, out var root, clock);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.Null(check.Error);
            Assert.Equal("direct", check.Source);
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));

            clock.Advance(TimeSpan.FromMinutes(3));
            await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task An_offline_api_falls_back_to_the_direct_link()
    {
        var handler = Handler(new Scenario { ApiOffline = true });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("direct", check.Source);
            Assert.Equal((long)Payload.Length, check.SizeBytes);
            Assert.Contains(DirectUrl, handler.Requests); // the HEAD that measures the installer
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task An_api_release_without_its_checksum_asset_uses_the_fallback_manifest()
    {
        var handler = Handler(new Scenario { NoChecksumAsset = true });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("direct", check.Source);
            Assert.Contains(ApiUrl, handler.Requests);
            Assert.Contains(SumsUrl, handler.Requests);
            Assert.DoesNotContain(LatestUrl, handler.Requests); // the version still came from the API
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData(false)] // quota exhausted
    [InlineData(true)]  // api.github.com unreachable
    public async Task A_failed_api_is_not_retried_on_every_check_while_it_is_cooling_down(bool offline)
    {
        var handler = Handler(offline ? new Scenario { ApiOffline = true } : new Scenario { ApiRateLimited = true });
        var clock = new FakeClock();
        var service = Create(handler, out var root, clock);

        try
        {
            var first = await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.Equal("direct", first.Source);
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));

            clock.Advance(TimeSpan.FromMinutes(3)); // past the 2-minute check cache, inside the 15-minute rate-limit cooldown
            var second = await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.Equal("direct", second.Source);
            Assert.Equal(1, handler.Calls.Count(call => call.Url == ApiUrl));

            clock.Advance(TimeSpan.FromMinutes(13)); // the primary path is tried again
            var third = await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.Equal("direct", third.Source);
            Assert.Equal(2, handler.Calls.Count(call => call.Url == ApiUrl));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_release_without_any_checksum_file_is_not_offered()
    {
        var handler = Handler(new Scenario { ApiOffline = true, NoSums = true });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.False(check.Available);
            Assert.NotNull(check.Error);
            Assert.Contains("SHA256SUMS.txt", check.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_manifest_line_with_a_path_is_never_used_as_a_file_name()
    {
        var handler = Handler(new Scenario
        {
            ApiOffline = true,
            Sums = $"{Sha(Payload)}  ../../evil.exe",
        });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.False(check.Available);
            Assert.NotNull(check.Error);
            Assert.Contains("SHA256SUMS.txt", check.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_foreign_asset_url_in_the_api_json_is_never_downloaded()
    {
        var handler = Handler(new Scenario { ForeignAssetUrl = true });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("direct", check.Source);
            Assert.DoesNotContain(handler.Requests, url => url.StartsWith("https://evil.example.com", StringComparison.Ordinal));
            Assert.All(handler.Requests, url => Assert.True(
                url.StartsWith("https://github.com/", StringComparison.Ordinal)
                || url.StartsWith("https://api.github.com/", StringComparison.Ordinal),
                url));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Both_paths_failing_is_reported_in_the_answer()
    {
        var handler = Handler(new Scenario { ApiOffline = true, DirectOffline = true });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.False(check.Available);
            Assert.Null(check.Latest);
            Assert.Contains("无法连接更新服务器", check.Error);
            Assert.Contains("API 路径", check.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_403_from_every_host_is_not_reported_as_unreachable()
    {
        var handler = Handler(new Scenario { ApiStatus = HttpStatusCode.Forbidden, DirectStatus = HttpStatusCode.Forbidden });
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.False(check.Available);
            Assert.NotNull(check.Error);
            Assert.Contains("403", check.Error);
            Assert.DoesNotContain("无法连接", check.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Download_uses_the_api_asset_first_and_verifies_the_checksum()
    {
        var handler = Handler(new Scenario());
        var service = Create(handler, out var root);
        handler.Calls.Clear();

        try
        {
            await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.True(service.TryStartDownload(out var error), error);

            var state = await WaitForDownloadAsync(service);

            Assert.Equal("ready", state.Phase);
            Assert.NotNull(state.Path);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(state.Path!));
            Assert.Null(state.Error);
            Assert.Contains(handler.Calls, call => call.Url == BrowserUrl && call.Method == "GET");
            Assert.DoesNotContain(handler.Calls, call => call.Url == DirectUrl && call.Method == "GET");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Download_falls_back_to_the_direct_link_when_the_api_candidates_fail()
    {
        var scenario = new Scenario();
        scenario.FailingDownloads.Add(BrowserUrl);
        scenario.FailingDownloads.Add(ApiAssetUrl);
        var handler = Handler(scenario);
        var service = Create(handler, out var root);
        handler.Calls.Clear();

        try
        {
            await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.True(service.TryStartDownload(out var error), error);

            var state = await WaitForDownloadAsync(service);

            Assert.Equal("ready", state.Phase);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(state.Path!));
            Assert.Contains(handler.Calls, call => call.Url == DirectUrl && call.Method == "GET");
            // The API asset endpoint must ask for bytes rather than JSON metadata.
            Assert.Contains(handler.Calls, call =>
                call.Url == ApiAssetUrl && call.Accept == "application/octet-stream");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_the_download_and_removes_the_file()
    {
        var wrong = new string('0', 64);
        var handler = Handler(new Scenario { ApiOffline = true, Sums = $"{wrong}  {InstallerName}" });
        var service = Create(handler, out var root);

        try
        {
            await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.True(service.TryStartDownload(out var error), error);

            var state = await WaitForDownloadAsync(service);

            Assert.Equal("failed", state.Phase);
            Assert.Contains("校验失败", state.Error);
            Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_download_that_is_not_a_windows_executable_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes("not a windows executable");
        var handler = Handler(new Scenario
        {
            ApiOffline = true,
            Payload = bytes,
            Sha = Sha(bytes), // the digest matches; only the MZ header is wrong
        });
        var service = Create(handler, out var root);

        try
        {
            await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.True(service.TryStartDownload(out var error), error);

            var state = await WaitForDownloadAsync(service);

            Assert.Equal("failed", state.Phase);
            Assert.Contains("Windows 安装程序", state.Error);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
