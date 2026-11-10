using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Partner.Api.Settings;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Services;

/// <summary>
/// One service: its own <see cref="WebApplication"/> bound to a single loopback port. Building a separate host
/// per instance is what lets each speak a different protocol on a different port without a shared router.
/// </summary>
internal sealed class ServiceInstance : IAsyncDisposable
{
    private readonly string _id;
    private readonly ServiceCounters _counters;
    private readonly PendingRounds _rounds;
    private readonly ServiceSummarizer _summarizer;
    private readonly ServiceProxy _proxy;
    private ServiceSettings _settings;
    private WebApplication? _app;
    private bool _running;
    private string? _error;

    public ServiceInstance(ServiceSettings settings, ApiContext ctx, HttpClient http, string workspace, TimeProvider clock)
    {
        _id = settings.Id;
        _settings = settings;
        _counters = new ServiceCounters();
        var dir = Path.Combine(workspace, "services", _id);
        _rounds = new PendingRounds(dir, clock, recoverCt => PendingRounds.RecoverWindowSeqAsync(ctx.Memory, _id, recoverCt));
        _summarizer = new ServiceSummarizer(() => Settings, ctx, http, _rounds, clock);
        _proxy = new ServiceProxy(() => Settings, ctx, http, _rounds, clock, _counters);
    }

    public string Id => _id;

    /// <summary>Read live so a hot config edit takes effect on the very next request.</summary>
    public ServiceSettings Settings => Volatile.Read(ref _settings);

    public void UpdateSettings(ServiceSettings settings) => Volatile.Write(ref _settings, settings);

    public async Task StartAsync(CancellationToken ct)
    {
        var settings = Settings;
        var protocol = LlmProtocols.Normalize(settings.Protocol);

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{settings.Port}");

            var app = builder.Build();
            _app = app;

            // One auth gate for every route, including /v1/models. Reads the live token so a rotation needs no
            // restart. The client's auth header is never forwarded to the upstream.
            app.Use(async (context, next) =>
            {
                if (!ServiceAuth.Authorized(context.Request, Settings.Token))
                {
                    await ServiceAuth.Unauthorized(context.Response, protocol).ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });

            app.MapGet("/v1/models", async (HttpContext context) =>
            {
                var current = Settings;
                await context.Response.WriteAsJsonAsync(new
                {
                    @object = "list",
                    data = new[]
                    {
                        new { id = current.Model, @object = "model", context_length = current.ContextTokens },
                    },
                }).ConfigureAwait(false);
            });

            if (protocol == LlmProtocols.Anthropic)
            {
                app.MapPost("/v1/messages", (HttpContext context) => _proxy.HandleAsync(context));
            }
            else
            {
                app.MapPost("/v1/chat/completions", (HttpContext context) => _proxy.HandleAsync(context));
            }

            app.MapFallback((HttpContext context) => ServiceAuth.WriteAsync(
                context.Response,
                StatusCodes.Status404NotFound,
                protocol,
                protocol == LlmProtocols.Anthropic ? "not_found_error" : "invalid_request_error",
                "unknown route"));

            await app.StartAsync(ct).ConfigureAwait(false);
            _running = true;
            _error = null;
        }
        catch (Exception error)
        {
            // A busy port or a bad bind must not take the main host down: the state reports the error instead.
            _running = false;
            _error = error.Message;
            if (_app is not null)
            {
                try
                {
                    await _app.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A half-started host is best-effort to clean up.
                }
                _app = null;
            }
        }
    }

    /// <summary>
    /// Stops the listener, flushing pending rounds first when asked (stop / disable / delete).
    /// <para>
    /// The flush is two-phase: the model call is bounded by <paramref name="ct"/>; if that budget runs out, the
    /// window is still written — with the template and no cancellation token — because a cancelled write would
    /// leave the buffer on disk and shutdown would silently lose it.
    /// </para>
    /// </summary>
    public async Task StopAsync(bool flush, bool templateOnly, CancellationToken ct)
    {
        if (flush)
        {
            if (!templateOnly)
            {
                try
                {
                    await _summarizer.FlushAsync(force: true, templateOnly: false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    templateOnly = true;
                }
                catch
                {
                    // Shutdown must not fail over an unsent summary; the template pass below still runs.
                    templateOnly = true;
                }
            }

            if (templateOnly)
            {
                try
                {
                    await _summarizer.FlushAsync(force: true, templateOnly: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Nothing left to do without a model; the buffer stays for the next start.
                }
            }
        }

        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Already stopped or never bound.
            }
        }

        _running = false;
    }

    public Task<string?> FlushAsync(bool force, bool templateOnly, CancellationToken ct) =>
        _summarizer.FlushAsync(force, templateOnly, ct);

    public async Task<ServiceState> StateAsync(CancellationToken ct)
    {
        var view = await _rounds.ReadAsync(ct).ConfigureAwait(false);
        return new ServiceState(
            _running,
            _error ?? _counters.Error,
            Settings.Port,
            _counters.Requests,
            _counters.LastActivityAt,
            view.PendingCount,
            view.LastFlushAt,
            view.LastMemoryId);
    }

    public async ValueTask DisposeAsync()
    {
        await _summarizer.DisposeAsync().ConfigureAwait(false);

        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Already stopped.
            }

            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
