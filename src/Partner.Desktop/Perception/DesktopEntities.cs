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

    public required PixelRect Rect { get; init; }

    public required double Confidence { get; init; }

    /// <summary>Digest of the entity's content: the same id in the same generation with different content is a different target.</summary>
    public required string EvidenceDigest { get; init; }

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
    /// Controls discovered inside those windows. Empty for apps that expose nothing (Chromium, games, canvases):
    /// the window-level path still works there, it just needs coordinates.
    /// </summary>
    public IReadOnlyList<UiElement> Elements { get; init; } = Array.Empty<UiElement>();

    /// <summary>An element by the id this observation gave it.</summary>
    public UiElement? FindElement(string elementId) =>
        Elements.FirstOrDefault(element => string.Equals(element.ElementId, elementId, StringComparison.Ordinal));

    public UiEntity? Find(string entityId) =>
        Entities.FirstOrDefault(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal));

    /// <summary>Stable digest used to detect content mutation between observations.</summary>
    public static string Digest(string label, string? value, PixelRect rect)
    {
        var payload = $"{label}\u001f{value}\u001f{rect.X},{rect.Y},{rect.Width},{rect.Height}";
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

    public required string EvidenceDigest { get; init; }

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
