namespace Partner.Core.Media;

/// <summary>
/// <see cref="IMediaStore"/> over <c>&lt;workspace&gt;/media</c>: one file per attachment.
/// <para>
/// A file is named <c>{id}__{name}</c>, where the name is the one the person gave it. Two reasons, and the
/// second is the important one: a reader recognises <c>季度报表.pdf</c> where they cannot recognise
/// <c>a3f9…pdf</c>, and <b>the model can only open a path it can read</b> — an attachment identified solely by an
/// opaque id is not something a tool like <c>read</c> can act on. Files written before this naming arrived are
/// still found by id.
/// </para>
/// </summary>
public sealed class FileMediaStore : IMediaStore
{
    /// <summary>Media type → the extension this store writes it under.</summary>
    private static readonly Dictionary<string, string> AcceptedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images: these reach the model inline as image parts, so the types are the ones every endpoint accepts.
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",

        // Documents and text. Reached as a *path* in the message, not inline — see MediaAttachment.IsImage.
        ["application/pdf"] = ".pdf",
        ["application/rtf"] = ".rtf",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["application/yaml"] = ".yaml",
        ["application/x-yaml"] = ".yaml",
        ["application/toml"] = ".toml",
        ["application/javascript"] = ".js",
        ["text/plain"] = ".txt",
        ["text/markdown"] = ".md",
        ["text/csv"] = ".csv",
        ["text/html"] = ".html",
        ["text/xml"] = ".xml",
        ["text/yaml"] = ".yaml",
        ["text/x-toml"] = ".toml",
        ["text/javascript"] = ".js",
        ["text/x-csharp"] = ".cs",
        ["text/x-python"] = ".py",
        ["text/x-sh"] = ".sh",
        ["text/x-powershell"] = ".ps1",
        ["text/x-sql"] = ".sql",
        ["text/x-java"] = ".java",
        ["text/x-go"] = ".go",
        ["text/x-rust"] = ".rs",
        ["text/x-c"] = ".c",
        ["text/x-c++"] = ".cpp",
    };

    /// A ceiling on one attachment. Local models take image bytes inline, so this is about not handing the
    /// runtime a file it will spend a minute encoding — not about disk.
    /// </summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>What a caller may name a file. Short enough to keep the prompt readable, long enough for a path.</summary>
    private const int MaxNameLength = 120;

    /// <summary>Separates the id from the name in a stored file name.</summary>
    private const string NameSeparator = "__";

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

    /// <summary>The types this store accepts, for a caller that has to say so before the upload rather than after.</summary>
    public static IReadOnlyCollection<string> Types => AcceptedTypes.Keys;

    public Task<string> SaveAsync(string contentType, Stream content, CancellationToken ct = default) =>
        SaveAsync(contentType, fileName: null, content, ct);

    public async Task<string> SaveAsync(string contentType, string? fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var extension = ExtensionFor(contentType, fileName);
        var mediaId = Guid.NewGuid().ToString("n");
        var name = SafeName(fileName, mediaId, extension);
        var path = Path.Combine(_directory, mediaId + NameSeparator + name);

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
                    throw new InvalidOperationException($"附件超过 {MaxBytes / (1024 * 1024)} MB 上限");
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

        var directory = new DirectoryInfo(_directory);
        if (!directory.Exists)
        {
            return null;
        }

        foreach (var candidate in directory.EnumerateFiles($"{mediaId}*"))
        {
            var stored = candidate.Name;
            var name = stored.Length > mediaId.Length + NameSeparator.Length
                && stored.StartsWith(mediaId + NameSeparator, StringComparison.Ordinal)
                    ? stored[(mediaId.Length + NameSeparator.Length)..]
                    : stored[(mediaId.Length)..];

            var extension = candidate.Extension;
            var contentType = AcceptedTypes.FirstOrDefault(
                pair => string.Equals(pair.Value, extension, StringComparison.OrdinalIgnoreCase)).Key
                ?? "application/octet-stream";

            return new MediaAttachment(mediaId, contentType, name, candidate.FullName, File.ReadAllBytes(candidate.FullName));
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
            .Select(file => IdOf(Path.GetFileName(file)))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>What is stored before a name is chosen: the legacy form is <c>{id}{ext}</c>, the current one has a separator.</summary>
    private static string IdOf(string fileName)
    {
        var at = fileName.IndexOf(NameSeparator, StringComparison.Ordinal);
        return at > 0 ? fileName[..at] : Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// The extension to write under, from the declared type or — when the browser sent a useless one — from the
    /// file's own extension. A wrong guess is not fatal: a model opening the path sees the bytes either way.
    /// </summary>
    private static string ExtensionFor(string? contentType, string? fileName)
    {
        var declared = (contentType ?? string.Empty).Split(';')[0].Trim();
        if (AcceptedTypes.ContainsKey(declared))
        {
            return AcceptedTypes[declared];
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var ext = Path.GetExtension(Path.GetFileName(fileName));
            if (ext.Length > 1)
            {
                return ext.ToLowerInvariant();
            }
        }

        if (!string.IsNullOrWhiteSpace(declared))
        {
            // An unlisted but well-formed type (application/vnd.openxmlformats-officedocument…, say) is allowed
            // through: the extension is guessed from the subtype. This is a document store, not an execution
            // surface, and refusing a .docx because nobody listed it helps nobody.
            var subtype = declared.Split('/').LastOrDefault() ?? string.Empty;
            foreach (var suffix in new[] { "vnd.openxmlformats-officedocument.wordprocessingml.document", "vnd.openxmlformats-officedocument.spreadsheetml.sheet", "vnd.openxmlformats-officedocument.presentationml.presentation", "vnd.ms-excel", "vnd.ms-word" })
            {
                if (declared.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return suffix switch
                    {
                        "vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                        "vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                        "vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                        "vnd.ms-excel" => ".xls",
                        _ => ".doc",
                    };
                }
            }

            if (subtype.Length is > 1 and <= 8 && subtype.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.'))
            {
                return "." + subtype.ToLowerInvariant();
            }
        }

        throw new NotSupportedException(
            $"无法确定附件类型 '{declared}'（文件名 '{fileName ?? "(无)"}'）；请给出可识别的扩展名。");
    }

    /// <summary>
    /// A name safe to put in a path and in a prompt: no directory part, no separators, no control characters,
    /// and bounded in length. The caller's name is a label, never a location.
    /// </summary>
    private static string SafeName(string? fileName, string mediaId, string extension)
    {
        var leaf = fileName is null ? string.Empty : Path.GetFileName(fileName.Trim());
        var cleaned = new string(leaf
            .Where(c => !char.IsControl(c) && c is not ('<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'))
            .ToArray())
            .Trim();

        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            return mediaId + extension;
        }

        // Keep the stem short but keep the extension: a name the model cannot open the right way is worse than
        // a long one, and the extension is what says how.
        if (cleaned.Length > MaxNameLength)
        {
            var stem = Path.GetFileNameWithoutExtension(cleaned);
            var ext = Path.GetExtension(cleaned);
            var room = Math.Max(1, MaxNameLength - ext.Length);
            cleaned = (stem.Length > room ? stem[..room] : stem) + ext;
        }

        return cleaned;
    }
}
