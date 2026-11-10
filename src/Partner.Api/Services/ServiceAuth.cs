using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Services;

/// <summary>
/// Authentication and protocol-shaped error bodies for one service instance. The client's auth headers are never
/// forwarded upstream — the proxy replaces them with the upstream key.
/// </summary>
internal static class ServiceAuth
{
    /// <summary>Accepts either a bearer token or an <c>x-api-key</c>, so an OpenAI SDK and an Anthropic SDK both work.</summary>
    public static bool Authorized(HttpRequest request, string token)
    {
        // Two empty spans compare equal, so an instance with no token would accept a bare "Bearer " header.
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(token);

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]), expected))
        {
            return true;
        }

        if (request.Headers.TryGetValue("x-api-key", out var key)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.ToString()), expected))
        {
            return true;
        }

        return false;
    }

    public static Task Unauthorized(HttpResponse response, string protocol) =>
        WriteAsync(response, StatusCodes.Status401Unauthorized, protocol,
            LlmProtocols.Normalize(protocol) == LlmProtocols.Anthropic ? "authentication_error" : "invalid_api_key",
            "invalid api key");

    /// <summary>
    /// Writes an error body in the shape of the protocol the client used, so an SDK can parse it. OpenAI wraps
    /// the error in <c>error</c>; Anthropic uses a top-level <c>type</c> plus a nested <c>error</c>.
    /// </summary>
    public static async Task WriteAsync(HttpResponse response, int status, string protocol, string type, string message)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";

        object body = LlmProtocols.Normalize(protocol) == LlmProtocols.Anthropic
            ? new { type = "error", error = new { type, message } }
            : new { error = new { message, type = "invalid_request_error", code = type } };

        await response.WriteAsync(JsonSerializer.Serialize(body)).ConfigureAwait(false);
    }
}
