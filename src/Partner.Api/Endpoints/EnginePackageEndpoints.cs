using Partner.Api.Voice;

namespace Partner.Api.Endpoints;

/// <summary>
/// The local-engine module as the settings page sees it: is it on disk, download it, or point at a copy the
/// user already has. Detection is just a GET — it re-reads the disk every time, so "检测" needs no endpoint of its own.
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
