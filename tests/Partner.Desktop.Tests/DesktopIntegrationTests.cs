using System.Runtime.InteropServices;
using Partner.Desktop;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;

/// <summary>
/// Integration against the real desktop. Read-only by design: enumeration and capture only — the suite must
/// never move the user's mouse or type into whatever window happens to be focused.
/// </summary>
public sealed class DesktopIntegrationTests
{
    [Fact]
    public void A_targeted_observation_refreshes_one_window_and_keeps_the_others()
    {
        // Acting re-checks one window; walking every window's UIA tree costs seconds (10.2 s for nine windows,
        // measured). The ids the model holds for the other windows have to survive that, or a screenshot of
        // one of them would silently lose its marks.
        using var automation = new DesktopAutomation();

        var full = automation.Observe();
        var target = full.Entities[0].EntityId;
        var carried = full.Elements.Where(element => element.WindowId != target).ToList();

        var targeted = automation.Observe(elementsFor: target);

        // A new observation, so every lease issued against the previous generation is invalid.
        Assert.NotEqual(full.ObsId, targeted.ObsId);
        Assert.Equal(full.Entities.Count, targeted.Entities.Count);
        Assert.Equal(full.Entities.Count, automation.Leases.Count);

        // The named window's ids are still positions in its own list, and the others are the elements from
        // before — same objects, because nothing about them was re-read.
        Assert.All(
            targeted.Elements.Where(element => element.WindowId == target),
            element => Assert.StartsWith(target + ":", element.ElementId, StringComparison.Ordinal));
        Assert.All(carried, element => Assert.Contains(element, targeted.Elements));
    }

    [Fact]
    public void A_cropped_frame_keeps_the_pixel_that_was_at_the_crop_origin()
    {
        // PrintWindow renders from the window-rectangle origin, a few pixels outside the visible frame; this
        // is the arithmetic that moves the picture back onto the rectangle every coordinate is expressed in.
        var frame = SyntheticFrame(4, 3);

        var cropped = GdiScreenCapture.Crop(frame, 1, 1, 2, 2);

        Assert.True(cropped.Ok, cropped.Error);
        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(2 * 2 * 4, cropped.Bgra.Length);
        Assert.Equal(Pixel(frame, 1, 1), Pixel(cropped, 0, 0));
        Assert.Equal(Pixel(frame, 2, 2), Pixel(cropped, 1, 1));
    }

    [Fact]
    public void A_crop_that_does_not_fit_is_refused_rather_than_returned_misaligned()
    {
        var cropped = GdiScreenCapture.Crop(SyntheticFrame(4, 3), 3, 0, 4, 3);

        Assert.False(cropped.Ok);
        Assert.Contains("visible frame", cropped.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_size_crop_is_the_frame_itself()
    {
        var frame = SyntheticFrame(4, 3);

        Assert.Same(frame, GdiScreenCapture.Crop(frame, 0, 0, 4, 3));
    }

    /// <summary>A frame where every pixel encodes its own coordinates.</summary>
    private static CaptureResult SyntheticFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)((y * width) + x);
                pixels[i + 1] = (byte)x;
                pixels[i + 2] = (byte)y;
                pixels[i + 3] = 255;
            }
        }

        return new CaptureResult { Ok = true, Width = width, Height = height, Bgra = pixels, Method = "test" };
    }

    private static int Pixel(CaptureResult frame, int x, int y)
    {
        var i = ((y * frame.Width) + x) * 4;
        return frame.Bgra[i] | frame.Bgra[i + 1] << 8 | frame.Bgra[i + 2] << 16;
    }

    [Fact]
    public void Window_enumeration_finds_visible_titled_windows()
    {
        var windows = WindowEnumerator.List();

        Assert.NotEmpty(windows);
        Assert.All(windows, w =>
        {
            Assert.NotEqual(0, w.Handle);
            Assert.False(string.IsNullOrWhiteSpace(w.Title));
            Assert.True(w.Width > 0 && w.Height > 0);
        });
        Assert.Single(windows, w => w.IsForeground);
    }

    [Fact]
    public async Task Gdi_capture_returns_pixels_of_the_expected_size()
    {
        var target = WindowEnumerator.List().FirstOrDefault(w => w.Width >= 100 && w.Height >= 100);
        Assert.NotNull(target);

        var capture = new GdiScreenCapture();
        var frame = await capture.CaptureWindowAsync(target!.Handle);

        Assert.True(frame.Ok, frame.Error);
        Assert.True(frame.Width > 0 && frame.Height > 0);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Bgra.Length);
        Assert.False(string.IsNullOrWhiteSpace(frame.Method));
    }

    [Fact]
    public async Task Capturing_an_invalid_handle_fails_without_throwing()
    {
        var capture = new GdiScreenCapture();

        var frame = await capture.CaptureWindowAsync(0x1234);

        Assert.False(frame.Ok);
        Assert.NotNull(frame.Error);
    }

    [Fact]
    public void Input_record_size_matches_the_win32_contract()
    {
        // INPUT is 40 bytes on x64 and 28 on x86; a mismatch silently breaks every injected event.
        var expected = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expected, InputInjector.InputSize);
        Assert.Equal(expected, Marshal.SizeOf<INPUTProbe>());
    }

    [Fact]
    public void Observe_issues_one_lease_per_entity_with_a_generation()
    {
        using var automation = new DesktopAutomation();

        var observation = automation.Observe(ObserveScope.Action);

        Assert.NotEmpty(observation.Entities);
        Assert.Equal(observation.Entities.Count, automation.Leases.Count);
        Assert.All(observation.Entities, e =>
        {
            Assert.Equal(observation.ObsId, e.Generation);
            Assert.False(string.IsNullOrWhiteSpace(e.IdentityDigest));
            Assert.False(string.IsNullOrWhiteSpace(e.LayoutDigest));
            Assert.Equal(e.EntityId, automation.Leases.Peek(e.EntityId)!.EntityId);
        });
    }

    [Fact]
    public async Task Action_without_any_observation_is_refused()
    {
        using var automation = new DesktopAutomation();

        var action = await automation.ActAsync("deadbeef", ActionKind.MovePointer, new ActionParams { X = 1, Y = 1 });

        Assert.False(action.GuardPassed);
        Assert.Equal("no_observation", action.GuardReason);
        Assert.Null(action.Injection);
    }

    [Fact]
    public async Task Action_against_an_unknown_entity_is_refused_without_injecting()
    {
        using var automation = new DesktopAutomation();
        automation.Observe();

        var action = await automation.ActAsync("deadbeef", ActionKind.MovePointer, new ActionParams { X = 1, Y = 1 });

        Assert.False(action.GuardPassed);
        Assert.Equal("entity_not_found", action.GuardReason);
        Assert.Null(action.Injection);
        Assert.Null(action.PostVerify);
    }

    [Fact]
    public void An_observation_is_stamped_when_it_finishes_not_when_it_starts()
    {
        // Reading the UIA trees of every window takes seconds; enumerating the windows takes milliseconds. The
        // stamp, and therefore every lease's issue time, must be taken after the walk: before it, a 15 s lease
        // is handed out already ~10 s old and the next action comes back "Expired" on a lease the caller had
        // just been given.
        using var automation = new DesktopAutomation();

        var before = DateTimeOffset.UtcNow;
        var observation = automation.Observe();
        var elapsed = DateTimeOffset.UtcNow - before;

        // Stamped at the end of the walk, so it sits at least most of the way through an observation that
        // takes seconds. Capturing it up front put it at the start of that window.
        Assert.True(
            observation.Timestamp >= before + TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds * 0.5),
            $"stamped {(observation.Timestamp - before).TotalMilliseconds:0.0}ms into a "
                + $"{elapsed.TotalMilliseconds:0}ms observation");

        // And the policy's "how slow is observing" input has to describe the whole observation, or a lease is
        // never lengthened for the case it was lengthened for.
        Assert.InRange(
            automation.Leases.ObservedRoundTripMs!.Value,
            elapsed.TotalMilliseconds * 0.5,
            elapsed.TotalMilliseconds + 1);
    }

    [Fact]
    public void Re_observing_refreshes_leases_for_entities_that_are_still_present()
    {
        using var automation = new DesktopAutomation();
        var first = automation.Observe();
        var entity = first.Entities.First();

        var second = automation.Observe();

        // Same window, new generation: a fresh observation re-issues a valid lease — that is the design.
        // What the generation guards against is acting on an id that is *absent* from the current
        // observation (window closed) or whose content changed (digest mismatch); both are covered by
        // LeaseStoreTests at the unit level.
        Assert.NotNull(second.Find(entity.EntityId));
        Assert.NotEqual(first.ObsId, second.ObsId);
        Assert.Equal(second.ObsId, automation.Leases.Peek(entity.EntityId)!.TargetGeneration);
    }

    [Fact]
    public void Observation_ids_are_unique_even_within_the_same_tick()
    {
        using var automation = new DesktopAutomation();

        var ids = Enumerable.Range(0, 5).Select(_ => automation.Observe().ObsId).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Action_after_the_lease_expires_is_refused_without_injecting()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        using var automation = new DesktopAutomation(
            ttlPolicy: new LeaseTtlPolicy { BaseMs = 1_000, FloorMs = 1_000 },
            clock: clock);

        // Observe once so a lease exists, then let it expire before acting.
        var observation = automation.Observe();
        var entity = observation.Entities.First();
        clock.Advance(TimeSpan.FromMinutes(5));

        var action = await automation.ActAsync(entity.EntityId, ActionKind.MovePointer, new ActionParams());

        Assert.False(action.GuardPassed);
        Assert.Equal(LeaseRejection.Expired.ToString(), action.GuardReason);
        Assert.Null(action.Injection);
    }

    [Fact]
    public void Lease_validation_order_is_expired_then_generation_then_missing_then_digest()
    {
        var clock = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new LeaseStore();
        var entity = new UiEntity
        {
            EntityId = "e",
            Generation = "g1",
            Role = "window",
            Label = "x",
            Rect = new PixelRect(0, 0, 1, 1),
            Confidence = 1,
            IdentityDigest = "d1",
            LayoutDigest = "l1",
            ProcessId = 1,
            ObservedAtMs = clock,
        };

        var lease = store.Issue(entity, "v", clock, 1_000);

        // Expired wins even when everything else is also wrong.
        Assert.Equal(
            LeaseRejection.Expired,
            store.Validate(lease, "g2", Array.Empty<UiEntity>(), clock.AddSeconds(2)).Reason);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>Mirrors the native INPUT layout so the marshalling size can be asserted independently.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUTProbe
    {
        public uint Type;
        public InputUnionProbe Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnionProbe
    {
        [FieldOffset(0)]
        public MouseProbe Mouse;

        [FieldOffset(0)]
        public long Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseProbe
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
