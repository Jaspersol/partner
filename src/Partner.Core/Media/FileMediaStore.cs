namespace Partner.Core.Media;

/// <summary>
/// <see cref="IMediaStore"/> over <c>&lt;workspace&gt;/media</c>: one file per attachment, named by its id.
/// </summary>
public sealed class FileMediaStore : IMediaStore
{
    /// <summary>What a model endpoint accepts as an image, and what this store will hold.</summary>
    private static readonly Dictionary<string, string> AcceptedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    /// <summary>
    /// A ceiling on one attachment. Local models take the bytes inline, so this is about not handing the
    /// runtime a file it will spend a minute encoding — not about disk.
    /// </summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    private readonly string _directory;

    public FileMediaStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Opens the store inside a workspace, creating the directory on first use.</summary>
    public static FileMediaStore Open(string workspace) =>
        new(Path.Combine(workspace, "media"));

    public async Task<string> SaveAsync(string contentType, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!AcceptedTypes.TryGetValue(contentType ?? string.Empty, out var extension))
        {
            throw new NotSupportedException(
                $"不支持的图片类型 '{contentType}'；可用：{string.Join("、", AcceptedTypes.Keys.OrderBy(k => k))}");
        }

        var mediaId = Guid.NewGuid().ToString("n");
        var path = Path.Combine(_directory, mediaId + extension);

        // Streamed to disk with a ceiling rather than buffered whole: an oversized upload should be refused
        // while it arrives, not after it has been held in memory.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;

            while (true)
            {
                var read = await content.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxBytes)
                {
                    file.Close();
                    File.Delete(path);
                    throw new InvalidOperationException($"图片超过 {MaxBytes / (1024 * 1024)} MB 上限");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
        }

        return mediaId;
    }

    public MediaAttachment? TryRead(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        // The id is a name, not a path: anything with a separator or a dot-dot is not one of ours.
        if (mediaId.Contains('/') || mediaId.Contains('\\') || mediaId.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var (contentType, extension) in AcceptedTypes)
        {
            var path = Path.Combine(_directory, mediaId + extension);
            if (File.Exists(path))
            {
                return new MediaAttachment(mediaId, contentType, File.ReadAllBytes(path));
            }
        }

        return null;
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(_directory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
