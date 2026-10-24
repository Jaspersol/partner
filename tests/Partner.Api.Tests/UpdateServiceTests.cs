using System.Net;
using System.Security.Cryptography;
using System.Text;
using Partner.Api.Updates;

namespace Partner.Api.Tests;

/// <summary>
/// The update check and download against a scripted GitHub: what the settings page asks for, and the one rule
/// that makes executing a remote installer acceptable — the file must match the release's SHA-256.
///
/// <para>
/// The check must never touch <c>api.github.com</c>: that host allows 60 calls an hour per address and answers
/// 403 rate limit exceeded once a shared connection has spent them, which is what the release pages avoid.
/// </para>
/// </summary>
public sealed class UpdateServiceTests
{
    private const string Repo = "Jaspersol/partner";
    private const string LatestUrl = $"https://github.com/{Repo}/releases/latest";
    private const string InstallerName = "Partner_0.2.0_x64-setup.exe";
    private const string InstallerUrl = $"https://github.com/{Repo}/releases/latest/download/{InstallerName}";
    private const string SumsUrl = $"https://github.com/{Repo}/releases/latest/download/SHA256SUMS.txt";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("installer bytes");

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static UpdateService Create(HttpMessageHandler handler, out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "partner-update-tests", Guid.NewGuid().ToString("N"));
        return new UpdateService(new HttpClient(handler), root);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// The release pages: <c>releases/latest</c> redirects to <c>releases/tag/&lt;tag&gt;</c>, and
    /// <c>latest/download/&lt;asset&gt;</c> serves the asset. <paramref name="sums"/> of <c>null</c> means the
    /// release has no checksum file.
    /// </summary>
    private static HttpResponseMessage Answer(HttpRequestMessage request, string tag, string? sums, bool fail)
    {
        if (fail)
        {
            throw new HttpRequestException("offline");
        }

        var url = request.RequestUri!.ToString();
        if (url == LatestUrl)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://github.com/{Repo}/releases/tag/{tag}");
            return redirect;
        }

        if (url == SumsUrl)
        {
            return sums is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) };
        }

        if (url == InstallerUrl && request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            head.Content.Headers.ContentLength = Payload.Length;
            return head;
        }

        if (url == InstallerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string[] ValidSums() => [$"{Sha(Payload)}  {InstallerName}"];

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
    public async Task A_newer_release_reports_the_installer_and_its_size()
    {
        var handler = new StubHandler(request => Answer(request, "v0.2.0", string.Join('\n', ValidSums()), fail: false));
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.Equal("0.1.0", check.Current);
            Assert.Equal("0.2.0", check.Latest);
            Assert.Equal(InstallerName, check.InstallerName);
            Assert.Equal((long)Payload.Length, check.SizeBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Being_up_to_date_needs_no_checksum_fetch()
    {
        var handler = new StubHandler(request => Answer(request, "v0.1.0", string.Join('\n', ValidSums()), fail: false));
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.False(check.Available);
            Assert.Equal("0.1.0", check.Latest);
            Assert.DoesNotContain(SumsUrl, handler.Requests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_release_without_a_checksum_file_is_not_offered()
    {
        var handler = new StubHandler(request => Answer(request, "v0.2.0", sums: null, fail: false));
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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task The_check_never_touches_the_rate_limited_api()
    {
        var handler = new StubHandler(request => Answer(request, "v0.2.0", string.Join('\n', ValidSums()), fail: false));
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.Null(check.Error);
            Assert.True(check.Available);
            Assert.NotEmpty(handler.Requests);
            Assert.All(handler.Requests, url => Assert.DoesNotContain("api.github.com", url));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_403_from_the_update_host_is_not_reported_as_unreachable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("rate limit exceeded"),
        });
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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task An_unreachable_release_feed_is_reported_in_the_answer()
    {
        var handler = new StubHandler(request => Answer(request, "v0.2.0", "", fail: true));
        var service = Create(handler, out var root);

        try
        {
            var check = await service.CheckAsync("0.1.0", CancellationToken.None);

            Assert.False(check.Available);
            Assert.Null(check.Latest);
            Assert.Contains("无法连接更新服务器", check.Error);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Download_verifies_the_checksum_and_leaves_a_ready_file()
    {
        var handler = new StubHandler(request => Answer(request, "v0.2.0", string.Join('\n', ValidSums()), fail: false));
        var service = Create(handler, out var root);
        handler.Requests.Clear();

        try
        {
            await service.CheckAsync("0.1.0", CancellationToken.None);
            Assert.True(service.TryStartDownload(out var error), error);

            var state = await WaitForDownloadAsync(service);

            Assert.Equal("ready", state.Phase);
            Assert.NotNull(state.Path);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(state.Path!));
            Assert.Null(state.Error);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_the_download_and_removes_the_file()
    {
        var wrong = new string('0', 64);
        var handler = new StubHandler(request => Answer(request, "v0.2.0", $"{wrong}  {InstallerName}", fail: false));
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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
}
