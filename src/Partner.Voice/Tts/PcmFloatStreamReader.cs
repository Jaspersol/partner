using System.Buffers.Binary;

namespace Partner.Voice.Tts;

/// <summary>
/// Turns a byte stream of little-endian float32 samples into whole-sample float chunks.
/// A TCP read may split a 4-byte float in half — without carrying the remainder, every chunk
/// boundary would produce garbage samples.
/// </summary>
public sealed class PcmFloatStreamReader
{
    private readonly int _bytesPerSample;
    private readonly byte[] _carry;
    private int _carryCount;

    public PcmFloatStreamReader(int bytesPerSample = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerSample, 1);
        _bytesPerSample = bytesPerSample;
        _carry = new byte[bytesPerSample];
    }

    /// <summary>Bytes held back because they did not form a whole sample.</summary>
    public int PendingBytes => _carryCount;

    /// <summary>Feeds raw bytes and returns whole samples decoded so far.</summary>
    public float[] Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return Array.Empty<float>();
        }

        if (_carryCount + bytes.Length < _bytesPerSample)
        {
            bytes.CopyTo(_carry.AsSpan(_carryCount));
            _carryCount += bytes.Length;
            return Array.Empty<float>();
        }

        var samples = new float[(_carryCount + bytes.Length) / _bytesPerSample];
        var index = 0;

        // 先补齐上一次留下的半个采样。
        if (_carryCount > 0)
        {
            var needed = _bytesPerSample - _carryCount;
            bytes[..needed].CopyTo(_carry.AsSpan(_carryCount));
            samples[index++] = ReadSample(_carry);
            bytes = bytes[needed..];
            _carryCount = 0;
        }

        // 再直接从输入里解整采样，避免多一次拷贝。
        var whole = bytes.Length / _bytesPerSample;
        for (var i = 0; i < whole; i++)
        {
            samples[index++] = ReadSample(bytes.Slice(i * _bytesPerSample, _bytesPerSample));
        }

        bytes = bytes[(whole * _bytesPerSample)..];
        if (!bytes.IsEmpty)
        {
            bytes.CopyTo(_carry);
            _carryCount = bytes.Length;
        }

        return samples;
    }

    /// <summary>Drops a trailing partial sample (call at end of stream).</summary>
    public int DiscardPartialSample()
    {
        var dropped = _carryCount;
        _carryCount = 0;
        return dropped;
    }

    private float ReadSample(ReadOnlySpan<byte> sample) =>
        _bytesPerSample == 4
            ? BinaryPrimitives.ReadSingleLittleEndian(sample)
            : throw new NotSupportedException($"Unsupported sample width: {_bytesPerSample} bytes.");
}
