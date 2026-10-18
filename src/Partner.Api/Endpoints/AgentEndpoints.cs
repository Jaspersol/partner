using Partner.Api.Agents;
using Partner.Api.Settings;

namespace Partner.Api.Endpoints;

/// <summary>
/// The prompts page's API: the agents section as one document, and the sub-agents' jobs.
/// <para>
/// The agents section has its own endpoint rather than riding on <c>PUT /api/settings</c>. That one takes the
/// whole settings document, and the settings page refills its form from the response; two pages each writing the
/// whole document is how one of them ends up writing the other's section back to its defaults.
/// </para>
/// </summary>
public static class AgentEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/agents", (ApiContext ctx) => Results.Ok(AgentsView.From(ctx.Settings.Load())));

        app.MapPut("/api/agents", async (AgentsSettings incoming, ApiContext ctx, ConsumerHost consumer) =>
        {
            var before = ctx.Settings.Load();

            // A new sub-agent arrives without an id; the server names it, so two clients cannot collide.
            var named = incoming with
            {
                SubAgents = incoming.SubAgents
                    .Select(agent => string.IsNullOrWhiteSpace(agent.Id)
                        ? agent with { Id = "agent-" + Guid.NewGuid().ToString("n")[..6] }
                        : agent)
                    .ToList(),
            };

            RuntimeSettings stored;
            try
            {
                stored = ctx.Settings.Save(before with { Agents = named });
            }
            catch (SettingsValidationException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message, errors = invalid.Errors });
            }

            // Only what changes the main agent's tool table restarts the consumer; a prompt, a name, a round
            // count or a sub-agent's model is read when a turn or a job starts.
            if (AgentRouting.ToolTableDiffers(before, stored))
            {
                var (config, reason) = ModelConfig.Resolve(stored);
                await consumer.ReconfigureAsync(config, reason).ConfigureAwait(false);
            }

            return Results.Ok(new { agents = AgentsView.From(stored), consumer = consumer.State });
        });

        app.MapGet("/api/agent-jobs", (ConsumerHost consumer) => Results.Ok(new { jobs = consumer.Agents.Jobs() }));

        // The job's own conversation, in the shape /api/session uses, minus the turn/step bookkeeping.
        app.MapGet("/api/agent-jobs/{id}/session", async (string id, ConsumerHost consumer, CancellationToken ct) =>
        {
            var events = await consumer.Agents.ReadSessionAsync(id, ct).ConfigureAwait(false);
            if (events is null)
            {
                return Results.NotFound(new { error = $"没有任务 {id} 的会话" });
            }

            var control = new[]
            {
                Partner.Core.Contracts.SessionEventKind.TurnStart,
                Partner.Core.Contracts.SessionEventKind.StepStart,
                Partner.Core.Contracts.SessionEventKind.StepEnd,
                Partner.Core.Contracts.SessionEventKind.TurnEnd,
            };

            return Results.Ok(new
            {
                items = events.Where(e => !control.Contains(e.Kind)).Select(e => new
                {
                    seq = e.Seq,
                    at = e.At,
                    kind = e.Kind.ToString(),
                    source = e.Source.ToString(),
                    body = e.Body,
                    meta = e.Meta,
                }).ToList(),
            });
        });

        app.MapPost("/api/agent-jobs/{id}/cancel", (string id, ConsumerHost consumer) =>
            consumer.Agents.Cancel(id)
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { error = $"任务 {id} 不存在，或已经结束" }));
    }
}
