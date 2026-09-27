namespace Partner.Core.Media;

/// <summary>
/// Where an attached image lives: a file in the workspace, addressed by an id.
/// <para>
/// The bytes deliberately stay out of the session log. A base64 image would put megabytes into a file that is
/// meant to be readable and is rewritten whole on a removal; the log records the id, and this holds the bytes.
/// </para>
/// </summary>
public interface IMediaStore
{
    /// <summary>Stores an image and returns its id. Throws when the type is not an accepted image.</summary>
    Task<string> SaveAsync(string contentType, Stream content, CancellationToken ct = default);

    /// <summary>The bytes and type of an attachment, or null when the id is unknown.</summary>
    MediaAttachment? TryRead(string mediaId);

    /// <summary>Every id currently stored, for diagnostics and rebuilds.</summary>
    IReadOnlyList<string> List();
}

/// <summary>One stored attachment.</summary>
/// <param name="MediaId">The id it was stored under.</param>
/// <param name="ContentType">Its media type, as accepted at upload time.</param>
/// <param name="Bytes">Its bytes.</param>
public sealed record MediaAttachment(string MediaId, string ContentType, byte[] Bytes)
{
    /// <summary>
    /// The attachment as the data URI an OpenAI-compatible endpoint expects. Built here rather than stored:
    /// the encoded form is roughly a third larger than the bytes, and nothing else needs it.
    /// </summary>
    public string ToDataUri() => $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";
}
