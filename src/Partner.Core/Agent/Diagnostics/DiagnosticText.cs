using System.Text.RegularExpressions;

namespace Partner.Core.Agent.Diagnostics;

/// <summary>
/// Takes secrets out of text that is about to be shown to somebody or written down.
/// <para>
/// This exists because the things worth diagnosing — an endpoint's error body, a tool's message, a failed
/// command's output — are exactly the things that can contain a key. A request-id log that leaks a bearer
/// token is worse than no log: it puts the secret somewhere with weaker protection and a longer life.
/// </para>
/// <para>
/// The reductions are deliberately shallow. Over-eager masking makes a diagnosis impossible, which is its own
/// kind of failure, so only what is recognisably a secret or a home directory is masked — and each rule says
/// what it matched, so a reader can tell a redaction from the real value.
/// </para>
/// </summary>
public static class DiagnosticText
{
    /// <summary>Longest text kept. Beyond this the interesting part is long gone and the size is not free.</summary>
    public const int MaxLength = 2000;

    /// <summary>What a redaction stands in for.</summary>
    private const string Redacted = "[redacted]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Tokens whose shape is enough to recognise them: a key, a session, an AWS id.</summary>
    private static readonly Regex KnownToken = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{16,}|gho_[A-Za-z0-9]{16,}|xox[baprs]-[A-Za-z0-9-]{8,}|AKIA[0-9A-Z]{12,})",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Anything that assigns a secret-looking name a value, whatever the value looks like.</summary>
    private static readonly Regex AssignedSecret = new(
        @"(?<name>\b(?:api[_-]?key|apikey|secret|password|passwd|pwd|token|access[_-]?token|authorization)\b)"
        + @"\s*[:=]\s*"
        + @"[""']?(?<value>[^""'\s,;&]{4,})[""']?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>A bearer header: a token in a place where the name says nothing useful.</summary>
    private static readonly Regex BearerHeader = new(
        @"\bBearer\s+[A-Za-z0-9._\-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>A user's home directory: not a secret, but a person's account name.</summary>
    private static readonly Regex WindowsHome = new(
        @"(?<drive>[A-Za-z]:\\Users\\)[^\\\s""']+",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex PosixHome = new(
        @"/home/[^/\s""']+",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// The text, with secrets replaced and the length bounded.
    /// </summary>
    /// <param name="text">What to clean. Null becomes the empty string rather than throwing: this runs on failure paths.</param>
    /// <param name="maxLength">Where to cut. Longer text is truncated with an ellipsis, never silently.</param>
    public static string Sanitize(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = text;

        // Bearer first. The named-assignment rule matches `Authorization:` and masks only the first token,
        // which on a header line leaves the credential itself sitting right there in the clear — a redaction
        // that hides the name and shows the secret is worse than none.
        cleaned = BearerHeader.Replace(cleaned, "Bearer " + Redacted);

        // Then the named assignments, then the shape rules for whatever those did not claim.
        cleaned = AssignedSecret.Replace(cleaned, m => $"{m.Groups["name"].Value}={Redacted}");
        cleaned = KnownToken.Replace(cleaned, Redacted);
        cleaned = WindowsHome.Replace(cleaned, m => m.Groups["drive"].Value + Redacted);
        cleaned = PosixHome.Replace(cleaned, "/home/" + Redacted);

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }

    /// <summary>Whether sanitising would change the text. For callers deciding whether to log at all.</summary>
    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(Sanitize(text), text, StringComparison.Ordinal);
}
