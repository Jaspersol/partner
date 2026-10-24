using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Tests;

/// <summary>
/// "Test connection" is a second, hand-written call beside the production client. It has to follow the same
/// protocol choice, or the settings page reports a working endpoint as unreachable the moment it is Anthropic.
/// </summary>
public sealed class ModelConnectionTestEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-model-connection-tests";

    protected override string ReadinessPath => "/api/settings";

    [Fact]
    public async Task An_anthropic_endpoint_is_probed_on_the_messages_route_with_its_headers()
    {
        var port = FreePort();
        var seen = new List<(string Path, string? Key, string? Version, string? Body)>();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var provider = builder.Build();

        provider.MapPost("/v1/messages", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            seen.Add((
                context.Request.Path.Value ?? string.Empty,
                context.Request.Headers["x-api-key"].FirstOrDefault(),
                context.Request.Headers["anthropic-version"].FirstOrDefault(),
                await reader.ReadToEndAsync()));

            return Results.Json(new
            {
                type = "message",
                role = "assistant",
                content = new[] { new { type = "text", text = "pong" } },
                stop_reason = "end_turn",
                usage = new { input_tokens = 1, output_tokens = 1 },
            });
        });

        await provider.StartAsync();
        try
        {
            var saved = await _http.PutAsJsonAsync("/api/settings", new
            {
                model = new
                {
                    protocol = "anthropic",
                    baseUrl = $"http://127.0.0.1:{port}/v1",
                    model = "claude-test",
                    apiKey = "secret-key",
                    contextTokens = 8192,
                    maxTokens = 1024,
                },
            });
            saved.EnsureSuccessStatusCode();

            var response = await _http.PostAsync("/api/voice/test/model", content: null);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
            Assert.Equal(2, seen.Count);
            Assert.All(seen, call => Assert.Equal("/v1/messages", call.Path));
            Assert.All(seen, call => Assert.Equal("secret-key", call.Key));
            Assert.All(seen, call => Assert.Equal(AnthropicLlmClient.ApiVersion, call.Version));

            // The vision probe uses the base64 image source block, not OpenAI's image_url part.
            Assert.Contains("\"type\":\"image\"", seen[0].Body, StringComparison.Ordinal);
            Assert.Contains("\"media_type\":\"image/png\"", seen[0].Body, StringComparison.Ordinal);
        }
        finally
        {
            await provider.StopAsync();
        }
    }
}
