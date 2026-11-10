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
            var before = ctx.Settings.Load();
            var created = body with
            {
                Id = GenerateId(before.Services),
                Token = TokenAuth.Generate(),
                Protocol = LlmProtocols.Normalize(body.Protocol),
                ContextTokens = body.ContextTokens > 0 ? body.ContextTokens : before.Model.ContextTokens,
            };

            var services = before.Services with
            {
                Instances = before.Services.Instances.Append(created).ToList(),
            };

            RuntimeSettings saved;
            try
            {
                saved = ctx.Settings.Save(before with { Services = services });
            }
            catch (SettingsValidationException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message, errors = invalid.Errors });
            }

            await host.ReconcileAsync(before.Services, saved.Services, ct).ConfigureAwait(false);

            var persisted = saved.Services.Instances.First(s => s.Id == created.Id);
            var state = await host.GetAsync(created.Id, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                settings = ServiceSettingsView.From(persisted),
                state = state?.State,
                token = created.Token, // shown once, never again
            });
        });

        app.MapPut("/api/services/{id}", async (
            string id, ServiceSettings body, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            var before = ctx.Settings.Load();
            if (before.Services.Instances.All(s => s.Id != id))
            {
                return Results.NotFound(new { error = $"service '{id}' not found" });
            }

            // Id is immutable; an empty token / apiKey means "keep the stored one" (resolved in SettingsStore.Save).
            var updated = body with { Id = id, Protocol = LlmProtocols.Normalize(body.Protocol) };
            var services = before.Services with
            {
                Instances = before.Services.Instances
                    .Select(s => s.Id == id ? updated : s)
                    .ToList(),
            };

            RuntimeSettings saved;
            try
            {
                saved = ctx.Settings.Save(before with { Services = services });
            }
            catch (SettingsValidationException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message, errors = invalid.Errors });
            }

            await host.ReconcileAsync(before.Services, saved.Services, ct).ConfigureAwait(false);
            return Results.Ok(await host.GetAsync(id, ct).ConfigureAwait(false));
        });

        app.MapDelete("/api/services/{id}", async (
            string id, bool? purgeMemories, ApiContext ctx, ServiceHost host, CancellationToken ct) =>
        {
            var before = ctx.Settings.Load();
            if (before.Services.Instances.All(s => s.Id != id))
            {
                return Results.NotFound(new { error = $"service '{id}' not found" });
            }

            var services = before.Services with
            {
                Instances = before.Services.Instances.Where(s => s.Id != id).ToList(),
            };
            var saved = ctx.Settings.Save(before with { Services = services });

            // Stop the listener FIRST — its stop already flushes the final window — so no request can arrive after
            // the purge and write a new memory through the summarizer. Purging before stopping would race exactly
            // that window: the last request in flight would be summarized into a record the purge had just passed.
            await host.ReconcileAsync(before.Services, saved.Services, ct).ConfigureAwait(false);

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
            var before = ctx.Settings.Load();
            if (before.Services.Instances.All(s => s.Id != id))
            {
                return Results.NotFound(new { error = $"service '{id}' not found" });
            }

            var token = TokenAuth.Generate();
            var services = before.Services with
            {
                Instances = before.Services.Instances
                    .Select(s => s.Id == id ? s with { Token = token } : s)
                    .ToList(),
            };
            var saved = ctx.Settings.Save(before with { Services = services });
            await host.ReconcileAsync(before.Services, saved.Services, ct).ConfigureAwait(false);
            return Results.Ok(new { token }); // shown once
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
