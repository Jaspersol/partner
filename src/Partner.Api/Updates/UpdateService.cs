using System.Net;
using System.Security.Cryptography;

namespace Partner.Api.Updates;

/// <summary>
/// What the settings page shows after a check. <see cref="Error"/> is set instead of the promise failing:
/// "the check did not happen" is an answer the page can show beside the version, not an exceptional state.
/// </summary>
public sealed record UpdateCheck(
    string Current,
    string? Latest,
    bool Available,
    string? InstallerName,
    long? SizeBytes,
    string? Error);

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
/// The check walks the release pages rather than the API: <c>releases/latest</c> answers with a redirect to
/// <c>releases/tag/&lt;tag&gt;</c>, and <c>releases/latest/download/&lt;asset&gt;</c> serves the asset itself. Those two
/// are plain web requests with no quota; <c>api.github.com/repos/…/releases/latest</c> allows 60 calls an hour
/// per address and answers 403 once a shared connection has spent them, which left the settings page unable to
/// check at all. The download is verified against the release's <c>SHA256SUMS.txt</c> asset before it is offered
/// to the shell: the shell will execute it, so a truncated or swapped file must not reach that point.
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

    private const string LatestReleaseUrl = $"https://github.com/{Repository}/releases/latest";
    private const string LatestAssetUrl = $"https://github.com/{Repository}/releases/latest/download/";
    private const string ChecksumsAsset = "SHA256SUMS.txt";
    private static readonly TimeSpan CheckTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _downloadRoot;
    private readonly object _gate = new();

    private LatestRelease? _latest;
    private DateTimeOffset _checkedAt;
    private UpdateDownloadState _download = new("idle", 0, null, null, null);
    private CancellationTokenSource? _cancel;

    /// <param name="http">Injected by tests; production uses a client with no timeout, since a download is long.</param>
    /// <param name="downloadRoot">Injected by tests; production uses the per-user temp folder.</param>
    public UpdateService(HttpClient? http = null, string? downloadRoot = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Partner");
        _downloadRoot = downloadRoot ?? Path.Combine(Path.GetTempPath(), "partner-update");
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

        LatestRelease release;
        try
        {
            release = await LoadLatestAsync(ct).ConfigureAwait(false);
            if (release.Version <= currentVersion)
            {
                return new UpdateCheck(currentVersion.ToString(), release.Version.ToString(), false, null, null, null);
            }

            release = await WithInstallerAsync(release, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new UpdateCheck(currentVersion.ToString(), null, false, null, null, Describe(error));
        }

        return new UpdateCheck(
            currentVersion.ToString(),
            release.Version.ToString(),
            true,
            release.Installer!.Name,
            release.Installer.SizeBytes > 0 ? release.Installer.SizeBytes : null,
            null);
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
            // No size up front: the release pages do not carry one, so the total arrives with Content-Length.
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

    private async Task<LatestRelease> LoadLatestAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_latest is not null && DateTimeOffset.UtcNow - _checkedAt < CheckTtl)
            {
                return _latest;
            }
        }

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

        var release = new LatestRelease(version, null);
        lock (_gate)
        {
            _latest = release;
            _checkedAt = DateTimeOffset.UtcNow;
        }

        return release;
    }

    /// <summary>The tag in <c>…/releases/tag/&lt;tag&gt;</c>, from wherever the redirect chain ended.</summary>
    private static string? LatestTag(HttpResponseMessage response)
    {
        var target = response.Headers.Location is { } location
            ? location.IsAbsoluteUri ? location : new Uri(new Uri(LatestReleaseUrl), location)
            : response.RequestMessage?.RequestUri;

        if (target is null)
        {
            return null;
        }

        const string Marker = "/releases/tag/";
        var path = target.AbsolutePath.TrimEnd('/');
        var marker = path.LastIndexOf(Marker, StringComparison.Ordinal);
        return marker < 0 ? null : Uri.UnescapeDataString(path[(marker + Marker.Length)..]);
    }

    /// <summary>
    /// Reads <c>SHA256SUMS.txt</c> and pins the one installer it lists. Cached with the release.
    ///
    /// <para>
    /// The checksum file is the only asset listing there is: without the API's release JSON, the name of the
    /// installer comes from the line that lists it, and so does its URL under <c>latest/download/</c>.
    /// </para>
    /// </summary>
    private async Task<LatestRelease> WithInstallerAsync(LatestRelease release, CancellationToken ct)
    {
        if (release.Installer is not null)
        {
            return release;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestAssetUrl + ChecksumsAsset);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"最新发布没有提供校验文件（{ChecksumsAsset}），已取消更新。");
        }

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        var installer = FindInstaller(text)
            ?? throw new InvalidOperationException($"更新包缺少校验值（{ChecksumsAsset} 中没有安装包）。");

        // The release JSON used to carry the size; the pages do not, so ask the asset itself. A host that will
        // not answer a HEAD leaves the size unknown, which only means the page shows no size before the download.
        var size = await InstallerSizeAsync(installer.Url, timeout.Token).ConfigureAwait(false);

        var complete = release with { Installer = installer with { SizeBytes = size } };
        lock (_gate)
        {
            _latest = complete;
        }

        return complete;
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

    /// <summary>The one <c>.exe</c> line in the checksum file, as an installer whose URL is its asset URL.</summary>
    private static Installer? FindInstaller(string checksums)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var split = trimmed.IndexOfAny([' ', '\t']);
            if (split <= 0)
            {
                continue;
            }

            var hash = trimmed[..split];
            var name = trimmed[split..].TrimStart();
            if (hash.Length == 64 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new Installer(name, LatestAssetUrl + Uri.EscapeDataString(name), hash, 0);
            }
        }

        return null;
    }

    private async Task RunDownloadAsync(Installer installer, CancellationToken ct)
    {
        var part = Path.Combine(_downloadRoot, installer.Name + ".part");
        var final = Path.Combine(_downloadRoot, installer.Name);
        long done = 0;
        long? total = null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DownloadTimeout);

        try
        {
            Directory.CreateDirectory(_downloadRoot);
            Delete(part);
            Delete(final);

            using var request = new HttpRequestMessage(HttpMethod.Get, installer.Url);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            total = response.Content.Headers.ContentLength ?? total;

            await using (var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    done += read;
                    SetDownload(new UpdateDownloadState("downloading", done, total, null, null));
                }
            }

            SetDownload(new UpdateDownloadState("verifying", done, total ?? done, null, null));
            var actual = await Sha256Async(part, deadline.Token).ConfigureAwait(false);
            if (!actual.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Delete(part);
                throw new InvalidOperationException("更新包校验失败，已删除。");
            }

            File.Move(part, final, overwrite: true);
            SetDownload(new UpdateDownloadState("ready", new FileInfo(final).Length, total ?? done, final, null));
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
            SetDownload(new UpdateDownloadState("failed", done, total, null, error.Message));
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

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "连接更新服务器超时。",
        // A status means the connection worked and the server answered; only a request without one is unreachable.
        HttpRequestException { StatusCode: { } status } http => $"更新服务器返回 HTTP {(int)status}：{http.Message}",
        HttpRequestException http => $"无法连接更新服务器：{http.Message}",
        _ => error.Message,
    };

    private sealed record Installer(string Name, string Url, string Sha256, long SizeBytes);

    /// <summary>The release as last read. The installer is resolved on demand, only when the check found it newer.</summary>
    private sealed record LatestRelease(AppVersion Version, Installer? Installer);
}
