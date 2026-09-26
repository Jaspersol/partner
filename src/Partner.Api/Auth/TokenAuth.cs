using System.Security.Cryptography;
using System.Text;

namespace Partner.Api.Auth;

/// <summary>
/// Every endpoint requires a per-launch bearer token, enforced by middleware rather than per-endpoint
/// attributes — a newly added endpoint is therefore protected by construction, not by remembering to
/// annotate it. There are no exempt endpoints, including health.
/// </summary>
public static class TokenAuth
{
    /// <summary>Environment variable carrying the token. Deliberately not a command-line argument: argv is
    /// readable by every process on the machine, the environment of a child is not.</summary>
    public const string EnvVar = "PARTNER_API_TOKEN";

    /// <summary>Resolves the token from the environment, generating one when absent so a developer running
    /// the API by hand is never left with an unauthenticated endpoint.</summary>
    public static (string Token, bool Generated) Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(configured) ? (Generate(), true) : (configured, false);
    }

    /// <summary>A 256-bit URL-safe token.</summary>
    public static string Generate()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static void UseTokenAuth(this WebApplication app, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var expected = Encoding.UTF8.GetBytes("Bearer " + token);

        app.Use(async (context, next) =>
        {
            var provided = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());

            if (!CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next().ConfigureAwait(false);
        });
    }
}
