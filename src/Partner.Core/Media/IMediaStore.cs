namespace Partner.Core.Media;

/// <summary>
/// Where an attachment lives: a file in the workspace, addressed by an id.
/// <para>
/// The bytes deliberately stay out of the session log. A base64 image would put megabytes into a file that is
/// meant to be readable and is rewritten whole on a removal; the log records the id, and this holds the bytes.
/// </para>
/// </summary>
public interface IMediaStore
{
    /// <summary>Stores an attachment and returns its id. Throws when the type is not accepted.</summary>
    Task<string> SaveAsync(string contentType, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Stores an attachment under the name the person gave it, so the model can be pointed at it by path.
    /// The name is sanitised rather than trusted: it is a label, not a location.
    /// </summary>
    Task<string> SaveAsync(string contentType, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>The bytes, type and name of an attachment, or null when the id is unknown.</summary>
    MediaAttachment? TryRead(string mediaId);

    /// <summary>Every id currently stored, for diagnostics and rebuilds.</summary>
    IReadOnlyList<string> List();
}

/// <summary>
/// One stored attachment.
/// </summary>
/// <param name="MediaId">The id it was stored under.</param>
/// <param name="ContentType">Its media type, as accepted at upload time.</param>
/// <param name="FileName">
/// The name the person gave it, when they gave one. This is what the model is told to call the file — an
/// attachment identified only by an id is not something a model can act on.
/// </param>
/// <param name="LocalPath">Where the file is on disk, so a message can name a path the model can open.</param>
/// <param name="Bytes">Its bytes.</param>
public sealed record MediaAttachment(
    string MediaId,
    string ContentType,
    string FileName,
    string LocalPath,
    byte[] Bytes)
{
    /// <summary>
    /// Whether the model can be handed this as an image. Only these travel as inline image parts: a PDF in an
    /// <c>image_url</c> part does not degrade, it fails the whole request.
    /// </summary>
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The attachment as the data URI an OpenAI-compatible endpoint expects. Built here rather than stored:
    /// the encoded form is roughly a third larger than the bytes, and only an image ever needs it.
    /// </summary>
    public string ToDataUri() => $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";
}
