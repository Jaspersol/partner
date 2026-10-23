using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

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
/// The check is a plain GET of <c>releases/latest</c> — the one place the version has to come from, since the
/// installer and the app carry no feed of their own. The download is verified against the release's
/// <c>SHA256SUMS.txt</c> asset before it is offered to the shell: the shell will execute it, so a truncated or
/// swapped file must not reach that point.
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

    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
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
            release.Installer.SizeBytes,
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
            _download = new UpdateDownloadState("downloading", 0, installer.SizeBytes > 0 ? installer.SizeBytes : null, null, null);
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

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("更新源没有可用的公开版本（仓库或 Release 不可见）。");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!AppVersion.TryParse(tag, out var version))
        {
            throw new InvalidOperationException("最新发布没有可识别的版本号。");
        }

        var assets = root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(ReadAsset).ToList()
            : [];

        var release = new LatestRelease(version, assets, null);
        lock (_gate)
        {
            _latest = release;
            _checkedAt = DateTimeOffset.UtcNow;
        }

        return release;
    }

    /// <summary>Reads <c>SHA256SUMS.txt</c> and pins the one installer it lists. Cached with the release.</summary>
    private async Task<LatestRelease> WithInstallerAsync(LatestRelease release, CancellationToken ct)
    {
        if (release.Installer is not null)
        {
            return release;
        }

        var sums = release.Assets.FirstOrDefault(asset => asset.Name.Equals(ChecksumsAsset, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"最新发布没有提供校验文件（{ChecksumsAsset}），已取消更新。");
        var exe = release.Assets.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("最新发布里没有安装包。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        var text = await _http.GetStringAsync(sums.Url, timeout.Token).ConfigureAwait(false);
        var sha = FindChecksum(text, exe.Name)
            ?? throw new InvalidOperationException($"更新包缺少校验值（{ChecksumsAsset} 中没有 {exe.Name}）。");

        var complete = release with { Installer = new Installer(exe.Name, exe.Url, sha, exe.Size) };
        lock (_gate)
        {
            _latest = complete;
        }

        return complete;
    }

    private static Asset ReadAsset(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        var url = element.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() ?? "" : "";
        var size = element.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
        return new Asset(name, url, size);
    }

    private static string? FindChecksum(string checksums, string fileName)
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
            if (hash.Length == 64 && name == fileName)
            {
                return hash;
            }
        }

        return null;
    }

    private async Task RunDownloadAsync(Installer installer, CancellationToken ct)
    {
        var part = Path.Combine(_downloadRoot, installer.Name + ".part");
        var final = Path.Combine(_downloadRoot, installer.Name);
        long done = 0;
        long? total = installer.SizeBytes > 0 ? installer.SizeBytes : null;

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
        HttpRequestException http => $"无法连接更新服务器：{http.Message}",
        _ => error.Message,
    };

    private sealed record Asset(string Name, string Url, long Size);

    private sealed record Installer(string Name, string Url, string Sha256, long SizeBytes);

    /// <summary>The release as last read. The installer is resolved on demand, only when the check found it newer.</summary>
    private sealed record LatestRelease(AppVersion Version, IReadOnlyList<Asset> Assets, Installer? Installer);
}
