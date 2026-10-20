using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Partner.Core.Memory;

/// <summary>
/// Counts tokens for budgeting. The local estimate is always available; a counter that can ask the model
/// itself (a tokenizer endpoint) also implements <see cref="CountAsync"/>.
/// </summary>
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
/// per character than 1 per 4, so this <b>under</b>counted Chinese before it counted CJK characters one for one. It is the fallback, not the answer — a
/// provider-backed counter (see <c>HttpLlmClient</c>) is preferred wherever one is available.
/// </para>
/// </summary>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    private const int CharsPerToken = 4;

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // Chinese and kana run near one token per character, not one per four, so they are counted one for one;
        // everything else keeps the ~4-chars-per-token rule. Counting 5000 characters of Chinese as 1250 tokens
        // is how a budget gets blown by a factor of four.
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
        return cjk + ((other + CharsPerToken - 1) / CharsPerToken);
    }
}

/// <summary>
/// Two estimates, the larger wins. The local approximation can undershoot (CJK), and a provider count can
/// undershoot relative to a different model's tokenizer; taking the max means a budget is never blown by
/// picking the optimistic side.
/// </summary>
public sealed class DualArmTokenCounter(ITokenCounter provider, ITokenCounter local) : ITokenCounter
{
    public int Count(string text) => Math.Max(provider.Count(text), local.Count(text));

    public async ValueTask<int> CountAsync(string text, CancellationToken ct = default)
    {
        var fromProvider = await provider.CountAsync(text, ct).ConfigureAwait(false);
        var fromLocal = await local.CountAsync(text, ct).ConfigureAwait(false);
        return Math.Max(fromProvider, fromLocal);
    }
}

/// <summary>
/// Memo for token counts. Counting the same body again is the common case (a session window is rebuilt every
/// turn), and a provider-backed count costs a round trip.
/// <para>
/// Keys are 128-bit hashes rather than the text itself: the session view hands out fresh string instances each
/// turn, so a string-keyed dictionary would retain every body it ever saw. A hash collision would mis-price one
/// entry, which a budget can absorb; retaining the history cannot.
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

    /// <summary>A 128-bit content hash. SHA-256 is in the box; a 128-bit prefix makes collisions irrelevant.</summary>
    private readonly record struct Key(ulong High, ulong Low)
    {
        public static Key Of(string text)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);

            return new Key(
                BinaryPrimitives.ReadUInt64BigEndian(hash),
                BinaryPrimitives.ReadUInt64BigEndian(hash[8..]));
        }
    }
}
