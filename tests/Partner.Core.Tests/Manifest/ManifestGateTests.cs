using System.Text.Json;
using Partner.Core.Manifest;

namespace Partner.Core.Tests.Manifest;

/// <summary>
/// The Phase 0 gate: a capability that disappears or drops a level must fail the build
/// (design doc Phase 0 acceptance).
/// </summary>
public sealed class ManifestGateTests
{
    private static CapabilityManifest Build(params (string Id, CapabilityLevel Level)[] entries) =>
        new()
        {
            ChecksFailed = 0,
            Capabilities = entries
                .Select(e => new CapabilityEntry
                {
                    Id = e.Id,
                    Owner = "test",
                    Level = e.Level,
                    Evidence = new[] { new CapabilityCheckEvidence($"{e.Id}.check", true, true) },
                })
                .ToList(),
        };

    [Fact]
    public void Identical_manifests_pass()
    {
        var baseline = Build(("a", CapabilityLevel.ProductSupported), ("b", CapabilityLevel.Covered));
        var current = Build(("a", CapabilityLevel.ProductSupported), ("b", CapabilityLevel.Covered));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.True(report.Ok);
        Assert.Empty(report.Regressions);
    }

    [Fact]
    public void Downgrade_is_a_regression()
    {
        var baseline = Build(("a", CapabilityLevel.ProductSupported));
        var current = Build(("a", CapabilityLevel.Covered));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.False(report.Ok);
        Assert.Contains(report.Regressions, r => r.Contains("downgraded") && r.Contains("product-supported → covered"));
    }

    [Fact]
    public void Missing_capability_is_a_regression()
    {
        var baseline = Build(("a", CapabilityLevel.Covered), ("gone", CapabilityLevel.ProductSupported));
        var current = Build(("a", CapabilityLevel.Covered));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.False(report.Ok);
        Assert.Contains(report.Regressions, r => r.StartsWith("missing: gone", StringComparison.Ordinal));
    }

    [Fact]
    public void Improvement_is_reported_but_does_not_fail()
    {
        var baseline = Build(("a", CapabilityLevel.Covered));
        var current = Build(("a", CapabilityLevel.ProductSupported));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.True(report.Ok);
        Assert.Single(report.Improvements);
    }

    [Fact]
    public void New_capability_is_reported_but_does_not_fail()
    {
        var baseline = Build(("a", CapabilityLevel.ProductSupported));
        var current = Build(("a", CapabilityLevel.ProductSupported), ("new", CapabilityLevel.Covered));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.True(report.Ok);
        Assert.Contains(report.Added, a => a.StartsWith("added: new", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_checks_always_fail_the_gate()
    {
        var baseline = Build(("a", CapabilityLevel.ProductSupported));
        var current = Build(("a", CapabilityLevel.ProductSupported)) with { ChecksFailed = 2 };

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.False(report.Ok);
        Assert.Contains(report.Regressions, r => r.Contains("smoke checks failed: 2"));
    }

    [Fact]
    public void Level_wire_format_round_trips()
    {
        foreach (var level in Enum.GetValues<CapabilityLevel>())
        {
            Assert.True(CapabilityLevels.TryParse(level.ToWire(), out var parsed));
            Assert.Equal(level, parsed);
        }

        Assert.False(CapabilityLevels.TryParse("bogus", out _));
        Assert.False(CapabilityLevels.TryParse(null, out _));
    }

    [Fact]
    public void Level_survives_a_json_round_trip_in_wire_form()
    {
        var manifest = Build(("a", CapabilityLevel.ProductSupported), ("b", CapabilityLevel.NotSupported));

        var json = JsonSerializer.Serialize(manifest, CapabilityManifest.Json);
        Assert.Contains("\"product-supported\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductSupported", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<CapabilityManifest>(json, CapabilityManifest.Json);
        Assert.NotNull(back);
        Assert.Equal(manifest.Capabilities.Select(c => c.Level), back!.Capabilities.Select(c => c.Level));
    }

    [Fact]
    public void Unknown_level_in_a_baseline_is_rejected()
    {
        const string json =
            "{\"checksFailed\":0,\"capabilities\":[{\"id\":\"a\",\"owner\":\"o\",\"level\":\"great\",\"evidence\":[]}]}";

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<CapabilityManifest>(json, CapabilityManifest.Json));
    }
}
