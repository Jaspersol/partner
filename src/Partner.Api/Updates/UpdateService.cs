using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Partner.Api.Updates;

/// <summary>
/// What the settings page shows after a check. <see cref="Error"/> is set instead of the promise failing:
/// "the check did not happen" is an answer the page can show beside the version, not an exceptional state.
/// <see cref="Source"/> names the path that answered — <c>api</c> for the GitHub REST API, <c>direct</c> for the
/// no-API release-page fallback — so a check that had to fall back is visible instead of silent.
/// </summary>
public sealed record UpdateCheck(
    string Current,
    string? Latest,
    bool Available,
    string? InstallerName,
    long? SizeBytes,
    string? Error,
    string? Source = null);

/// <summary>One installer download. <see cref="Path"/> is set only when the phase is <c>ready</c>.</summary>
public sealed record UpdateDownloadState(
    string Phase,
    long BytesDone,
    long? BytesTotal,
    string? Path,
    string? Error);

/// <summary>
/// Checks the project's GitHub releases for a newer installer and downloads it into the temp folder.
///
/// <para>
/// Two paths answer the same question. The <b>primary</b> is the GitHub REST API
/// (<c>api.github.com/repos/…/releases/latest</c>): one JSON document gives the tag, the release notes, the
/// installer's name/size/URLs and the address of its <c>.exe.sha256</c> checksum. The <b>fallback</b> is the
/// plain release pages: <c>releases/latest</c> redirects to <c>releases/tag/&lt;tag&gt;</c> and
/// <c>releases/latest/download/&lt;asset&gt;</c> serves the asset itself. Those cost none of the API's 60 calls
/// an hour per address, so a shared egress IP that has spent the quota cannot stop the check — the API path
/// fails, the direct path takes over. Both the manual check and the installer resolution use this pair, and a
/// failure anywhere in the API path (including a missing per-asset checksum) falls back rather than giving up.
/// </para>
///
/// <para>
/// The download uses the same two paths as ordered candidates: the release's <c>browser_download_url</c>, its
/// API asset URL, then the direct <c>releases/download/&lt;tag&gt;/</c> and <c>releases/latest/download/</c>
/// URLs. Whichever answers, the bytes are streamed under a size cap, checked for the <c>MZ</c> header and
/// verified against the release's SHA-256 before they are offered to the shell: the shell will execute the
/// file, so a truncated, swapped or foreign file must not reach that point. Candidate URLs and redirect targets
/// are restricted to GitHub hosts, and the file name is sanitised before it is used as a path.
/// </para>
///
/// <para>
/// The shell owns starting the installer and quitting; this service only hands it a verified path. That split is
/// what keeps the API free of window/process lifetime decisions and the shell free of HTTP.
/// </para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>The repository whose releases the app ships from. A fork changes this one constant.</summary>
    public const string Repository = "Jaspersol/partner";

    private const string ApiReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string LatestReleaseUrl = $"https://github.com/{Repository}/releases/latest";
    private const string LatestAssetUrl = $"https://github.com/{Repository}/releases/latest/download/";
    private const string ChecksumsAsset = "SHA256SUMS.txt";
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan CheckTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ApiCooldown = TimeSpan.FromMinutes(15);

    // Asset downloads are redirected by GitHub to one of these hosts. Redirects are re-checked at response
    // time, so a GitHub URL cannot hand the installer to a host outside this list.
    private static readonly string[] DownloadHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "githubusercontent.com",
    ];

    private readonly HttpClient _http;
    private readonly string _downloadRoot;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private LatestRelease? _latest;
    private DateTimeOffset _checkedAt;
    private DateTimeOffset _apiRetryAfter;
    private UpdateDownloadState _download = new("idle", 0, null, null, null);
    private CancellationTokenSource? _cancel;

    /// <param name="http">Injected by tests; production uses a client with no timeout, since a download is long.</param>
    /// <param name="downloadRoot">Injected by tests; production uses the per-user temp folder.</param>
    /// <param name="log">Injected by tests; production logs the path taken to stderr.</param>
    /// <param name="clock">Injected by tests; production uses the system clock for caches and the API cooldown.</param>
    public UpdateService(
        HttpClient? http = null,
        string? downloadRoot = null,
        Action<string>? log = null,
        TimeProvider? clock = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Partner");
        _downloadRoot = downloadRoot ?? Path.Combine(Path.GetTempPath(), "partner-update");
        _log = log ?? (message => Console.Error.WriteLine($"[update] {message}"));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Compares <paramref name="current"/> with the newest published release. Throws only for a malformed
    /// <paramref name="current"/>; everything the network can do wrong comes back in the result's error.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(string current, CancellationToken ct)
    {
        if (!AppVersion.TryParse(current, out var currentVersion))
        {
            throw new ArgumentException($"Unrecognized version: {current}", nameof(current));
        }

        try
        {
            var release = await LoadLatestAsync(ct).ConfigureAwait(false);
            if (release.Version <= currentVersion)
            {
                Cache(release);
                return new UpdateCheck(
                    currentVersion.ToString(), release.Version.ToString(), false, null, null, null, release.Source);
            }

            var complete = await WithInstallerAsync(release, ct).ConfigureAwait(false);
            Cache(complete);
            return new UpdateCheck(
                currentVersion.ToString(),
                complete.Version.ToString(),
                true,
                complete.Installer!.Name,
                complete.Installer.SizeBytes > 0 ? complete.Installer.SizeBytes : null,
                null,
                complete.Source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new UpdateCheck(currentVersion.ToString(), null, false, null, null, Describe(error));
        }
    }

    public UpdateDownloadState Download()
    {
        lock (_gate)
        {
            return _download;
        }
    }

    /// <summary>Starts the background download. False with a reason when there is nothing to download or one is under way.</summary>
    public bool TryStartDownload(out string error)
    {
        lock (_gate)
        {
            var installer = _latest?.Installer;
            if (installer is null)
            {
                error = "请先检查更新。";
                return false;
            }

            if (_download.Phase is "downloading" or "verifying" or "ready")
            {
                error = "更新包正在下载或已下载完成。";
                return false;
            }

            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            _download = new UpdateDownloadState("downloading", 0, null, null, null);
            var token = _cancel.Token;
            _ = Task.Run(() => RunDownloadAsync(installer, token));
            error = "";
            return true;
        }
    }

    /// <summary>Stops a download in flight. Harmless once it is ready: the verified file is kept.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
        }
    }

    /// <summary>
    /// API first, release pages second. A failed API path is remembered for a short cooldown, so a rate-limited
    /// shared address does not pay for a doomed request on every check.
    /// </summary>
    private async Task<LatestRelease> LoadLatestAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_latest is not null && _clock.GetUtcNow() - _checkedAt < CheckTtl)
            {
                return _latest;
            }
        }

        Exception? primary = null;
        if (!ApiPaused())
        {
            try
            {
                return await LoadFromApiAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                primary = error;
                PauseApi(ApiCooldownFor(error));
                _log($"检查更新主路径（GitHub API）失败，切换直链备用方案：{Describe(error)}");
            }
        }

        try
        {
            var release = await LoadFromDirectAsync(ct).ConfigureAwait(false);
            if (primary is not null)
            {
                _log("已通过直链备用方案完成检查更新");
            }

            return release;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fallback)
        {
            _log($"直链备用方案同样失败：{Describe(fallback)}");
            if (primary is null)
            {
                throw;
            }

            // Both paths are down. Say so, keeping the direct failure (the closer one to the user) in front.
            throw new InvalidOperationException($"{Describe(fallback)}（API 路径：{Describe(primary)}）");
        }
    }

    /// <summary>
    /// A spent quota or an unreachable API host will not recover between two adjacent checks, so neither should
    /// be paid for again for a while. An HTTP error or a malformed answer may be transient; those retry sooner.
    /// </summary>
    private static TimeSpan ApiCooldownFor(Exception error) => error switch
    {
        RateLimitException => ApiCooldown,
        OperationCanceledException => ApiCooldown,
        HttpRequestException { StatusCode: null } => ApiCooldown,
        _ => CheckTtl,
    };

    private bool ApiPaused()
    {
        lock (_gate)
        {
            return _clock.GetUtcNow() < _apiRetryAfter;
        }
    }

    private void PauseApi(TimeSpan duration)
    {
        var until = _clock.GetUtcNow() + duration;
        lock (_gate)
        {
            if (until > _apiRetryAfter)
            {
                _apiRetryAfter = until;
            }
        }
    }

    private void Cache(LatestRelease release)
    {
        lock (_gate)
        {
            _latest = release;
            _checkedAt = _clock.GetUtcNow();
        }
    }

    /// <summary>
    /// Primary check: one REST call. The JSON's asset list is kept for the installer step; a release with no
    /// usable installer is a failed primary so the manifest-based fallback can still name one.
    /// </summary>
    private async Task<LatestRelease> LoadFromApiAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiReleaseUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("更新源没有可用的公开版本（仓库或 Release 不可见）。");
        }

        if (RateLimited(response))
        {
            throw new RateLimitException("GitHub API 访问次数已用完，请稍后再试");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("检查更新失败", null, response.StatusCode);
        }

        EnsureAllowedRedirect(response);
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
        var root = json.RootElement;
        var tag = Text(root, "tag_name")
            ?? throw new InvalidOperationException("最新发布没有可识别的版本号。");
        if (!AppVersion.TryParse(tag, out var version))
        {
            throw new InvalidOperationException("最新发布没有可识别的版本号。");
        }

        var assets = ParseAssets(root);
        if (PickInstallerAsset(assets) is null)
        {
            throw new InvalidOperationException("该版本没有提供安装包。");
        }

        return new LatestRelease(version, "api", tag, assets, null);
    }

    /// <summary>
    /// GitHub spends the primary quota with 403 + <c>x-ratelimit-remaining: 0</c>; its secondary limit
    /// answers 429 or 403 with <c>Retry-After</c> and no remaining header. All of them mean the same thing:
    /// stop spending API calls for the long cooldown and let the direct path answer.
    /// </summary>
    private static bool RateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.RetryAfter is not null
                || (response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
                    && values.Any(value => value.Trim() == "0"))));

    private static List<ReleaseAsset> ParseAssets(JsonElement root)
    {
        var assets = new List<ReleaseAsset>();
        if (!root.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var item in list.EnumerateArray())
        {
            var name = Text(item, "name");
            var url = Text(item, "browser_download_url");
            // Only GitHub URLs ever enter the candidate list: a poisoned release document cannot redirect the
            // download to a host of its choosing.
            if (!ValidAssetName(name) || !IsAllowed(url))
            {
                continue;
            }

            var size = item.TryGetProperty("size", out var number) && number.TryGetInt64(out var bytes) ? bytes : 0;
            var apiUrl = Text(item, "url");
            assets.Add(new ReleaseAsset(name!, size, url!, IsAllowed(apiUrl) ? apiUrl : null));
        }

        return assets;
    }

    private static ReleaseAsset? PickInstallerAsset(IReadOnlyList<ReleaseAsset> assets)
    {
        static bool IsInstaller(ReleaseAsset asset)
        {
            var name = asset.Name;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
        }

        return assets.FirstOrDefault(asset =>
                   IsInstaller(asset) && asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(IsInstaller);
    }

    private static string? Text(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(key, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Fallback check: no API at all. <c>releases/latest</c> answers with a redirect to
    /// <c>releases/tag/&lt;tag&gt;</c>, which is where the version comes from.
    /// </summary>
    private async Task<LatestRelease> LoadFromDirectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        // Followed or not, the answer names the tag: with auto-redirect on, RequestMessage carries the URL the
        // chain ended at; with it off, Location carries the next hop. Both point at /releases/tag/<tag>, so the
        // tag is read before the status is judged — a 302 is the normal answer, not a failure.
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        var tag = LatestTag(response);
        if (tag is null)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("更新源没有可用的公开版本（仓库或 Release 不可见）。");
            }

            response.EnsureSuccessStatusCode();
            throw new InvalidOperationException("最新发布没有可识别的版本号。");
        }

        if (!AppVersion.TryParse(tag, out var version))
        {
            throw new InvalidOperationException("最新发布没有可识别的版本号。");
        }

        return new LatestRelease(version, "direct", tag, [], null);
    }

    /// <summary>The tag in <c>…/releases/tag/&lt;tag&gt;</c>, from wherever the redirect chain ended.</summary>
    private static string? LatestTag(HttpResponseMessage response)
    {
        var target = response.Headers.Location is { } location
            ? location.IsAbsoluteUri ? location : new Uri(new Uri(LatestReleaseUrl), location)
            : response.RequestMessage?.RequestUri;

        if (target is null || !IsAllowed(target))
        {
            return null;
        }

        const string Marker = "/releases/tag/";
        var path = target.AbsolutePath.TrimEnd('/');
        var marker = path.LastIndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var tag = Uri.UnescapeDataString(path[(marker + Marker.Length)..]);
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    /// <summary>
    /// Resolves the installer and its checksum once the check found a newer version. The API path reads the
    /// per-asset <c>.exe.sha256</c>; if that is missing or unreadable the direct path's
    /// <c>SHA256SUMS.txt</c> manifest takes over — so releases published before the API path existed remain
    /// updatable. Verification is mandatory on both paths: without a digest no installer is offered.
    /// </summary>
    private async Task<LatestRelease> WithInstallerAsync(LatestRelease release, CancellationToken ct)
    {
        if (release.Installer is not null)
        {
            return release;
        }

        Exception? primary = null;
        if (release.Source == "api")
        {
            try
            {
                var fromApi = await InstallerFromApiAsync(release, ct).ConfigureAwait(false);
                return release with { Installer = fromApi };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                primary = error;
                _log($"API 校验信息不可用，改用直链校验文件：{Describe(error)}");
            }
        }

        try
        {
            var fromManifest = await InstallerFromManifestAsync(release, ct).ConfigureAwait(false);
            if (primary is not null)
            {
                _log("已通过直链校验文件确定安装包");
            }

            return release with { Source = "direct", Installer = fromManifest };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fallback)
        {
            if (primary is null)
            {
                throw;
            }

            throw new InvalidOperationException($"{Describe(fallback)}（API 校验：{Describe(primary)}）");
        }
    }

    /// <summary>Reads the <c>.exe.sha256</c> asset named by the API and pins the installer it belongs to.</summary>
    private async Task<Installer> InstallerFromApiAsync(LatestRelease release, CancellationToken ct)
    {
        var asset = PickInstallerAsset(release.Assets)
            ?? throw new InvalidOperationException("该版本没有提供安装包。");
        var checksumName = asset.Name + ".sha256";
        var checksum = release.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, checksumName, StringComparison.OrdinalIgnoreCase) && IsAllowed(item.Url))
            ?? throw new InvalidOperationException($"该版本没有提供校验文件（{checksumName}）。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        var digest = await FetchDigestAsync(checksum.Url, timeout.Token).ConfigureAwait(false);
        return new Installer(asset.Name, BuildCandidates(release, asset, asset.Name), digest, asset.Size);
    }

    /// <summary>
    /// Reads <c>SHA256SUMS.txt</c> and pins the one installer it lists. Without the API's release JSON, the
    /// name of the installer comes from the line that lists it, and so does its direct asset URL.
    /// </summary>
    private async Task<Installer> InstallerFromManifestAsync(LatestRelease release, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestAssetUrl + ChecksumsAsset);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"最新发布没有提供校验文件（{ChecksumsAsset}），已取消更新。");
        }

        response.EnsureSuccessStatusCode();
        EnsureAllowedRedirect(response);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        var found = FindInstaller(text)
            ?? throw new InvalidOperationException($"更新包缺少校验值（{ChecksumsAsset} 中没有安装包）。");
        var (name, digest) = found;

        // The API knows the size; the release pages do not, so ask the direct asset itself. A host that will not
        // answer a HEAD leaves the size unknown, which only means the page shows no size before the download.
        var asset = release.Assets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        var size = asset is { Size: > 0 }
            ? asset.Size
            : await InstallerSizeAsync(LatestAssetUrl + Uri.EscapeDataString(name), timeout.Token).ConfigureAwait(false);

        return new Installer(name, BuildCandidates(release, asset, name), digest, size);
    }

    private async Task<string> FetchDigestAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureAllowedRedirect(response);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseDigest(text)
            ?? throw new InvalidOperationException("校验文件内容无法识别（需要 64 位 SHA-256）。");
    }

    /// <summary>The installer's byte count from a HEAD, or 0 when the host will not say.</summary>
    private async Task<long> InstallerSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : 0;
        }
        catch (HttpRequestException)
        {
            // A size is a nicety for the page; the download measures itself from Content-Length regardless.
            return 0;
        }
    }

    /// <summary>
    /// Ordered download URLs: the API's public URL, its asset API URL, then the two direct release-page URLs.
    /// The last one needs neither the API nor a tag, which is why it is always kept as the final fallback.
    /// </summary>
    private static List<string> BuildCandidates(LatestRelease release, ReleaseAsset? asset, string name)
    {
        var urls = new List<string>();
        void Add(string? url)
        {
            if (IsAllowed(url) && !urls.Any(item => string.Equals(item, url, StringComparison.Ordinal)))
            {
                urls.Add(url!);
            }
        }

        if (asset is not null)
        {
            Add(asset.Url);
            Add(asset.ApiUrl);
        }

        // EscapeDataString keeps a name or tag with a slash from turning into a different release path.
        Add($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(release.Tag)}/{Uri.EscapeDataString(name)}");
        Add(LatestAssetUrl + Uri.EscapeDataString(name));
        return urls;
    }

    /// <summary>The one <c>.exe</c> line in the checksum file: its name and its SHA-256.</summary>
    private static (string Name, string Sha256)? FindInstaller(string checksums)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            var split = trimmed.IndexOfAny([' ', '\t']);
            if (split <= 0)
            {
                continue;
            }

            var hash = trimmed[..split];
            var name = trimmed[split..].Trim().TrimStart('*').Trim();
            if (!ValidAssetName(name)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || hash.Length != 64
                || !hash.All(char.IsAsciiHexDigit))
            {
                continue;
            }

            return (name, hash.ToLowerInvariant());
        }

        return null;
    }

    private static string? ParseDigest(string text) => text
        .Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(token => token.Length == 64 && token.All(char.IsAsciiHexDigit))?
        .ToLowerInvariant();

    private static bool ValidAssetName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('/')
        && !name.Contains('\\')
        && name != "."
        && name != "..";

    /// <summary>Only HTTPS GitHub hosts, including the hosts GitHub redirects release assets to.</summary>
    private static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri);

    private static bool IsAllowed(Uri? uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && DownloadHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));

    private static void EnsureAllowedRedirect(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is { } final && !IsAllowed(final))
        {
            throw new InvalidOperationException("更新重定向目标不在允许的 GitHub 域名内。");
        }
    }

    private static bool IsAssetApiUrl(string url) =>
        url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
        && url.Contains("/releases/assets/", StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string name)
    {
        var cleaned = new string(name.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray())
            .Trim('.')
            .Trim();
        return cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && cleaned.Length > 4
            ? cleaned
            : "Partner-setup.exe";
    }

    private async Task RunDownloadAsync(Installer installer, CancellationToken ct)
    {
        var file = SafeFileName(installer.Name);
        var part = Path.Combine(_downloadRoot, file + ".part");
        var final = Path.Combine(_downloadRoot, file);
        long done = 0;
        long? total = null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);

        try
        {
            Directory.CreateDirectory(_downloadRoot);
            Delete(part);
            Delete(final);

            var errors = new List<string>();
            foreach (var url in installer.Urls)
            {
                if (!IsAllowed(url))
                {
                    errors.Add($"{url}（域名不在允许范围内）");
                    continue;
                }

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        // Each attempt downloads from scratch: a stream that broke halfway cannot be resumed by
                        // appending to a partial file whose hash would then be wrong.
                        (done, total) = await DownloadOneAsync(url, installer, part, deadline.Token).ConfigureAwait(false);
                        File.Move(part, final, overwrite: true);
                        SetDownload(new UpdateDownloadState(
                            "ready", new FileInfo(final).Length, total ?? done, final, null));
                        return;
                    }
                    catch (ChecksumMismatchException)
                    {
                        // Trying another mirror cannot fix bytes that do not match the release's digest.
                        Delete(part);
                        throw;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        errors.Add(Describe(error));
                        Delete(part);
                        done = 0;
                        total = null;
                        // The 30-minute budget is shared by every candidate: retrying after it expired would
                        // only spin through URLs that cannot be reached any more.
                        if (deadline.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(deadline.Token);
                        }

                        if (attempt == 0)
                        {
                            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                        }
                    }
                }
            }

            throw new InvalidOperationException($"下载更新失败：{string.Join("；", errors)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Delete(part);
            SetDownload(new UpdateDownloadState("cancelled", done, total, null, null));
        }
        catch (OperationCanceledException)
        {
            Delete(part);
            SetDownload(new UpdateDownloadState("failed", done, total, null, "下载超时。"));
        }
        catch (Exception error)
        {
            Delete(part);
            SetDownload(new UpdateDownloadState("failed", done, total, null, Describe(error)));
        }
        finally
        {
            lock (_gate)
            {
                _cancel?.Dispose();
                _cancel = null;
            }
        }
    }

    /// <summary>Streams one candidate to disk under a size cap and verifies it looks like a Windows installer.</summary>
    private async Task<(long Done, long? Total)> DownloadOneAsync(
        string url, Installer installer, string part, CancellationToken ct)
    {
        Delete(part);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (IsAssetApiUrl(url))
        {
            // The asset API serves JSON metadata unless the caller asks for the bytes.
            request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("下载更新失败", null, response.StatusCode);
        }

        EnsureAllowedRedirect(response);
        var total = response.Content.Headers.ContentLength;
        if (total > MaxInstallerBytes)
        {
            throw new InvalidOperationException("更新包体积异常，已停止下载。");
        }

        long done = 0;
        var head = new byte[2];
        var headLength = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (headLength < 2)
                {
                    var take = Math.Min(2 - headLength, read);
                    Array.Copy(buffer, 0, head, headLength, take);
                    headLength += take;
                }

                hasher.AppendData(buffer.AsSpan(0, read));
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (done > MaxInstallerBytes)
                {
                    throw new InvalidOperationException("更新包体积异常，已停止下载。");
                }

                SetDownload(new UpdateDownloadState("downloading", done, total, null, null));
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
        }

        SetDownload(new UpdateDownloadState("verifying", done, total ?? done, null, null));
        if (done == 0)
        {
            throw new InvalidOperationException("下载的更新包为空。");
        }

        if (total is { } expected && done != expected)
        {
            throw new InvalidOperationException($"下载不完整：{done}/{expected} 字节。");
        }

        if (headLength < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
        {
            throw new InvalidOperationException("下载的文件不是有效的 Windows 安装程序。");
        }

        var digest = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!digest.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException("更新包校验失败，已删除。");
        }

        return (done, total);
    }

    private void SetDownload(UpdateDownloadState state)
    {
        lock (_gate)
        {
            _download = state;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A locked leftover is not worth failing the download over; the next attempt overwrites the .part.
        }
    }

    private static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "连接更新服务器超时。",
        // A status means the connection worked and the server answered; only a request without one is unreachable.
        HttpRequestException { StatusCode: { } status } http =>
            $"更新服务器返回 HTTP {(int)status}：{http.Message}",
        HttpRequestException http => $"无法连接更新服务器：{http.Message}",
        _ => error.Message,
    };

    private sealed class RateLimitException(string message) : InvalidOperationException(message);

    private sealed class ChecksumMismatchException(string message) : InvalidOperationException(message);

    private sealed record ReleaseAsset(string Name, long Size, string Url, string? ApiUrl);

    private sealed record Installer(
        string Name,
        IReadOnlyList<string> Urls,
        string Sha256,
        long SizeBytes);

    /// <summary>
    /// The release as last read. <see cref="Assets"/> is populated only by the API path; the installer is
    /// resolved on demand, only when the check found it newer.
    /// </summary>
    private sealed record LatestRelease(
        AppVersion Version,
        string Source,
        string Tag,
        IReadOnlyList<ReleaseAsset> Assets,
        Installer? Installer);
}
