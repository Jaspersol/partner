namespace Partner.Desktop.Perception;

/// <summary>
/// Issues and validates entity leases. Validation order is part of the contract and must not be rearranged:
/// <b>expired, generation mismatch, entity not found, digest mismatch</b>. Each step rules out a different
/// failure mode, and the reported reason makes an "action refused" diagnosable.
/// </summary>
public sealed class LeaseStore
{
    private readonly LeaseTtlPolicy _policy;
    private readonly Dictionary<string, EntityLease> _leases = new(StringComparer.Ordinal);

    public LeaseStore(LeaseTtlPolicy? policy = null)
    {
        _policy = policy ?? LeaseTtlPolicy.Default;
    }

    /// <summary>Observed round trip of the last observation, used to keep leases useful.</summary>
    public double? ObservedRoundTripMs { get; set; }

    public int Count => _leases.Count;

    public EntityLease Issue(UiEntity entity, string viewId, DateTimeOffset now, double ttlMs)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var lease = new EntityLease
        {
            EntityId = entity.EntityId,
            ViewId = viewId,
            TargetGeneration = entity.Generation,
            IdentityDigest = entity.IdentityDigest,
            IssuedAt = now,
            ExpiresAt = now.AddMilliseconds(ttlMs),
            TtlMs = ttlMs,
        };

        _leases[entity.EntityId] = lease;
        return lease;
    }

    public EntityLease? Peek(string entityId) =>
        _leases.TryGetValue(entityId, out var lease) ? lease : null;

    public LeaseValidation Validate(
        EntityLease? lease,
        string currentGeneration,
        IReadOnlyList<UiEntity> liveEntities,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentGeneration);
        ArgumentNullException.ThrowIfNull(liveEntities);

        if (lease is null)
        {
            return new LeaseValidation(LeaseRejection.EntityNotFound, null);
        }

        // 1) hard expiry is the only correctness wall.
        if (now > lease.ExpiresAt)
        {
            return new LeaseValidation(LeaseRejection.Expired, null);
        }

        // 2) a new observation invalidates every lease issued against the previous generation.
        if (!string.Equals(lease.TargetGeneration, currentGeneration, StringComparison.Ordinal))
        {
            return new LeaseValidation(LeaseRejection.GenerationMismatch, null);
        }

        // 3) the entity must still exist in the live set.
        var entity = liveEntities.FirstOrDefault(e =>
            string.Equals(e.EntityId, lease.EntityId, StringComparison.Ordinal));

        if (entity is null)
        {
            return new LeaseValidation(LeaseRejection.EntityNotFound, null);
        }

        // 4) Same id, same generation, but is it still the same window? Identity is handle + process + process
        // start time, so this catches a recycled handle (the previous window closed and Windows handed the same
        // handle to a new one) and a restarted process. Title and geometry are not identity: a window that
        // retitles itself or moves is still the window the model decided about.
        if (!string.Equals(entity.IdentityDigest, lease.IdentityDigest, StringComparison.Ordinal))
        {
            return new LeaseValidation(LeaseRejection.DigestMismatch, entity);
        }

        return new LeaseValidation(LeaseRejection.None, entity);
    }

    public int EvictExpired(DateTimeOffset now)
    {
        var expired = _leases
            .Where(pair => now > pair.Value.ExpiresAt)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expired)
        {
            _leases.Remove(key);
        }

        return expired.Count;
    }

    public bool IsSoftExpired(EntityLease lease, DateTimeOffset now) =>
        lease.IsSoftExpired(now, _policy.SoftExpiryFraction);
}
