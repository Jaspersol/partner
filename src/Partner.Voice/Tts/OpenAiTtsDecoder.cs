using System.Buffers.Binary;

namespace Partner.Voice.Tts;

/// <summary>
/// 双层 WAV 解码器：16-bit 小端整数采样 → [-1,1] float。
/// </summary>
public static class WavPcm16Decoder
{
    /// <summary>
    /// 解析 16-bit PCM WAV，返回 [-1,1] float。头部按 RIFF 块遍历（fmt 可能在 data 前带扩展字段），
    /// 只认 <c>data</c> 块与 PCM 格式；不认识或不是 PCM 时抛 <see cref="InvalidDataException"/>。
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
                size = wav.Length - body; // 尾部块可能被截断
            }

            if (id == "fmt ")
            {
                var format = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body, 2));
                if (format != 1) // PCM only; WAVE_FORMAT_EXTENSIBLE (0xFFFE) 偶见于带扩展头的服务端。
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

            position = body + size + (size & 1); // 块按 2 字节对齐
        }

        if (bitsPerSample != 16 || channels < 1)
        {
            throw new InvalidDataException($"Unsupported PCM layout: {bitsPerSample}-bit, {channels} channel(s).");
        }

        data ??= Array.Empty<byte>();
        if (data.Length < 2)
        {
            return Array.Empty<float>(); // 空 data 块是合法响应（例如按了空白输入），不是错误。
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
            // 多声道取第一路：播放只需要单声道流，混合不是这里的职责。
            for (var i = 0; i < frames; i++)
            {
                var raw = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * channels * 2, 2));
                samples[i] = raw / 32768f;
            }
        }

        return samples;
    }
}
