using System.Text.Json;
using Partner.Api.Settings;
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
                approval = settings.Approval,
                allowedWindowTitles = settings.AllowedWindowTitles,

                // Server-owned: the banner announces "approve this window" with this number, so a change
                // here has to say so rather than drift between two writers.
                stepsPerGrant = Api.Settings.TaskLeaseStore.DefaultSteps,
                taskLeases = ctx.TaskLeases.Snapshot().Select(lease => new
                {
                    entityId = lease.EntityId,
                    remainingSteps = lease.RemainingSteps,
                    expiresAt = lease.ExpiresAt,
                }),
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

        app.MapPost("/api/desktop/approval", (ApprovalRequest request, ApiContext ctx) =>
        {
            // An unknown gear is refused rather than coerced: the settings file falls back to the strictest
            // gear on a typo, but a caller that asks for something this does not know should hear about it.
            var requested = request.Approval ?? string.Empty;
            if (!ApprovalGears.Values.Contains(requested, StringComparer.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"approval 必须是 $" + string.Join(" / ", ApprovalGears.Values) });
            }

            var gear = ApprovalGears.Normalize(requested);

            var before = ctx.Settings.Load();
            if (before.Desktop.Approval == gear)
            {
                return Results.Ok(new { approval = gear, changed = false });
            }

            // No restart: the gear is a decision, and governance reads it per call. Restarting would interrupt
            // a turn in flight — and would tear down a consumer started with an injected client.
            var stored = ctx.Settings.Save(before with { Desktop = before.Desktop with { Approval = gear } });

            return Results.Ok(new { approval = stored.Desktop.Approval, changed = true });
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
                    foreground = entity.IsForeground,
                    rect = new { x = entity.Rect.X, y = entity.Rect.Y, width = entity.Rect.Width, height = entity.Rect.Height },
                    // The same controls the tool returns: an endpoint that listed only windows would make the
                    // element path look like it does not exist.
                    elements = observation.Elements
                        .Where(element => element.WindowId == entity.EntityId)
                        .Select(element => new
                        {
                            elementId = element.ElementId,
                            name = element.Name,
                            role = element.Role,
                            value = element.Value,
                            clickAt = new { x = element.Centre.X, y = element.Centre.Y },
                        }),
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

            var seenElement = request.ElementId is { Length: > 0 } elementId
                ? ctx.Desktop.LastObservation?.FindElement(elementId)
                : null;

            if (request.ElementId is { Length: > 0 } && seenElement is null)
            {
                return Results.BadRequest(new { error = $"未知控件 '{request.ElementId}'：先 POST /api/desktop/observe" });
            }

            var entity = ctx.Desktop.LastObservation?.Find(seenElement?.WindowId ?? request.EntityId ?? string.Empty);
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
                    new ActionParams
                    {
                        X = request.X,
                        Y = request.Y,
                        VirtualKey = request.VirtualKey,
                        Text = request.Text,
                        ScrollDelta = request.ScrollDelta,
                        ToX = request.ToX,
                        ToY = request.ToY,
                    },
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

        app.MapPost("/api/desktop/launch", async (LaunchRequest request, ApiContext ctx, CancellationToken ct) =>
        {
            var disabled = DisabledReason(ctx);
            if (disabled is not null)
            {
                return Results.BadRequest(new { error = disabled });
            }

            var tool = new Partner.Desktop.Tools.DesktopLaunchTool();
            var result = await tool
                .InvokeAsync(JsonSerializer.Serialize(new { target = request.Target, arguments = request.Arguments }), ct)
                .ConfigureAwait(false);

            return result.Ok
                ? Results.Ok(new { launched = request.Target, detail = result.Content })
                : Results.BadRequest(new { error = result.Content });
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

    public sealed record DesktopActRequest(
        string? EntityId,
        string? ElementId,
        string? Kind,
        int X,
        int Y,
        int ToX,
        int ToY,
        ushort VirtualKey,
        string? Text,
        int ScrollDelta);

    public sealed record ApprovalRequest(string? Approval);

    public sealed record LaunchRequest(string? Target, string? Arguments);

    public sealed record ApprovalAnswer(bool Approved);
}
