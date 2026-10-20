using System.Buffers.Binary;

namespace Partner.Voice.Tts;

/// <summary>
/// Two-layer WAV decoder: 16-bit little-endian integer samples → [-1,1] float.
/// </summary>
public static class WavPcm16Decoder
{
    /// <summary>
    /// Parses a 16-bit PCM WAV and returns [-1,1] floats. The header is walked as RIFF chunks (fmt may carry
    /// extension fields before data); only the <c>data</c> chunk and PCM format are accepted; anything else
    /// throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static float[] Decode(byte[] wav)
    {
        if (wav.Length < 44
            || System.Text.Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
            || System.Text.Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a RIFF/WAVE body.");
        }

        var position = 12;
        short channels = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (position + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(position + 4, 4));
            var body = position + 8;
            if (body + size > wav.Length)
            {
                size = wav.Length - body; // trailing chunk may be truncated
            }

            if (id == "fmt ")
            {
                var format = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body, 2));
                if (format != 1) // PCM only; WAVE_FORMAT_EXTENSIBLE (0xFFFE) sometimes appears from servers with an extension header.
                {
                    throw new InvalidDataException($"WAV format {format} is not PCM.");
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 2, 2));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14, 2));
            }
            else if (id == "data")
            {
                data = wav[body..(body + size)];
            }

            position = body + size + (size & 1); // chunks are 2-byte aligned
        }

        if (bitsPerSample != 16 || channels < 1)
        {
            throw new InvalidDataException($"Unsupported PCM layout: {bitsPerSample}-bit, {channels} channel(s).");
        }

        data ??= Array.Empty<byte>();
        if (data.Length < 2)
        {
            return Array.Empty<float>(); // an empty data chunk is a legal response (e.g. blank input), not an error.
        }

        var frames = data.Length / 2 / channels;
        var samples = new float[frames];
        if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2));
                samples[i] = raw / 32768f;
            }
        }
        else
        {
            // Take the first channel: playback only needs a mono stream, and mixing is not this layer's job.
            for (var i = 0; i < frames; i++)
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * channels * 2, 2));
                samples[i] = raw / 32768f;
            }
        }

        return samples;
    }
}
