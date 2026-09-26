using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Contracts;

namespace Partner.Core.Sessions;

/// <summary>
/// One record in the single session log (design doc §2.2).
/// <para>
/// Append-only: an event is never rewritten. A correction is a new event with
/// <see cref="Kind"/> = <see cref="SessionEventKind.Edit"/> carrying <see cref="EditedFromSeq"/>.
/// </para>
/// </summary>
public sealed record SessionEvent
{
    /// <summary>Assigned by <c>ISessionStore</c> (single writer). Leave 0 on append.</summary>
    public long Seq { get; init; }

    public DateTimeOffset At { get; init; }

    public SessionEventKind Kind { get; init; }

    public InputSource Source { get; init; }

    /// <summary>Single-line content. Newlines are rejected by the store (line-framed log).</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Only set when <see cref="Kind"/> is <see cref="SessionEventKind.Edit"/>: the seq being revised.</summary>
    public long? EditedFromSeq { get; init; }

    /// <summary>Optional free-form annotations (tool name, latency, model, …).</summary>
    public IReadOnlyDictionary<string, string>? Meta { get; init; }
}

/// <summary>Canonical JSON settings for the session log. Compact, string enums, nulls omitted.</summary>
public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize(SessionEvent value) => JsonSerializer.Serialize(value, Options);

    public static SessionEvent? Deserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionEvent>(line, Options);
        }
        catch (JsonException)
        {
            // A corrupt interior line is not expected: the tail is repaired on open and
            // every record is flushed as a whole line. Skip rather than fail the whole read.
            return null;
        }
    }
}
