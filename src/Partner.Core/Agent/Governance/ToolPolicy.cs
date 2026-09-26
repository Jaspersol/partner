using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Agent.Governance;

/// <summary>
/// Per-tool constraints on the <b>target</b> the tool would act on (design doc §1.1 <c>ToolGovernanceSpec.target_param</c>).
/// A rule that is present but cannot be checked is a denial, never a pass (fail-closed).
/// </summary>
public sealed record TargetParamRules
{
    /// <summary>Absolute path prefixes the target path must fall under. <c>${workspace}</c> is expanded at load time.</summary>
    public IReadOnlyList<string>? AllowedPathPrefixes { get; init; }

    /// <summary>Hosts the target URL may point at. A subdomain of an allowed host is allowed.</summary>
    public IReadOnlyList<string>? AllowedHosts { get; init; }

    /// <summary>Allowed leading command tokens.</summary>
    public IReadOnlyList<string>? AllowedCommands { get; init; }

    /// <summary>Window titles the target window must match (case-insensitive substring).</summary>
    public IReadOnlyList<string>? RequiredWindowTitles { get; init; }
}

public sealed record ToolPolicyEntry
{
    public required string ToolName { get; init; }

    public required ToolDecision Decision { get; init; }

    public TargetParamRules? Rules { get; init; }
}

/// <summary>
/// The tool governance policy (design doc §2.9.6 / §8.5 gap A). Precedence is fixed and documented:
/// <c>deny</c> &gt; <c>ask</c> &gt; <c>allow</c> &gt; <b>not listed = deny</b>.
/// </summary>
public sealed record ToolPolicy
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public required IReadOnlyDictionary<string, ToolPolicyEntry> Entries { get; init; }

    /// <summary>An empty policy denies everything.</summary>
    public static ToolPolicy Empty { get; } = new()
    {
        Entries = new Dictionary<string, ToolPolicyEntry>(StringComparer.Ordinal),
    };

    public ToolPolicyEntry? Find(string toolName) =>
        toolName is not null && Entries.TryGetValue(toolName, out var entry) ? entry : null;

    public static ToolPolicy Load(string path, string? workspaceDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path), workspaceDirectory);
    }

    /// <summary>
    /// Parses a policy document. Malformed content fails loudly: a silently empty policy would deny every
    /// tool and look like a product bug rather than a configuration error.
    /// </summary>
    public static ToolPolicy Parse(string json, string? workspaceDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var document = JsonSerializer.Deserialize<PolicyDocument>(json, Json)
            ?? throw new ArgumentException("Policy document is empty.", nameof(json));

        if (document.Tools is null || document.Tools.Count == 0)
        {
            return Empty;
        }

        var entries = new Dictionary<string, ToolPolicyEntry>(StringComparer.Ordinal);

        foreach (var (toolName, raw) in document.Tools)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                throw new ArgumentException("Policy contains a tool entry with an empty name.", nameof(json));
            }

            if (raw is null)
            {
                throw new ArgumentException($"Policy entry '{toolName}' is null.", nameof(json));
            }

            if (!Enum.TryParse<ToolDecision>(raw.Decision, ignoreCase: true, out var decision))
            {
                throw new ArgumentException(
                    $"Policy entry '{toolName}' has unknown decision '{raw.Decision}' "
                    + "(expected allow / ask / deny).", nameof(json));
            }

            entries[toolName] = new ToolPolicyEntry
            {
                ToolName = toolName,
                Decision = decision,
                Rules = raw.Rules is null ? null : Expand(raw.Rules, toolName, workspaceDirectory),
            };
        }

        return new ToolPolicy { Entries = entries };
    }

    private static TargetParamRules Expand(TargetParamRules rules, string toolName, string? workspaceDirectory)
    {
        if (rules.AllowedPathPrefixes is null)
        {
            return rules;
        }

        var expanded = new List<string>(rules.AllowedPathPrefixes.Count);
        foreach (var prefix in rules.AllowedPathPrefixes)
        {
            if (prefix.Contains("${workspace}", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(workspaceDirectory))
                {
                    throw new ArgumentException(
                        $"Policy entry '{toolName}' uses ${{workspace}} but no workspace directory was supplied.");
                }

                expanded.Add(Path.GetFullPath(prefix.Replace(
                    "${workspace}", workspaceDirectory, StringComparison.Ordinal)));
            }
            else
            {
                expanded.Add(Path.GetFullPath(prefix));
            }
        }

        return rules with { AllowedPathPrefixes = expanded };
    }

    private sealed record PolicyDocument
    {
        public int Version { get; init; } = 1;

        public Dictionary<string, RawEntry>? Tools { get; init; }
    }

    private sealed record RawEntry
    {
        public string Decision { get; init; } = string.Empty;

        public TargetParamRules? Rules { get; init; }
    }
}
