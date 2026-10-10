using System.Security.Cryptography;
using System.Text;

namespace Partner.Desktop.Perception;

/// <summary>What kind of observation produced the entities — it changes the lease TTL (design doc §2.6.1).</summary>
public enum ObserveScope
{
    Action,
    Explore,
    Debug,
}

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>
    /// Whether a screen point lands inside this rectangle. Pointer actions are global — the coordinates go to
    /// whatever is there — so this is the only thing that ties a click to the window it claims to target.
    /// </summary>
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);

        return right <= left || bottom <= top
            ? new PixelRect(0, 0, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }
}

/// <summary>One addressable thing on screen. At L1 an entity is a top-level window.</summary>
public sealed record UiEntity
{
    public required string EntityId { get; init; }

    /// <summary>Snapshot generation: any new observation invalidates leases issued against an older one.</summary>
    public required string Generation { get; init; }

    public required string Role { get; init; }

    public required string Label { get; init; }

    public string? Value { get; init; }

    /// <summary>
    /// Whether this window had focus when it was observed. Keystrokes are global, so a KeyPress is only
    /// meaningful against the foreground window — otherwise it types into whatever the user was using.
    /// </summary>
    public bool IsForeground { get; init; }

    /// <summary>
    /// Whether this window is minimized. A minimized window reports a 160×28 rect at -32000,-32000: clicking
    /// "inside" it lands in the void, and a screenshot of it is a blank stub — so both are refused, and the
    /// model is told to focus it first.
    /// </summary>
    public bool IsMinimized { get; init; }

    public required PixelRect Rect { get; init; }

    public required double Confidence { get; init; }

    /// <summary>
    /// Whether this window's control list may be incomplete because the UI Automation walk hit a bound.
    /// <para>
    /// The walk is bounded — depth, controls per window, nodes visited, time — and on a Chromium-shaped
    /// window those bounds are what makes an observation take milliseconds instead of tens of seconds. But a
    /// truncated list is not the same answer as a short one, and reporting it as one is how a control that
    /// exists goes missing with nothing to show for it. Depth and the control cap are ordinary and are not
    /// reported; being cut off on nodes or time is, because that is where controls really do disappear.
    /// </para>
    /// </summary>
    public bool ElementsTruncated { get; init; }

    /// <summary>Why the walk stopped, when it stopped on a bound rather than on the tree running out.</summary>
    public WalkStopReason ElementsTruncation { get; init; } = WalkStopReason.None;

    /// <summary>
    /// Identity of the window: handle + owning process + when that process started.
    /// <para>
    /// Deliberately <b>not</b> the title or the rectangle. A music player retitles itself on every track, a
    /// browser on every tab change, an editor on its dirty marker — and a window that is restored, moved or
    /// resized keeps its identity. Folding either into identity is how "act only on what you saw" turns into
    /// "you may never act at all" on exactly the applications people want to control. Compare
    /// <see cref="LayoutDigest"/> for position instead, and treat a difference there as drift to report
    /// rather than a reason to refuse.
    /// </para>
    /// </summary>
    public required string IdentityDigest { get; init; }

    /// <summary>Digest of the rectangle alone, so a move/resize is detectable as drift.</summary>
    public required string LayoutDigest { get; init; }

    /// <summary>The owning process, part of identity so a recycled handle is not mistaken for the same window.</summary>
    public required int ProcessId { get; init; }

    /// <summary>UTC ticks of the owning process's start; null when it refuses to say (elevated).</summary>
    public long? ProcessStartTicks { get; init; }

    public required DateTimeOffset ObservedAtMs { get; init; }
}

public sealed record DesktopObservation
{
    public required string ObsId { get; init; }

    public required string TargetKey { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required IReadOnlyList<string> Sources { get; init; }

    public required IReadOnlyList<UiEntity> Entities { get; init; }

    /// <summary>
    /// Controls discovered inside those windows. Empty for apps that expose nothing (games, canvases): the
    /// window-level path still works there, it just needs coordinates. A Chromium window started with a debug
    /// port is not one of those — its controls come from the page's DOM rather than from UI Automation.
    /// </summary>
    public IReadOnlyList<UiElement> Elements { get; set; } = Array.Empty<UiElement>();

    /// <summary>An element by the id this observation gave it.</summary>
    public UiElement? FindElement(string elementId) =>
        Elements.FirstOrDefault(element => string.Equals(element.ElementId, elementId, StringComparison.Ordinal));

    public UiEntity? Find(string entityId) =>
        Entities.FirstOrDefault(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal));

    /// <summary>
    /// Identity digest of a window: handle + process + process start time.
    /// <para>
    /// Title and rectangle are excluded on purpose — see <see cref="UiEntity.IdentityDigest"/>. The label is
    /// carried alongside as <see cref="UiEntity.Label"/> so the model can still read it; it is simply not what
    /// makes a window *that* window.
    /// </para>
    /// </summary>
    public static string Identity(string entityId, int processId, long? processStartTicks, string? processName)
    {
        var payload = $"{entityId}\u001f{processId}\u001f{processStartTicks?.ToString() ?? "?"}\u001f{processName ?? "?"}";
        return Hash(payload);
    }

    /// <summary>Digest of a rectangle alone — drift to report, not a reason to refuse.</summary>
    public static string Layout(PixelRect rect) =>
        Hash($"{rect.X},{rect.Y},{rect.Width},{rect.Height}");

    /// <summary>
    /// Identity digest of a control inside a window: the window, its name and its role.
    /// <para>
    /// The rectangle is excluded here too, and for the same reason: a layout shift moves a button without
    /// replacing it, and the caller recomputes the click point from the <i>fresh</i> rectangle anyway.
    /// The value is excluded as before — typing into a field must not make the field look like a different one.
    /// </para>
    /// </summary>
    public static string ElementIdentity(string windowId, string name, string role) =>
        Hash($"{windowId}\u001f{name}\u001f{role}");

    private static string Hash(string payload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

/// <summary>Lease over one entity (design doc §2.6.1 ①).</summary>
public sealed record EntityLease
{
    public required string EntityId { get; init; }

    public required string ViewId { get; init; }

    public required string TargetGeneration { get; init; }

    /// <summary>Identity of the leased window — handle + process, not its title or rectangle.</summary>
    public required string IdentityDigest { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required double TtlMs { get; init; }

    public bool IsSoftExpired(DateTimeOffset now, double softExpiryFraction) =>
        now >= IssuedAt.AddMilliseconds(TtlMs * softExpiryFraction);
}

public enum LeaseRejection
{
    None,
    Expired,
    GenerationMismatch,
    EntityNotFound,
    DigestMismatch,
}

public sealed record LeaseValidation(LeaseRejection Reason, UiEntity? Entity)
{
    public bool Ok => Reason == LeaseRejection.None;
}
