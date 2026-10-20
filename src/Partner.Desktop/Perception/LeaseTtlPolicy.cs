namespace Partner.Desktop.Perception;

/// <summary>
/// Lease TTL policy. Short leases when the target is cheap to re-observe; longer ones for exploratory or debug
/// views where re-observing is expensive; the lease always shortens as the scene gets busier (more entities,
/// bigger payloads), because the chance of the target moving grows with it.
/// </summary>
public sealed record LeaseTtlPolicy
{
    public double BaseMs { get; init; } = 15_000;

    public double FloorMs { get; init; } = 2_000;

    public double CapMs { get; init; } = 60_000;

    public double ViewBonusExploreMs { get; init; } = 5_000;

    public double ViewBonusDebugMs { get; init; } = 10_000;

    /// <summary>Entity count below which no bonus applies.</summary>
    public int EntityBonusThreshold { get; init; } = 20;

    public double EntityBonusPerUnitMs { get; init; } = 100;

    public int PayloadBonusBaselineBytes { get; init; } = 2_000;

    public double PayloadBonusPerByteMs { get; init; } = 0.5;

    public double PayloadBonusCapMs { get; init; } = 10_000;

    /// <summary>Fraction of the TTL after which the lease is <i>soft</i> expired (advisory only).</summary>
    public double SoftExpiryFraction { get; init; } = 0.6;

    public static LeaseTtlPolicy Default { get; } = new();

    public sealed record TtlDecision(double TtlMs, bool RefreshRequired, string Reason);

    /// <summary>
    /// <c>clamp(base + viewBonus + entityBonus + payloadBonus, floor, cap)</c>, then raised to at least the
    /// observed round trip (a lease shorter than one round trip is useless) and clamped again.
    /// </summary>
    public TtlDecision Compute(
        ObserveScope scope,
        int entityCount,
        int payloadBytes,
        double? observedRoundTripMs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entityCount);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);

        var viewBonus = scope switch
        {
            ObserveScope.Explore => ViewBonusExploreMs,
            ObserveScope.Debug => ViewBonusDebugMs,
            _ => 0d,
        };

        var entityBonus = Math.Max(0, entityCount - EntityBonusThreshold) * EntityBonusPerUnitMs;
        var payloadBonus = Math.Min(
            Math.Max(0, payloadBytes - PayloadBonusBaselineBytes) * PayloadBonusPerByteMs,
            PayloadBonusCapMs);

        var raw = BaseMs + viewBonus + entityBonus + payloadBonus;

        if (observedRoundTripMs is { } observed && observed > CapMs)
        {
            // Observation is slower than the longest lease we are willing to hand out: keep the cap and tell
            // the caller to refresh before acting.
            return new TtlDecision(CapMs, true, $"observed round trip {observed:0}ms exceeds the cap");
        }

        var ttl = observedRoundTripMs is { } rtt ? Math.Max(raw, rtt) : raw;
        ttl = Math.Clamp(ttl, FloorMs, CapMs);

        return new TtlDecision(ttl, false, $"base {BaseMs:0} + view {viewBonus:0} + entity {entityBonus:0} + payload {payloadBonus:0}");
    }
}
