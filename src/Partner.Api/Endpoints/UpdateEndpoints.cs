using Partner.Api.Updates;

namespace Partner.Api.Endpoints;

/// <summary>
/// The update check and its download. The check is a GET because it changes nothing; the download is a POST
/// that starts work and is then polled, the same shape as the engine-module download.
/// </summary>
public static class UpdateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/update/check", (string? current, UpdateService updates, CancellationToken ct) =>
            CheckAsync(current, updates, ct));

        app.MapPost("/api/update/download", (UpdateService updates) =>
            updates.TryStartDownload(out var error)
                ? Results.Accepted(value: updates.Download())
                : Results.Conflict(new { error }));

        app.MapGet("/api/update/download", (UpdateService updates) => Results.Ok(updates.Download()));

        app.MapPost("/api/update/download/cancel", (UpdateService updates) =>
        {
            updates.Cancel();
            return Results.Ok(updates.Download());
        });
    }

    private static async Task<IResult> CheckAsync(string? current, UpdateService updates, CancellationToken ct)
    {
        if (!AppVersion.TryParse(current, out _))
        {
            return Results.BadRequest(new { error = "缺少或无法识别当前版本号。" });
        }

        return Results.Ok(await updates.CheckAsync(current!, ct));
    }
}
