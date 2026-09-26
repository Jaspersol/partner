using System.Text.Json;
using System.Text.Json.Serialization;

namespace Partner.Core.Manifest;

/// <summary>Capability level, ordered from weakest to strongest (design doc §2.6).</summary>
public enum CapabilityLevel
{
    /// <summary>Not proven by any smoke check.</summary>
    NotSupported = 0,

    /// <summary>Partially proven.</summary>
    Covered = 1,

    /// <summary>Fully proven by smoke checks that actually ran.</summary>
    ProductSupported = 2,
}

public static class CapabilityLevels
{
    public static string ToWire(this CapabilityLevel level) => level switch
    {
        CapabilityLevel.ProductSupported => "product-supported",
        CapabilityLevel.Covered => "covered",
        _ => "not-supported",
    };

    public static bool TryParse(string? wire, out CapabilityLevel level)
    {
        switch (wire)
        {
            case "product-supported":
                level = CapabilityLevel.ProductSupported;
                return true;
            case "covered":
                level = CapabilityLevel.Covered;
                return true;
            case "not-supported":
                level = CapabilityLevel.NotSupported;
                return true;
            default:
                level = CapabilityLevel.NotSupported;
                return false;
        }
    }
}

/// <summary>Serializes <see cref="CapabilityLevel"/> in the wire form the design doc uses (kebab-case).</summary>
public sealed class CapabilityLevelConverter : JsonConverter<CapabilityLevel>
{
    public override CapabilityLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!CapabilityLevels.TryParse(raw, out var level))
        {
            throw new JsonException($"Unknown capability level '{raw}' (expected not-supported / covered / product-supported).");
        }

        return level;
    }

    public override void Write(Utf8JsonWriter writer, CapabilityLevel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWire());
}

public sealed record CapabilityEntry
{
    public required string Id { get; init; }

    public required string Owner { get; init; }

    public required CapabilityLevel Level { get; init; }

    public required IReadOnlyList<CapabilityCheckEvidence> Evidence { get; init; }
}

public sealed record CapabilityCheckEvidence(string Check, bool Ran, bool Passed);

public sealed record CapabilityManifest
{
    public required IReadOnlyList<CapabilityEntry> Capabilities { get; init; }

    public required int ChecksFailed { get; init; }

    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new CapabilityLevelConverter() },
    };

    /// <summary>
    /// Compares a previously committed baseline against what smoke just observed.
    /// Any capability that disappeared or dropped a level is a regression (design doc Phase 0).
    /// </summary>
    public static ManifestGateReport Compare(CapabilityManifest baseline, CapabilityManifest current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var now = current.Capabilities.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        var regressions = new List<string>();
        var improvements = new List<string>();

        foreach (var before in baseline.Capabilities)
        {
            if (!now.TryGetValue(before.Id, out var after))
            {
                regressions.Add($"missing: {before.Id} (baseline {before.Level.ToWire()})");
                continue;
            }

            if (after.Level < before.Level)
            {
                regressions.Add($"downgraded: {before.Id} {before.Level.ToWire()} → {after.Level.ToWire()}");
            }
            else if (after.Level > before.Level)
            {
                improvements.Add($"improved: {before.Id} {before.Level.ToWire()} → {after.Level.ToWire()}");
            }
        }

        if (current.ChecksFailed > 0)
        {
            regressions.Add($"smoke checks failed: {current.ChecksFailed}");
        }

        var baselineIds = baseline.Capabilities.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var added = current.Capabilities
            .Where(c => !baselineIds.Contains(c.Id))
            .Select(c => $"added: {c.Id} {c.Level.ToWire()}")
            .ToList();

        return new ManifestGateReport(regressions, improvements, added);
    }
}

public sealed record ManifestGateReport(
    IReadOnlyList<string> Regressions,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> Added)
{
    public bool Ok => Regressions.Count == 0;
}
