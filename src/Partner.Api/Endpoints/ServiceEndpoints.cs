using Partner.Api.Auth;
using Partner.Api.Services;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;
using Partner.Core.Memory;

namespace Partner.Api.Endpoints;

/// <summary>
/// CRUD for the pass-through services. All endpoints hang off the main host, so <c>UseTokenAuth</c> protects
/// them by construction. The plaintext token is returned exactly once — at creation or rotation.
/// </summary>
public static class ServiceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/services", async (ServiceHost host, CancellationToken ct) =>
            Results.Ok(await host.StateAsync(ct).ConfigureAwait(false)));

        app.MapGet("/api/services/{id}", async (string id, ServiceHost host, CancellationToken ct) =>
        {
            var item = await host.GetAsync(id, ct).ConfigureAwait(false);
            return item is null
                ? Results.NotFound(new { error = $"service '{id}' not found" })
                : Results.Ok(item);
        });

        app.MapPost("/api/services", async (
            ServiceSettings body, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            await ctx.Settings.ApplyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
            RuntimeSettings? before = null;
            ServiceSettings? created = null;
            RuntimeSettings saved;
            try
            {
                saved = ctx.Settings.Update(current =>
                {
                    before = current;
                    created = body with
                    {
                        Id = GenerateId(current.Services),
                        Token = TokenAuth.Generate(),
                        Protocol = LlmProtocols.Normalize(body.Protocol),
                        ContextTokens = body.ContextTokens > 0 ? body.ContextTokens : current.Model.ContextTokens,
                    };
                    return current with { Services = current.Services with { Instances = current.Services.Instances.Append(created).ToList() } };
                });
            }
            catch (SettingsValidationException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message, errors = invalid.Errors });
            }

            await host.ReconcileAsync(before!.Services, saved.Services, ct).ConfigureAwait(false);

            var persisted = saved.Services.Instances.First(s => s.Id == created!.Id);
            var state = await host.GetAsync(created!.Id, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                settings = ServiceSettingsView.From(persisted),
                state = state?.State,
                token = created.Token, // shown once, never again
            });
            }
            finally { ctx.Settings.ApplyGate.Release(); }
        });

        app.MapPut("/api/services/{id}", async (
            string id, ServiceSettings body, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            await ctx.Settings.ApplyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
            RuntimeSettings? before = null;
            var found = false;
            RuntimeSettings saved;
            try
            {
                saved = ctx.Settings.Update(current =>
                {
                    before = current;
                    found = current.Services.Instances.Any(s => s.Id == id);
                    if (!found) return null;
                    var updated = body with { Id = id, Protocol = LlmProtocols.Normalize(body.Protocol) };
                    return current with { Services = current.Services with
                    {
                        Instances = current.Services.Instances.Select(s => s.Id == id ? updated : s).ToList(),
                    } };
                });
            }
            catch (SettingsValidationException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message, errors = invalid.Errors });
            }

            if (!found) return Results.NotFound(new { error = $"service '{id}' not found" });
            await host.ReconcileAsync(before!.Services, saved.Services, ct).ConfigureAwait(false);
            return Results.Ok(await host.GetAsync(id, ct).ConfigureAwait(false));
            }
            finally { ctx.Settings.ApplyGate.Release(); }
        });

        app.MapDelete("/api/services/{id}", async (
            string id, bool? purgeMemories, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            await ctx.Settings.ApplyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
            RuntimeSettings? before = null;
            var found = false;
            var saved = ctx.Settings.Update(current =>
            {
                before = current;
                found = current.Services.Instances.Any(s => s.Id == id);
                return found ? current with { Services = current.Services with
                {
                    Instances = current.Services.Instances.Where(s => s.Id != id).ToList(),
                } } : null;
            });
            if (!found) return Results.NotFound(new { error = $"service '{id}' not found" });

            // Stop the listener FIRST — its stop already flushes the final window — so no request can arrive after
            // the purge and write a new memory through the summarizer. Purging before stopping would race exactly
            // that window: the last request in flight would be summarized into a record the purge had just passed.
            await host.ReconcileAsync(before!.Services, saved.Services, ct).ConfigureAwait(false);

            var deleted = 0;
            if (purgeMemories == true)
            {
                var origin = MemoryOrigins.Service(id);
                var records = await ctx.Memory.ListAsync(
                    new MemoryQuery(Origin: origin, Limit: int.MaxValue), ct).ConfigureAwait(false);
                foreach (var record in records)
                {
                    await ctx.Memory.SoftDeleteAsync(record.RecordId, Actor.Human, ct).ConfigureAwait(false);
                    deleted++;
                }
            }

            var dir = Path.Combine(ctx.Workspace, "services", id);
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // A file still held open by a just-stopped listener is cleaned on the next startup instead.
                }
            }

            return Results.Ok(new { deletedMemories = deleted });
            }
            finally { ctx.Settings.ApplyGate.Release(); }
        });

        app.MapPost("/api/services/{id}/flush", async (
            string id, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            if (ctx.Settings.Load().Services.Instances.All(s => s.Id != id))
            {
                return Results.NotFound(new { error = $"service '{id}' not found" });
            }

            var memoryId = await host.FlushAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { memoryId });
        });

        app.MapPost("/api/services/{id}/token", async (
            string id, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            await ctx.Settings.ApplyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
            RuntimeSettings? before = null;
            var found = false;
            var token = TokenAuth.Generate();
            var saved = ctx.Settings.Update(current =>
            {
                before = current;
                found = current.Services.Instances.Any(s => s.Id == id);
                return found ? current with { Services = current.Services with
                {
                    Instances = current.Services.Instances.Select(s => s.Id == id ? s with { Token = token } : s).ToList(),
                } } : null;
            });
            if (!found) return Results.NotFound(new { error = $"service '{id}' not found" });
            await host.ReconcileAsync(before!.Services, saved.Services, ct).ConfigureAwait(false);
            return Results.Ok(new { token }); // shown once
            }
            finally { ctx.Settings.ApplyGate.Release(); }
        });
    }

    private static string GenerateId(ServicesSettings current)
    {
        var taken = current.Instances.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        string id;
        do
        {
            id = Guid.NewGuid().ToString("n")[..12];
        }
        while (!taken.Add(id));

        return id;
    }
}
