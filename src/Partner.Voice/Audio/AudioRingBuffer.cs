namespace Partner.Voice.Audio;

/// <summary>
/// Single-writer ring buffer of mono float32 audio plus a monotonic <b>stream clock</b>.
/// <para>
/// The clock is "seconds written since the process started", not wall time. That is what makes cursors
/// cheap — and it is exactly why an idempotency key built from it must also carry a boot id, otherwise a
/// restart reuses the same offsets and a fresh utterance looks like an old, already-consumed one.
/// </para>
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private readonly object _gate = new();
    private int _write;
    private long _total;

    public AudioRingBuffer(double capacitySeconds, int sampleRate = 16_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        SampleRate = sampleRate;
        CapacitySeconds = capacitySeconds;
        _capacity = Math.Max(1, (int)Math.Ceiling(capacitySeconds * sampleRate));
        _buffer = new float[_capacity];
    }

    public int SampleRate { get; }

    public double CapacitySeconds { get; }

    /// <summary>True stream clock: total samples ever written / sample rate.</summary>
    public double Seconds
    {
        get
        {
            lock (_gate)
            {
                return _total / (double)SampleRate;
            }
        }
    }

    /// <summary>Oldest instant still readable. Anything before it has been overwritten.</summary>
    public double OldestAvailableSeconds
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(0, (_total - _capacity) / (double)SampleRate);
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            var remaining = samples;
            if (remaining.Length > _capacity)
            {
                // Only the newest capacity is retained…
                remaining = remaining[^_capacity..];
            }

            var first = Math.Min(remaining.Length, _capacity - _write);
            remaining[..first].CopyTo(_buffer.AsSpan(_write));
            if (remaining.Length > first)
            {
                remaining[first..].CopyTo(_buffer);
            }

            _write = (_write + remaining.Length) % _capacity;

            // …but the clock counts everything ever written: it is a stream clock, and callers use it to
            // detect eviction. Counting only what fit would make an overwritten cursor look valid.
            _total += samples.Length;
        }
    }

    /// <summary>
    /// Audio from <paramref name="cursorSeconds"/> up to <paramref name="maxSeconds"/> worth, or <c>null</c>
    /// when the cursor has already been evicted (the caller then resynchronizes and reports the loss).
    /// </summary>
    public float[]? ReadSince(double cursorSeconds, double maxSeconds)
    {
        lock (_gate)
        {
            var startSample = (long)Math.Round(cursorSeconds * SampleRate);
            var oldest = _total - _capacity;

            if (startSample < oldest)
            {
                return null;
            }

            var from = Math.Max(0, startSample);
            var available = _total - from;
            if (available <= 0)
            {
                return Array.Empty<float>();
            }

            var count = (int)Math.Min(available, Math.Max(0, (long)Math.Round(maxSeconds * SampleRate)));
            if (count <= 0)
            {
                return Array.Empty<float>();
            }

            var result = new float[count];
            var offset = (int)(from % _capacity);
            var first = Math.Min(count, _capacity - offset);
            Array.Copy(_buffer, offset, result, 0, first);
            if (count > first)
            {
                Array.Copy(_buffer, 0, result, first, count - first);
            }

            return result;
        }
    }

    /// <summary>
    /// Audio from the most recent <paramref name="seconds"/> worth of the stream (and nothing older),
    /// or what is available when the buffer holds less. Empty rather than null when it cannot be
    /// served: a probe consumer is asking, not synchronizing.
    /// </summary>
    public float[] ReadLast(double seconds)
    {
        lock (_gate)
        {
            var count = (int)Math.Min(_total, Math.Max(0, Math.Round(seconds * SampleRate)));
            if (count <= 0)
            {
                return Array.Empty<float>();
            }

            var start = (_write - count + _capacity * 2) % _capacity;
            var result = new float[count];
            var first = Math.Min(count, _capacity - start);
            Array.Copy(_buffer, start, result, 0, first);
            if (count > first)
            {
                Array.Copy(_buffer, 0, result, first, count - first);
            }

            return result;
        }
    }

    /// <summary>RMS of the last <paramref name="seconds"/> of audio (the speech/silence test).</summary>
    public double Energy(double seconds)
    {
        var samples = ReadLast(seconds);
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Fraction of 20 ms frames whose RMS reaches <paramref name="threshold"/> (speech presence test).</summary>
    public double SpeechFraction(double threshold, double seconds)
    {
        var samples = ReadLast(seconds);
        var frame = (int)(0.02 * SampleRate);
        if (frame <= 0 || samples.Length < frame)
        {
            return 0;
        }

        var frames = samples.Length / frame;
        var voiced = 0;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            var offset = f * frame;
            for (var i = 0; i < frame; i++)
            {
                sum += samples[offset + i] * samples[offset + i];
            }

            if (Math.Sqrt(sum / frame) >= threshold)
            {
                voiced++;
            }
        }

        return (double)voiced / frames;
    }
}
