using Partner.Desktop.Capture;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tests;

/// <summary>Lease TTL policy: the reference numbers are normative.</summary>
public sealed class LeaseTtlPolicyTests
{
    private static readonly LeaseTtlPolicy Policy = new();

    [Fact]
    public void Base_case_is_the_action_bonus_free_value()
    {
        var decision = Policy.Compute(ObserveScope.Action, entityCount: 0, payloadBytes: 0);

        Assert.Equal(15_000, decision.TtlMs);
        Assert.False(decision.RefreshRequired);
    }

    [Fact]
    public void View_scope_adds_its_bonus()
    {
        Assert.Equal(15_000, Policy.Compute(ObserveScope.Action, 0, 0).TtlMs);
        Assert.Equal(20_000, Policy.Compute(ObserveScope.Explore, 0, 0).TtlMs);
        Assert.Equal(25_000, Policy.Compute(ObserveScope.Debug, 0, 0).TtlMs);
    }

    [Fact]
    public void Entity_bonus_only_applies_above_the_threshold()
    {
        Assert.Equal(15_000, Policy.Compute(ObserveScope.Action, entityCount: 20, payloadBytes: 0).TtlMs);
        Assert.Equal(15_500, Policy.Compute(ObserveScope.Action, entityCount: 25, payloadBytes: 0).TtlMs);
    }

    [Fact]
    public void Payload_bonus_scales_and_is_capped()
    {
        // 2000 bytes is the baseline: no bonus.
        Assert.Equal(15_000, Policy.Compute(ObserveScope.Action, 0, payloadBytes: 2_000).TtlMs);

        // 10_000 bytes → (10_000 - 2_000) * 0.5 = 4_000 ms.
        Assert.Equal(19_000, Policy.Compute(ObserveScope.Action, 0, payloadBytes: 10_000).TtlMs);

        // A huge payload contributes at most PayloadBonusCapMs (10_000), so the base case tops out at 25_000.
        Assert.Equal(25_000, Policy.Compute(ObserveScope.Action, 0, payloadBytes: 10_000_000).TtlMs);
    }

    [Fact]
    public void Ttl_is_clamped_to_the_cap()
    {
        // 500 entities → 15_000 + (500 - 20) * 100 = 63_000, clamped to the 60_000 cap.
        Assert.Equal(60_000, Policy.Compute(ObserveScope.Action, entityCount: 500, payloadBytes: 0).TtlMs);

        // Just below the cap: no clamping.
        Assert.Equal(59_900, Policy.Compute(ObserveScope.Action, entityCount: 469, payloadBytes: 0).TtlMs);
    }

    [Fact]
    public void Ttl_is_clamped_to_the_floor()
    {
        var tight = new LeaseTtlPolicy { BaseMs = 100 };

        Assert.Equal(2_000, tight.Compute(ObserveScope.Action, 0, 0).TtlMs);
    }

    [Fact]
    public void Observed_round_trip_raises_the_ttl_and_can_force_a_refresh()
    {
        Assert.Equal(15_000, Policy.Compute(ObserveScope.Action, 0, 0, observedRoundTripMs: 500).TtlMs);
        Assert.Equal(30_000, Policy.Compute(ObserveScope.Action, 0, 0, observedRoundTripMs: 30_000).TtlMs);

        var slow = Policy.Compute(ObserveScope.Action, 0, 0, observedRoundTripMs: 90_000);
        Assert.Equal(60_000, slow.TtlMs);
        Assert.True(slow.RefreshRequired);
    }

    [Fact]
    public void Soft_expiry_is_sixty_percent_of_the_ttl()
    {
        var issued = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var lease = new EntityLease
        {
            EntityId = "e",
            ViewId = "v",
            TargetGeneration = "g",
            IdentityDigest = "d",
            IssuedAt = issued,
            ExpiresAt = issued.AddMilliseconds(10_000),
            TtlMs = 10_000,
        };

        Assert.False(lease.IsSoftExpired(issued.AddMilliseconds(5_999), Policy.SoftExpiryFraction));
        Assert.True(lease.IsSoftExpired(issued.AddMilliseconds(6_000), Policy.SoftExpiryFraction));
    }

    [Fact]
    public void Negative_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.Compute(ObserveScope.Action, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.Compute(ObserveScope.Action, 0, -1));
    }
}

/// <summary>Lease validation order is a contract: each rejection names a different failure mode.</summary>
public sealed class LeaseStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static UiEntity Entity(string id = "e1", string generation = "g1", string digest = "d1") => new()
    {
        EntityId = id,
        Generation = generation,
        Role = "window",
        Label = "Notepad",
        Rect = new PixelRect(10, 10, 100, 100),
        Confidence = 1.0,
        IdentityDigest = digest,
        LayoutDigest = "layout-" + digest,
        ProcessId = 4242,
        ObservedAtMs = Now,
    };

    private static LeaseStore StoreWithLease(out EntityLease lease, double ttlMs = 10_000)
    {
        var store = new LeaseStore();
        lease = store.Issue(Entity(), "view-1", Now, ttlMs);
        return store;
    }

    [Fact]
    public void Valid_lease_passes_and_returns_the_entity()
    {
        var store = StoreWithLease(out var lease);

        var validation = store.Validate(lease, "g1", new[] { Entity() }, Now.AddSeconds(1));

        Assert.True(validation.Ok);
        Assert.Equal("e1", validation.Entity!.EntityId);
    }

    [Fact]
    public void Expired_is_reported_first()
    {
        var store = StoreWithLease(out var lease, ttlMs: 1_000);

        // Expired AND the generation moved on: expiry must win.
        var validation = store.Validate(lease, "g2", Array.Empty<UiEntity>(), Now.AddSeconds(5));

        Assert.Equal(LeaseRejection.Expired, validation.Reason);
    }

    [Fact]
    public void Generation_mismatch_is_reported_before_entity_lookup()
    {
        var store = StoreWithLease(out var lease);

        var validation = store.Validate(lease, "g2", Array.Empty<UiEntity>(), Now.AddSeconds(1));

        Assert.Equal(LeaseRejection.GenerationMismatch, validation.Reason);
    }

    [Fact]
    public void Missing_entity_is_reported_when_the_generation_still_matches()
    {
        var store = StoreWithLease(out var lease);

        var validation = store.Validate(lease, "g1", Array.Empty<UiEntity>(), Now.AddSeconds(1));

        Assert.Equal(LeaseRejection.EntityNotFound, validation.Reason);
    }

    [Fact]
    public void Digest_mismatch_is_reported_last()
    {
        var store = StoreWithLease(out var lease);

        var validation = store.Validate(lease, "g1", new[] { Entity(digest: "changed") }, Now.AddSeconds(1));

        Assert.Equal(LeaseRejection.DigestMismatch, validation.Reason);
        Assert.NotNull(validation.Entity);
    }

    [Fact]
    public void No_lease_at_all_is_an_entity_not_found_rejection()
    {
        var store = new LeaseStore();

        var validation = store.Validate(null, "g1", new[] { Entity() }, Now);

        Assert.Equal(LeaseRejection.EntityNotFound, validation.Reason);
    }

    [Fact]
    public void Eviction_drops_only_expired_leases()
    {
        var store = new LeaseStore();
        store.Issue(Entity("short"), "v", Now, 1_000);
        store.Issue(Entity("long"), "v", Now, 60_000);

        Assert.Equal(1, store.EvictExpired(Now.AddSeconds(5)));
        Assert.Equal(1, store.Count);
        Assert.Null(store.Peek("short"));
        Assert.NotNull(store.Peek("long"));
    }

    [Fact]
    public void Reissuing_replaces_the_previous_lease_for_the_same_entity()
    {
        var store = new LeaseStore();
        var first = store.Issue(Entity(), "v1", Now, 1_000);
        var second = store.Issue(Entity(digest: "d2"), "v2", Now.AddSeconds(1), 10_000);

        Assert.Equal(1, store.Count);
        Assert.NotEqual(first.IdentityDigest, second.IdentityDigest);
        Assert.Equal("v2", store.Peek("e1")!.ViewId);
    }
}

/// <summary>Frame-diff based verification: the decision matrix must never conflate "no change" with "not measured".</summary>
public sealed class PostActionVerifierTests
{
    private static byte[] Frame(int width, int height, params (int X, int Y, int W, int H)[] dirty)
    {
        var buffer = new byte[width * height * 4];
        Array.Fill(buffer, (byte)0x20);
        foreach (var (x, y, w, h) in dirty)
        {
            for (var row = y; row < y + h; row++)
            {
                for (var col = x; col < x + w; col++)
                {
                    var index = ((row * width) + col) * 4;
                    buffer[index] = 0xFF;
                    buffer[index + 1] = 0xFF;
                    buffer[index + 2] = 0xFF;
                }
            }
        }

        return buffer;
    }

    [Fact]
    public void Identical_frames_have_no_dirty_region()
    {
        var frame = Frame(64, 64);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var dirty = PostActionVerifier.FindDirtyBounds(frame, frame, 64, 64, watch, TimeSpan.FromSeconds(1));

        Assert.NotNull(dirty);
        Assert.True(dirty!.IsEmpty);
    }

    [Fact]
    public void Dirty_bounds_cover_the_changed_block()
    {
        var before = Frame(64, 64);
        var after = Frame(64, 64, (10, 20, 5, 7));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var dirty = PostActionVerifier.FindDirtyBounds(before, after, 64, 64, watch, TimeSpan.FromSeconds(1));

        Assert.Equal(new PixelRect(10, 20, 5, 7), dirty);
    }

    [Fact]
    public void Sub_threshold_noise_is_ignored()
    {
        var before = Frame(32, 32);
        var after = (byte[])before.Clone();
        for (var i = 0; i < after.Length; i += 4)
        {
            after[i] = (byte)(before[i] + 3); // below the default channel threshold of 8
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var dirty = PostActionVerifier.FindDirtyBounds(before, after, 32, 32, watch, TimeSpan.FromSeconds(1));

        Assert.True(dirty!.IsEmpty);
    }

    [Fact]
    public void Exhausted_budget_returns_null_so_the_caller_reports_indeterminate()
    {
        var before = Frame(256, 256);
        var after = Frame(256, 256, (1, 1, 2, 2));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // A budget that is already gone: the scan must give up rather than block.
        var dirty = PostActionVerifier.FindDirtyBounds(before, after, 256, 256, watch, TimeSpan.Zero);

        Assert.Null(dirty);
    }

    [Fact]
    public void Mismatched_buffer_sizes_are_rejected()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(PostActionVerifier.FindDirtyBounds(new byte[10], new byte[10], 64, 64, watch, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Capture_failure_is_indeterminate_not_no_change()
    {
        var verifier = new PostActionVerifier(new FailingCapture());

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, 10, 10), new CaptureResult
        {
            Ok = true,
            Width = 10,
            Height = 10,
            Bgra = Frame(10, 10),
            Method = "test",
        });

        Assert.Equal(VerifyDecision.Indeterminate, result.Decision);
        Assert.Contains("capture unavailable", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resize_between_frames_is_measured_not_given_up_on()
    {
        // A window that was restored, moved or resized changes the frame between the two captures — and for a
        // focus that restored it, that change *is* the outcome. Indeterminate there left the model with no
        // signal about the step it just took.
        var verifier = new PostActionVerifier(new FixedCapture(Frame(20, 20, (2, 2, 6, 6)), 20, 20));

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, 10, 10), new CaptureResult
        {
            Ok = true,
            Width = 10,
            Height = 10,
            Bgra = Frame(10, 10),
            Method = "test",
        });

        // The change is inside the overlap, so it is a real measurement — reported as a change, not as a
        // failure to measure.
        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Contains("resized", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resize_with_identical_overlap_still_counts_as_a_change()
    {
        var verifier = new PostActionVerifier(new FixedCapture(Frame(20, 20), 20, 20));

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, 10, 10), new CaptureResult
        {
            Ok = true,
            Width = 10,
            Height = 10,
            Bgra = Frame(10, 10),
            Method = "test",
        });

        // Nothing differs inside the overlap, but the window changed shape — reporting "nothing changed" would
        // be a lie about something the model can see, and shape is usually what it just asked for.
        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.Contains("10×10 → 20×20", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ratio_is_computed_in_frame_space_not_screen_space()
    {
        // The frames are captures of the window, so their origin is the window's top-left. Handing the verifier
        // a *screen* rectangle intersected two different coordinate spaces: a window at (752,320) made every
        // target look empty, and the ratio meaningless.
        var verifier = new PostActionVerifier(new FixedCapture(Frame(64, 64, (20, 20, 8, 8)), 64, 64));

        var result = await verifier.VerifyAsync(
            1,
            new PixelRect(0, 0, 64, 64),
            new CaptureResult { Ok = true, Width = 64, Height = 64, Bgra = Frame(64, 64), Method = "test" });

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.InRange(result.IntersectedAreaRatio, 0.01, 0.03);
    }

    [Fact]
    public async Task Dirty_region_inside_the_target_is_any_change()
    {
        // 64x64 frame, target is the whole frame, a 10x10 block changed: ratio 100/4096 ≈ 2.4% ≥ 0.5%.
        var verifier = new PostActionVerifier(new FixedCapture(Frame(64, 64, (30, 30, 10, 10)), 64, 64));

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, 64, 64), Before(64, 64));

        Assert.Equal(VerifyDecision.AnyChange, result.Decision);
        Assert.True(result.IntersectedAreaRatio >= PostActionVerifier.MinIntersectedAreaRatio);
    }

    [Fact]
    public async Task Dirty_region_outside_the_target_is_no_change()
    {
        // Same frame, but the target is a tiny corner the change does not touch.
        var verifier = new PostActionVerifier(new FixedCapture(Frame(64, 64, (40, 40, 8, 8)), 64, 64));

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, 4, 4), Before(64, 64));

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Equal(0, result.IntersectedAreaRatio);
    }

    [Fact]
    public async Task Unchanged_frame_is_no_change()
    {
        var verifier = new PostActionVerifier(new FixedCapture(Frame(32, 32), 32, 32));

        var result = await verifier.VerifyAsync(1, new PixelRect(0, 0, 32, 32), Before(32, 32));

        Assert.Equal(VerifyDecision.NoChange, result.Decision);
        Assert.Contains("no dirty region", result.Reason!, StringComparison.Ordinal);
    }

    private static CaptureResult Before(int width, int height) => new()
    {
        Ok = true,
        Width = width,
        Height = height,
        Bgra = Frame(width, height),
        Method = "test",
    };

    private sealed class FailingCapture : IScreenCapture
    {
        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) =>
            Task.FromResult(CaptureResult.Failed("test", "simulated failure"));

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            Task.FromResult(CaptureResult.Failed("test", "simulated failure"));
    }

    private sealed class FixedCapture(byte[] frame, int width, int height) : IScreenCapture
    {
        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) =>
            Task.FromResult(new CaptureResult
            {
                Ok = true,
                Width = width,
                Height = height,
                Bgra = frame,
                Method = "test",
            });

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int w, int h, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }
}
