using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Partner.Core.Memory;

/// <summary>
/// A counter that can be told what the model really charged, and tunes its estimate toward it.
/// </summary>
public interface ICalibratedTokenCounter : ITokenCounter
{
    /// <param name="estimated">What this counter said the request cost.</param>
    /// <param name="actual">What the server says it cost (<c>usage.prompt_tokens</c>).</param>
    void Calibrate(int estimated, int actual);
}

public interface ITokenCounter
{
    int Count(string text);

    /// <summary>
    /// Model-native count when the implementation has one. The default keeps this interface non-breaking: a
    /// counter that only knows the local estimate answers from it.
    /// </summary>
    ValueTask<int> CountAsync(string text, CancellationToken ct = default) => ValueTask.FromResult(Count(text));
}

/// <summary>
/// Dependency-free approximation: ~1 token per 4 characters, never zero for non-empty text.
/// <para>
/// Deliberately crude, and knowingly wrong in one direction for some scripts: CJK text runs closer to 1 token
/// per character than 1 per 4, so this counts CJK characters one for one. It is the fallback, not the answer — a
/// provider-backed counter (see <c>HttpLlmClient</c>) is preferred wherever one is available.
/// </para>
/// </summary>
public sealed class ApproximateTokenCounter : ICalibratedTokenCounter
{
    private const int CharsPerToken = 4;

    private const double MinScale = 0.4;
    private const double MaxScale = 4.0;

    /// <summary>How far one observation moves the scale. A single odd request must not swing every later budget.</summary>
    private const double Smoothing = 0.3;

    private double _scale = 1.0;

    /// <summary>The factor the raw estimate is multiplied by; 1 until the server has said otherwise.</summary>
    public double Scale => Volatile.Read(ref _scale);

    /// <summary>
    /// Moves the scale toward <c>actual / estimated</c>. The estimate here is already scaled, so the update is
    /// multiplicative: if the server charged 20% more than said, the scale grows by a fifth of that 20%.
    /// </summary>
    public void Calibrate(int estimated, int actual)
    {
        if (estimated < 50 || actual < 50)
        {
            return; // too small to say anything about the ratio
        }

        var ratio = (double)actual / estimated;
        var current = Scale;
        var next = Math.Clamp(current * (1 + (Smoothing * (ratio - 1))), MinScale, MaxScale);
        Volatile.Write(ref _scale, next);
    }

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // Chinese and kana run near one token per character, not one per four, so they are counted one for one;
        // everything else keeps the ~4-chars-per-token rule.
        var cjk = 0;
        foreach (var ch in text)
        {
            if (ch is >= (char)0x4E00 and <= (char)0x9FFF or >= (char)0x3040 and <= (char)0x30FF)
            {
                cjk++;
            }
        }

        var other = text.Length - cjk;

        // Round up so any non-empty text costs at least one token.
        var raw = cjk + ((other + CharsPerToken - 1) / CharsPerToken);
        return Math.Max(1, (int)Math.Ceiling(raw * Scale));
    }
}

/// <summary>
/// Two estimates, the larger wins. The local approximation can undershoot (CJK), and a provider count can
/// undershoot relative to a different model's tokenizer; taking the max means a budget is never blown by
/// picking the optimistic side.
/// </summary>
public sealed class DualArmTokenCounter(ITokenCounter provider, ITokenCounter local) : ICalibratedTokenCounter
{
    public void Calibrate(int estimated, int actual) => (local as ICalibratedTokenCounter)?.Calibrate(estimated, actual);

    public int Count(string text) => Math.Max(provider.Count(text), local.Count(text));

    public async ValueTask<int> CountAsync(string text, CancellationToken ct = default)
    {
        // Both at once: the provider arm is a network round trip and the local one is not.
        var providerCount = provider.CountAsync(text, ct).AsTask();
        var localCount = local.CountAsync(text, ct).AsTask();
        await Task.WhenAll(providerCount, localCount).ConfigureAwait(false);
        return Math.Max(providerCount.Result, localCount.Result);
    }
}

/// <summary>
/// Memo for token counts. Counting the same body again is the common case, and a provider-backed count costs a
/// round trip.
/// <para>
/// Keys are 128-bit hashes rather than the text itself: the session view hands out fresh string instances each
/// turn, so a string-keyed dictionary would retain every body it ever saw. A hash collision would mis-price one
/// entry, which a budget can absorb.
/// </para>
/// </summary>
public sealed class TokenCountCache
{
    private readonly int _capacity;
    private readonly Dictionary<Key, int> _entries = new();
    private readonly Queue<Key> _inserted = new();
    private readonly object _gate = new();

    public TokenCountCache(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Size
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>How often a lookup found something, for diagnostics.</summary>
    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public bool TryGet(string text, out int tokens)
    {
        var key = Key.Of(text);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out tokens))
            {
                Hits++;
                return true;
            }

            Misses++;
            return false;
        }
    }

    public void Set(string text, int tokens)
    {
        var key = Key.Of(text);

        lock (_gate)
        {
            if (_entries.ContainsKey(key))
            {
                _entries[key] = tokens;
                return;
            }

            // FIFO eviction: simpler than LRU, and a rebuilt window re-counts at worst one turn's worth.
            while (_entries.Count >= _capacity && _inserted.Count > 0)
            {
                _entries.Remove(_inserted.Dequeue());
            }

            _entries[key] = tokens;
            _inserted.Enqueue(key);
        }
    }

    /// <summary>
    /// A 128-bit content key: two independent 64-bit FNV-1a passes over the characters, plus the length. The hash
    /// is what a cache lookup costs. Nothing here is adversarial (the text is this process's own history), and a
    /// collision would mis-price one entry, which a budget absorbs.
    /// </summary>
    private readonly record struct Key(ulong High, ulong Low)
    {
        public static Key Of(string text)
        {
            const ulong OffsetA = 14695981039346656037UL;
            const ulong PrimeA = 1099511628211UL;
            const ulong OffsetB = 0x9E3779B97F4A7C15UL;
            const ulong PrimeB = 0x100000001B3UL * 31 + 7;

            var a = OffsetA;
            var b = OffsetB ^ (ulong)text.Length;

            foreach (var ch in text)
            {
                a = (a ^ ch) * PrimeA;
                b = ((b << 5) | (b >> 59)) ^ ch;
                b *= PrimeB;
            }

            return new Key(a, b ^ ((ulong)text.Length << 32));
        }
    }
}
