using Partner.Api.Pet;
using Partner.Api.Settings;
using Partner.Api.Voice;

namespace Partner.Api.Endpoints;

/// <summary>
/// The pet window's own API: what to load, what was said, and what went wrong.
/// <para>
/// Separate from the settings page's endpoints on purpose. The window polls the feed a few times a second; the
/// settings document is large and changes rarely, and <c>settingsRevision</c> is what tells the window when to
/// re-read it.
/// </para>
/// </summary>
public static class PetEndpoints
{
    public sealed record CoreImportRequest(string? Path);

    public sealed record ModelImportRequest(string? Path, string? Model3);

    public sealed record StatusRequest(string? Error);

    /// <summary>Both halves are optional: a preview is either a motion or a face, and an unknown name is ignored.</summary>
    public sealed record PreviewRequest(string? Action, string? Expression);

    public static void Map(WebApplication app, ApiContext ctx)
    {
        app.MapGet("/api/pet/core", (PetAssetStore pet) => Results.Ok(pet.Status()));

        app.MapPost("/api/pet/core/import", (CoreImportRequest request, PetAssetStore pet) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { error = "path 不能为空" });
            }

            try
            {
                return Results.Ok(pet.ImportCore(request.Path));
            }
            catch (PetImportException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        app.MapGet("/api/pet/models", (PetAssetStore pet) => Results.Ok(pet.List()));

        app.MapPost("/api/pet/models/import", (ModelImportRequest request, PetAssetStore pet) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { error = "path 不能为空" });
            }

            try
            {
                return Results.Ok(pet.Import(request.Path, request.Model3));
            }
            catch (PetImportException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        // An unknown id is not an error: the page deletes what it listed, and a double click must not look like
        // a failed delete.
        app.MapDelete("/api/pet/models/{id}", (string id, PetAssetStore pet, ApiContext context) =>
        {
            var deleted = pet.Delete(id);
            if (deleted)
            {
                ForgetDeletedModel(context.Settings, id);
            }

            return Results.Ok(new { deleted });
        });

        app.MapGet("/api/pet/feed", async (long? after, long? previewAfter, PetAssetStore pet, VoiceHost voice, CancellationToken ct) =>
        {
            var (replies, lastSeq) = await RepliesAsync(ctx, after ?? 0, ct).ConfigureAwait(false);
            return Results.Ok(new PetFeed(
                voice.IsSpeaking,
                ctx.Settings.Revision,
                replies,
                pet.LastError,
                lastSeq,
                pet.PendingPreview(previewAfter ?? 0)));
        });

        // The settings page's 「预览」 button: it has no other route to the pet window, which may be showing a
        // different model and is the only place a mapped motion can actually be judged.
        app.MapPost("/api/pet/preview", (PreviewRequest request, PetAssetStore pet) =>
            Results.Ok(pet.RequestPreview(request.Action ?? string.Empty, request.Expression ?? string.Empty)));

        // The pet window is the only place that sees the webview's console, so what it fails at has to come back
        // here to be visible at all.
        app.MapPost("/api/pet/status", (StatusRequest request, PetAssetStore pet) =>
        {
            pet.Report(request.Error);
            return Results.Ok(new { error = pet.LastError });
        });
    }

    /// <summary>
    /// The last five finalized assistant replies, oldest first, and the cursor for the caller's next <c>after</c>.
    /// The log is read in pages because it is the user's whole history and the window polls every second. Only
    /// <see cref="Partner.Core.Contracts.SessionEventKind.Assistant"/> counts, and the body is cut to what fits above
    /// a model. The cursor is the session's last seq even when no reply was found (see <see cref="PetFeed.LastSeq"/>).
    /// </summary>
    private static async Task<(IReadOnlyList<PetReply> Replies, long LastSeq)> RepliesAsync(
        ApiContext ctx, long after, CancellationToken ct)
    {
        const int PageSize = 100;
        const int Keep = 5;
        const int MaxBodyChars = 4000;

        var found = new List<PetReply>(Keep);
        var lastSeq = ctx.Session.LastSeq;
        var from = Math.Max(1, after + 1);
        while (from <= lastSeq)
        {
            var page = new List<Partner.Core.Sessions.SessionEvent>(PageSize);
            await foreach (var sessionEvent in ctx.Session
                .ReadAsync(from, PageSize, ct)
                .ConfigureAwait(false))
            {
                page.Add(sessionEvent);
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var sessionEvent in page)
            {
                if (sessionEvent.Seq > lastSeq)
                {
                    continue;
                }

                if (sessionEvent.Kind != Partner.Core.Contracts.SessionEventKind.Assistant)
                {
                    continue;
                }

                var body = sessionEvent.Body.Length > MaxBodyChars
                    ? sessionEvent.Body[..SafeCut(sessionEvent.Body, MaxBodyChars)]
                    : sessionEvent.Body;

                // A ring of five: the window shows a short stack, so older ones are dropped as newer ones arrive.
                found.Add(new PetReply(sessionEvent.Seq, body));
                if (found.Count > Keep)
                {
                    found.RemoveAt(0);
                }
            }

            from = page[^1].Seq + 1;
        }

        return (found, lastSeq);
    }

    /// <summary>
    /// Drops what the settings still say about a just-deleted model. Otherwise <c>activeModelId</c> would point at a
    /// model the window cannot load, and since validation refuses an unknown id, the user's next save would fail.
    /// </summary>
    internal static void ForgetDeletedModel(SettingsStore settings, string id)
    {
        var stored = settings.Load();
        var pet = stored.Pet;
        var active = pet.ActiveModelId == id;
        if (!active && !pet.Profiles.ContainsKey(id))
        {
            return;
        }

        // Save bumps the revision, which is what makes the pet window re-read the document.
        settings.Save(stored with
        {
            Pet = pet with
            {
                ActiveModelId = active ? null : pet.ActiveModelId,
                Profiles = pet.Profiles
                    .Where(pair => !string.Equals(pair.Key, id, StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            },
        });
    }

    /// <summary>
    /// The cut index for a truncation. Splitting a surrogate pair would leave half a character, which JSON
    /// serializes as a replacement character: a visibly wrong last glyph in the bubble.
    /// </summary>
    private static int SafeCut(string text, int limit) =>
        char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
}