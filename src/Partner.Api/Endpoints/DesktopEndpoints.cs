using Partner.Desktop;
using Partner.Desktop.Input;

namespace Partner.Api.Endpoints;

/// <summary>
/// The desktop capability over HTTP: what the model can do through its tools, plus the approval queue those
/// tools wait in.
/// <para>
/// The endpoints exist for the UI and for diagnostics — a human clicking here *is* the approval, so these do
/// not ask again. The model's path goes through the tools, where governance puts every action in the
/// <c>ask</c> list.
/// </para>
/// </summary>
public static class DesktopEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/desktop", (ApiContext ctx) =>
        {
            var settings = ctx.Settings.Load().Desktop;

            return Results.Ok(new
            {
                enabled = settings.Enabled,
                leaseTtlMs = settings.LeaseTtlMs,
                allowedWindowTitles = settings.AllowedWindowTitles,
                lastObsId = ctx.Desktop.LastObservation?.ObsId,
                entityCount = ctx.Desktop.LastObservation?.Entities.Count ?? 0,
                entities = (ctx.Desktop.LastObservation?.Entities ?? Array.Empty<Partner.Desktop.Perception.UiEntity>())
                    .Select(entity => new
                    {
                        entityId = entity.EntityId,
                        title = entity.Label,
                        process = entity.Value,
                        foreground = entity.IsForeground,
                    }),
            });
        });

        app.MapPost("/api/desktop/observe", (ApiContext ctx) =>
        {
            var disabled = DisabledReason(ctx);
            if (disabled is not null)
            {
                return Results.BadRequest(new { error = disabled });
            }

            // Observing is what issues the leases an action must carry, so this is the only way in.
            var observation = ctx.Desktop.Observe();
            var policy = new Partner.Desktop.Tools.DesktopToolPolicy(ctx.Settings.Load().Desktop.AllowedWindowTitles);

            var entities = observation.Entities
                .Where(entity => policy.Allows(entity.Label))
                .Select(entity => new
                {
                    entityId = entity.EntityId,
                    title = entity.Label,
                    process = entity.Value,
                    rect = new { x = entity.Rect.X, y = entity.Rect.Y, width = entity.Rect.Width, height = entity.Rect.Height },
                })
                .ToList();

            return Results.Ok(new { obsId = observation.ObsId, count = entities.Count, entities });
        });

        app.MapPost("/api/desktop/act", async (DesktopActRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var disabled = DisabledReason(ctx);
            if (disabled is not null)
            {
                return Results.BadRequest(new { error = disabled });
            }

            if (!Enum.TryParse<ActionKind>(request.Kind, ignoreCase: true, out var kind))
            {
                return Results.BadRequest(new
                {
                    error = $"kind 必须是 {string.Join(" / ", Enum.GetNames<ActionKind>())}",
                });
            }

            var entity = ctx.Desktop.LastObservation?.Find(request.EntityId ?? string.Empty);
            if (entity is null)
            {
                return Results.BadRequest(new { error = $"未知实体 '{request.EntityId}'：先 POST /api/desktop/observe" });
            }

            var policy = new Partner.Desktop.Tools.DesktopToolPolicy(ctx.Settings.Load().Desktop.AllowedWindowTitles);
            if (!policy.Allows(entity.Label))
            {
                return Results.BadRequest(new { error = $"窗口 '{entity.Label}' 不在允许列表内" });
            }

            var action = await ctx.Desktop
                .ActAsync(
                    entity.EntityId,
                    kind,
                    new ActionParams { X = request.X, Y = request.Y, VirtualKey = request.VirtualKey },
                    ct)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                actionId = action.ActionId,
                guardPassed = action.GuardPassed,
                guardReason = action.GuardReason,
                injected = action.Injection?.Ok,
                injectionDetail = action.Injection?.Detail,
                verified = action.PostVerify?.Decision.ToString(),
            });
        });

        // The model's tool call parks here until someone answers; nobody answering means denied.
        app.MapGet("/api/approvals", (ApiContext ctx) => Results.Ok(new
        {
            timeoutSeconds = ctx.Approvals.Timeout.TotalSeconds,
            pending = ctx.Approvals.Pending.Select(request => new
            {
                approvalId = request.ApprovalId,
                toolName = request.ToolName,
                arguments = request.ArgumentsJson,
                requestedAt = request.RequestedAt,
                expiresAt = request.ExpiresAt,
            }),
        }));

        app.MapPost("/api/approvals/{approvalId}", (string approvalId, ApprovalAnswer answer, ApiContext ctx) =>
            ctx.Approvals.Answer(approvalId, answer.Approved)
                ? Results.Ok(new { approvalId, approved = answer.Approved })
                : Results.NotFound(new { error = $"'{approvalId}' 不存在或已处理" }));
    }

    private static string? DisabledReason(ApiContext ctx) =>
        ctx.Settings.Load().Desktop.Enabled ? null : "桌面能力未启用（设置里的「桌面」段）";

    public sealed record DesktopActRequest(string? EntityId, string? Kind, int X, int Y, ushort VirtualKey);

    public sealed record ApprovalAnswer(bool Approved);
}
