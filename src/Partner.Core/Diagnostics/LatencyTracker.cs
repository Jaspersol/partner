namespace Partner.Core.Diagnostics;

/// <summary>One named latency, summarised.</summary>
public sealed record LatencySummary(string Name, int Count, double P50Ms, double P95Ms, double MaxMs, double LastMs);

/// <summary>
/// The latencies a person feels, in one place: first sound after a reply starts, a search, an action, a hand-off,
/// a whole turn.
/// <para>
/// Process-wide on purpose. These are diagnostics written from unrelated components, none of which should have
/// to be handed a tracker to be measured; the cost is that tests must use distinct names (or <see cref="Reset"/>).
/// Each name keeps its latest <see cref="Samples"/> values.
/// </para>
/// </summary>
public static class LatencyTracker
{
    public const int Samples = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Ring> Rings = new(StringComparer.Ordinal);

    public static void Record(string name, TimeSpan elapsed) => Record(name, elapsed.TotalMilliseconds);

    public static void Record(string name, double milliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (Gate)
        {
            if (!Rings.TryGetValue(name, out var ring))
            {
                Rings[name] = ring = new Ring();
            }

            ring.Add(milliseconds);
        }
    }

    public static IReadOnlyList<LatencySummary> Snapshot()
    {
        lock (Gate)
        {
            return Rings
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.Summarise(pair.Key))
                .ToList();
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Rings.Clear();
        }
    }

    private sealed class Ring
    {
        private readonly double[] _values = new double[Samples];
        private int _count;
        private int _next;
        private double _last;

        public void Add(double value)
        {
            _values[_next] = value;
            _next = (_next + 1) % Samples;
            _count = Math.Min(_count + 1, Samples);
            _last = value;
        }

        public LatencySummary Summarise(string name)
        {
            var sorted = _values.Take(_count).OrderBy(v => v).ToArray();
            return new LatencySummary(
                name,
                _count,
                sorted[sorted.Length / 2],
                sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1)],
                sorted[^1],
                _last);
        }
    }
}
