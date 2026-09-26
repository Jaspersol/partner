using System.Globalization;

namespace Partner.Game.Adapter;

/// <summary>A sparse snapshot of game state. Only what a sensor/reflex actually reads.</summary>
public sealed record GameStateSnapshot
{
    public required int Seq { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>Flat key/value state ("x", "y", "hp", "enemies", …). Values are strings on purpose:
    /// the adapter must not invent a schema per game.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>Reads a numeric value; returns <paramref name="fallback"/> when the key is missing or not numeric.</summary>
    public double GetDouble(string key, double fallback = 0)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
    }

    /// <summary>Reads an integer value; returns <paramref name="fallback"/> when the key is missing or not numeric.</summary>
    public int GetInt(string key, int fallback = 0)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!Values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            ? (int)real
            : fallback;
    }
}

/// <summary>An action the reflex engine wants the game to perform.</summary>
public sealed record GameActionRequest(string Kind, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>Convenience factory: <c>GameActionRequest.Of("move", ("dx", "3"), ("dy", "4"))</c>.</summary>
    public static GameActionRequest Of(string kind, params (string Key, string Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(parameters);

        var map = new Dictionary<string, string>(parameters.Length, StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            map[key] = value;
        }

        return new GameActionRequest(kind, map);
    }
}

/// <summary>The result of a single act call.</summary>
public sealed record GameActionOutcome
{
    public required bool Ok { get; init; }

    public required string Detail { get; init; }

    /// <summary>Round trip of the act call, measured by the adapter.</summary>
    public required double LatencyMs { get; init; }

    /// <summary>The state the game reported after the action, when the game returned one.</summary>
    public GameStateSnapshot? After { get; init; }
}

/// <summary>Observed latency distribution of observe/act round trips.</summary>
public sealed record LatencyStats(double P50Ms, double P99Ms, double MaxMs, int Samples);

/// <summary>
/// A game adapter talks to a game over its own process boundary and its own protocol
/// (design doc §2.7: 独立进程、独立协议、绝不走 MCP). Every method is a single request/response
/// round trip except attach/detach.
/// </summary>
public interface IGameAdapter : IAsyncDisposable
{
    string GameId { get; }

    bool IsAttached { get; }

    Task AttachAsync(CancellationToken ct = default);

    Task DetachAsync(CancellationToken ct = default);

    /// <summary>Reads the current state. This is the primary observation path (game API first).</summary>
    Task<GameStateSnapshot> ObserveAsync(CancellationToken ct = default);

    Task<GameActionOutcome> ActAsync(GameActionRequest action, CancellationToken ct = default);

    /// <summary>Observed latency of observe+act round trips, for the latency self-monitor.</summary>
    LatencyStats Latency { get; }
}
