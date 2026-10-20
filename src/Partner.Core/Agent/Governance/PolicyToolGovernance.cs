using System.Text.Json;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Governance;

/// <summary>
/// Governance backed by a <see cref="ToolPolicy"/> file, with target-parameter validation. Every denial
/// carries a precise reason so the decision is auditable.
/// </summary>
public sealed class PolicyToolGovernance : IToolGovernance
{
    private readonly ToolPolicy _policy;

    public PolicyToolGovernance(ToolPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public GovernanceDecision Decide(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var entry = _policy.Find(toolName);
        if (entry is null)
        {
            return new GovernanceDecision(
                ToolDecision.Deny,
                $"'{toolName}' is not present in the tool policy (fail-closed)");
        }

        if (entry.Decision == ToolDecision.Deny)
        {
            return new GovernanceDecision(ToolDecision.Deny, $"'{toolName}' is explicitly denied by policy");
        }

        var violation = TargetParameterValidator.Validate(entry.Rules, argumentsJson);
        if (violation is not null)
        {
            return new GovernanceDecision(ToolDecision.Deny, violation);
        }

        return entry.Decision == ToolDecision.Ask
            ? new GovernanceDecision(ToolDecision.Ask, $"'{toolName}' requires approval")
            : new GovernanceDecision(ToolDecision.Allow, $"'{toolName}' is allowed by policy");
    }
}

/// <summary>
/// Validates the target of a tool call against its rules. Returns <c>null</c> when the call is acceptable,
/// otherwise a human-readable violation. Rules that cannot be checked deny the call.
/// </summary>
public static class TargetParameterValidator
{
    private static readonly string[] PathKeys = { "path", "file", "filepath", "target", "destination" };
    private static readonly string[] HostKeys = { "url", "host", "endpoint", "uri" };
    private static readonly string[] CommandKeys = { "command", "cmd", "script" };
    private static readonly string[] WindowKeys = { "windowtitle", "title", "window" };

    /// <summary>Shell metacharacters that make a "single command" claim false.</summary>
    private static readonly char[] ShellMetacharacters = { ';', '|', '&', '>', '<', '`', '\n', '\r', '$' };

    public static string? Validate(TargetParamRules? rules, string? argumentsJson)
    {
        if (rules is null)
        {
            return null;
        }

        var args = ParseArguments(argumentsJson);

        if (rules.AllowedPathPrefixes is { Count: > 0 })
        {
            var value = Find(args, PathKeys);
            if (value is null)
            {
                return "target path is missing but the policy requires a path inside an allowed prefix";
            }

            var full = SafeFullPath(value);
            if (full is null)
            {
                return $"target path '{value}' is not a valid path";
            }

            var allowed = rules.AllowedPathPrefixes.Any(prefix =>
                full.StartsWith(EnsureTrailingSeparator(prefix), StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, prefix.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

            if (!allowed)
            {
                return $"target path '{full}' is outside the allowed prefixes";
            }
        }

        if (rules.AllowedHosts is { Count: > 0 })
        {
            var value = Find(args, HostKeys);
            if (value is null)
            {
                return "target host is missing but the policy requires an allowed host";
            }

            var host = ExtractHost(value);
            if (host is null)
            {
                return $"target '{value}' is not a valid URL or host";
            }

            var allowed = rules.AllowedHosts.Any(candidate =>
                string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
            {
                return $"target host '{host}' is not in the allowed hosts";
            }
        }

        if (rules.AllowedCommands is { Count: > 0 })
        {
            var value = Find(args, CommandKeys);
            if (value is null)
            {
                return "target command is missing but the policy requires an allowed command";
            }

            if (value.IndexOfAny(ShellMetacharacters) >= 0)
            {
                return $"target command '{value}' contains shell metacharacters";
            }

            var head = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (!rules.AllowedCommands.Any(c => string.Equals(c, head, StringComparison.OrdinalIgnoreCase)))
            {
                return $"target command '{head}' is not in the allowed commands";
            }
        }

        if (rules.RequiredWindowTitles is { Count: > 0 })
        {
            var value = Find(args, WindowKeys);
            if (value is null)
            {
                return "target window title is missing but the policy requires a matching window";
            }

            if (!rules.RequiredWindowTitles.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return $"target window '{value}' does not match the required titles";
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseArguments(string? argumentsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }
        catch (JsonException)
        {
            // Unparseable arguments cannot be validated; the caller's rules will deny on the missing field.
        }

        return result;
    }

    private static string? Find(Dictionary<string, string> args, string[] keys)
    {
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? SafeFullPath(string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string EnsureTrailingSeparator(string prefix) =>
        prefix.EndsWith(Path.DirectorySeparatorChar) ? prefix : prefix + Path.DirectorySeparatorChar;

    private static string? ExtractHost(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        // A bare host is acceptable.
        return value.Contains('/') || value.Contains(' ') ? null : value;
    }
}
