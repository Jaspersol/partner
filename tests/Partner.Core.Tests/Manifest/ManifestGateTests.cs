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

    /// <summary>
    /// The two cases that are easy to confuse, and the reason they are separated at all.
    /// <para>
    /// A capability that dropped because its checks <em>failed</em> is a regression. A capability that dropped
    /// because its checks were <em>opted out of</em> is a run that did not look — neither a pass nor a
    /// regression. Reporting the second as the first is what makes a gate untrustworthy: a developer on a busy
    /// desktop gets red builds that have nothing to do with the code, and then red stops meaning anything.
    /// </para>
    /// </summary>
    private static CapabilityManifest WithEvidence(
        string id,
        CapabilityLevel level,
        params CapabilityCheckEvidence[] evidence) =>
        new()
        {
            ChecksFailed = 0,
            Capabilities = new List<CapabilityEntry>
            {
                new() { Id = id, Owner = "test", Level = level, Evidence = evidence },
            },
        };

    [Fact]
    public void A_drop_caused_only_by_skipped_checks_is_reported_as_not_verified_not_as_a_regression()
    {
        var baseline = WithEvidence(
            "desktop.live",
            CapabilityLevel.ProductSupported,
            new CapabilityCheckEvidence("ran", true, true),
            new CapabilityCheckEvidence("opt-in", true, true));

        var current = WithEvidence(
            "desktop.live",
            CapabilityLevel.Covered,
            new CapabilityCheckEvidence("ran", true, true),
            new CapabilityCheckEvidence("opt-in", Ran: false, Passed: false, Skipped: true));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.True(report.Ok);
        Assert.Empty(report.Regressions);
        Assert.Contains(report.NotVerified, n => n.Contains("desktop.live") && n.Contains("1 check(s) skipped"));
    }

    [Fact]
    public void A_drop_beside_skipped_checks_is_still_a_regression()
    {
        // The excuse is for checks that did not run. A check that ran and failed is evidence, and it is
        // reported — otherwise "partly opted out" would become a way to hide a real one.
        var baseline = WithEvidence(
            "desktop.live",
            CapabilityLevel.ProductSupported,
            new CapabilityCheckEvidence("ran", true, true),
            new CapabilityCheckEvidence("opt-in", true, true));

        var current = WithEvidence(
            "desktop.live",
            CapabilityLevel.Covered,
            new CapabilityCheckEvidence("ran", true, false),
            new CapabilityCheckEvidence("opt-in", Ran: false, Passed: false, Skipped: true));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.False(report.Ok);
        Assert.Empty(report.NotVerified);
        Assert.Contains(report.Regressions, r => r.Contains("downgraded: desktop.live"));
    }

    [Fact]
    public void A_check_that_neither_ran_nor_was_skipped_is_not_excused()
    {
        // Nothing was skipped and the level still dropped: the checks went missing, which is a regression.
        var baseline = WithEvidence("desktop.live", CapabilityLevel.ProductSupported, new CapabilityCheckEvidence("ran", true, true));
        var current = WithEvidence(
            "desktop.live",
            CapabilityLevel.Covered,
            new CapabilityCheckEvidence("ran", true, true),
            new CapabilityCheckEvidence("vanished", Ran: false, Passed: false));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.False(report.Ok);
        Assert.Contains(report.Regressions, r => r.Contains("downgraded: desktop.live"));
    }

    [Fact]
    public void A_skipped_check_is_not_counted_as_evidence_of_a_pass()
    {
        // The other half of the rule: a capability whose every check was skipped reports nothing at all, so a
        // green run cannot be read as "proven" for something nobody looked at.
        var baseline = WithEvidence("desktop.live", CapabilityLevel.ProductSupported, new CapabilityCheckEvidence("opt-in", true, true));
        var current = WithEvidence(
            "desktop.live",
            CapabilityLevel.NotSupported,
            new CapabilityCheckEvidence("opt-in", Ran: false, Passed: false, Skipped: true));

        var report = CapabilityManifest.Compare(baseline, current);

        Assert.True(report.Ok);
        Assert.Contains(report.NotVerified, n => n.Contains("desktop.live"));
    }
}
