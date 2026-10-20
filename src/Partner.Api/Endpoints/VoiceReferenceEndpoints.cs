namespace Partner.Api.Endpoints;

/// <summary>
/// Reference audio for voice cloning. A file picked in the UI is only a browser File with no disk path, and the
/// local TTS engine needs a path it can open, so the file is uploaded into the workspace's <c>voices</c>
/// directory and the stored path is written into settings.
/// </summary>
public static class VoiceReferenceEndpoints
{
    /// <summary>Reference audio is usually seconds long; 20 MB holds a full wav without becoming an upload channel.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly string[] Extensions = [".wav"];

    public static void Map(WebApplication app, string workspace)
    {
        // Raw body plus the name in a header, like the attachment upload.
        app.MapPost("/api/voice/reference", async (HttpRequest request, CancellationToken ct) =>
        {
            var encoded = request.Headers["X-File-Name"].ToString();
            var name = SafeName(encoded.Contains('%') ? Uri.UnescapeDataString(encoded) : encoded);
            if (name is null)
            {
                return Results.BadRequest(new { error = $"参考音频需要是 {string.Join(" / ", Extensions)} 文件" });
            }

            if (request.ContentLength is > MaxBytes)
            {
                return Results.BadRequest(new { error = $"文件太大，上限 {MaxBytes / 1024 / 1024} MB" });
            }

            var directory = Path.Combine(workspace, "voices");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);

            // Written under a temporary name first, so a cut-off upload never replaces a good file.
            var temporary = path + ".part";
            long written;
            await using (var file = File.Create(temporary))
            {
                var buffer = new byte[81920];
                written = 0;
                int read;
                while ((read = await request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > MaxBytes)
                    {
                        file.Close();
                        File.Delete(temporary);
                        return Results.BadRequest(new { error = $"文件太大，上限 {MaxBytes / 1024 / 1024} MB" });
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            if (written == 0)
            {
                File.Delete(temporary);
                return Results.BadRequest(new { error = "文件是空的" });
            }

            File.Move(temporary, path, overwrite: true);
            return Results.Ok(new { path, fileName = name, bytes = written });
        });
    }

    /// <summary>The name is a label, never a location: directory parts and odd characters are dropped; the extension must be allowed.</summary>
    internal static string? SafeName(string? raw)
    {
        var leaf = Path.GetFileName((raw ?? string.Empty).Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(leaf.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 100)
        {
            cleaned = cleaned[^100..];
        }

        return Extensions.Contains(Path.GetExtension(cleaned), StringComparer.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(cleaned).Length > 0
            ? cleaned
            : null;
    }
}
