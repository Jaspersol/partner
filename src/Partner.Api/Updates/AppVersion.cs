using System.Globalization;

namespace Partner.Api.Updates;

/// <summary>
/// The part of SemVer that release tags use: three numeric parts and an optional prerelease suffix. Build
/// metadata is ignored, because it does not order versions.
/// </summary>
/// <remarks>
/// Only this subset is parsed on purpose: a tag that looks like <c>v1.2</c> or <c>latest</c> is a release
/// mistake, and calling it "older" would hide the mistake behind a silent "up to date".
/// </remarks>
public readonly struct AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }

    private AppVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        // Build metadata (`+sha`) never affects precedence, so it is cut before anything else is read.
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var prerelease = dash >= 0 ? value[(dash + 1)..] : null;

        // An empty suffix ("1.2.3-") is not a prerelease; it is a typo.
        if (prerelease is { Length: 0 })
        {
            return false;
        }

        var parts = core.Split('.');
        if (parts.Length != 3
            || !TryPart(parts[0], out var major)
            || !TryPart(parts[1], out var minor)
            || !TryPart(parts[2], out var patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>Digits only: a sign or whitespace inside a component is a malformed tag, not a number.</summary>
    private static bool TryPart(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(AppVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // A release outranks any prerelease of the same numbers: 1.0.0 > 1.0.0-rc.1.
        if (Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }

        if (Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Dot-separated identifiers, numeric ones compared as numbers and ranking below alphanumeric ones.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var x = a[i];
            var y = b[i];
            var xNumeric = TryPart(x, out var xn);
            var yNumeric = TryPart(y, out var yn);

            var result = xNumeric && yNumeric
                ? xn.CompareTo(yn)
                : xNumeric
                    ? -1
                    : yNumeric
                        ? 1
                        : string.CompareOrdinal(x, y);
            if (result != 0)
            {
                return result;
            }
        }

        // Same prefix: the one with more identifiers is the later version (alpha < alpha.1).
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(AppVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;
}
