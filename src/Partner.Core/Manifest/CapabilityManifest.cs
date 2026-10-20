using System.Text.Json;
using System.Text.Json.Serialization;

namespace Partner.Core.Manifest;

/// <summary>Capability level, ordered from weakest to strongest.</summary>
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

/// <summary>Serializes <see cref="CapabilityLevel"/> in the kebab-case wire form.</summary>
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

public sealed record CapabilityCheckEvidence(string Check, bool Ran, bool Passed, bool Skipped = false);

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
    /// Any capability that disappeared or dropped a level is a regression.
    /// </summary>
    public static ManifestGateReport Compare(CapabilityManifest baseline, CapabilityManifest current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var now = current.Capabilities.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        var regressions = new List<string>();
        var improvements = new List<string>();
        var notVerified = new List<string>();

        foreach (var before in baseline.Capabilities)
        {
            if (!now.TryGetValue(before.Id, out var after))
            {
                regressions.Add($"missing: {before.Id} (baseline {before.Level.ToWire()})");
                continue;
            }

            if (after.Level < before.Level)
            {
                // A drop caused only by checks that were opted out is not a regression: it is the run saying
                // "I did not check this". Reporting it as a regression makes the gate untrustworthy, because a
                // busy desktop then produces a red build unrelated to the code. The capability is named as not
                // verified instead; the regression list stays for evidence that failed or went missing.
                //
                // Every clause matters: a check that ran and failed is evidence however many others were
                // skipped, and a check that neither ran nor was skipped has vanished, which is worse.
                var failed = after.Evidence.Where(e => e.Ran && !e.Passed).ToArray();
                var absent = after.Evidence.Where(e => !e.Ran && !e.Skipped).ToArray();
                var skipped = after.Evidence.Count(e => e.Skipped);

                if (failed.Length == 0 && absent.Length == 0 && skipped > 0)
                {
                    notVerified.Add($"not verified: {before.Id} ({skipped} check(s) skipped this run; baseline {before.Level.ToWire()})");
                }
                else
                {
                    regressions.Add($"downgraded: {before.Id} {before.Level.ToWire()} → {after.Level.ToWire()}");
                }
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

        return new ManifestGateReport(regressions, improvements, added, notVerified);
    }
}

public sealed record ManifestGateReport(
    IReadOnlyList<string> Regressions,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> Added,
    /// <summary>
    /// Capabilities whose baseline level was not reached because their checks were opted out of this run
    /// (<c>PARTNER_DESKTOP_LIVE</c> unset). Reported, never fatal — but also never counted as passed.
    /// </summary>
    IReadOnlyList<string> NotVerified)
{
    public bool Ok => Regressions.Count == 0;
}
