using Partner.Api.Voice;

namespace Partner.Api.Endpoints;

/// <summary>
/// The local-engine module as the settings page sees it: on disk, downloading, or pointed at an existing copy.
/// Detection is a plain GET; it re-reads the disk every time, so no separate endpoint is needed.
/// </summary>
public static class EnginePackageEndpoints
{
    public sealed record RootRequest(string? Root);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/local-engines/package", (EnginePackage package) => Results.Ok(package.Status()));

        app.MapPost("/api/local-engines/package/download", (EnginePackage package) =>
            package.TryStartDownload(out var error)
                ? Results.Accepted(value: package.Status())
                : Results.Conflict(new { error }));

        app.MapPost("/api/local-engines/package/cancel", (EnginePackage package) =>
        {
            package.Cancel();
            return Results.Ok(package.Status());
        });

        app.MapPut("/api/local-engines/package/root", (RootRequest request, EnginePackage package) =>
            package.TrySetRoot(request.Root, out var error)
                ? Results.Ok(package.Status())
                : Results.BadRequest(new { error }));
    }
}
