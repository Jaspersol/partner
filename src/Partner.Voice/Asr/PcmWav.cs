using System.Buffers.Binary;

namespace Partner.Voice.Asr;

/// <summary>
/// 把 [-1,1] 的 float 采样写成 16-bit 单声道 PCM WAV（RIFF 头手工拼，不引第三方库）。
/// 两个 ASR 客户端（chat-audio 内联与 openai multipart）共用这一份编码。
/// </summary>
internal static class PcmWav
{
    public static byte[] Encode16(float[] pcm, int sampleRate)
    {
        const int headerSize = 44;
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataBytes = pcm.Length * sizeof(short);
        var wav = new byte[headerSize + dataBytes];

        System.Text.Encoding.ASCII.GetBytes("RIFF", wav.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataBytes);
        System.Text.Encoding.ASCII.GetBytes("WAVE", wav.AsSpan(8, 4));
        System.Text.Encoding.ASCII.GetBytes("fmt ", wav.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16); // fmt chunk 长度
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), sampleRate * channels * (bitsPerSample / 8)); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), (short)(channels * (bitsPerSample / 8)));      // block align
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
        System.Text.Encoding.ASCII.GetBytes("data", wav.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);

        for (var i = 0; i < pcm.Length; i++)
        {
            var sample = (short)Math.Clamp((int)MathF.Round(pcm[i] * short.MaxValue), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(headerSize + (i * sizeof(short)), 2), sample);
        }

        return wav;
    }
}
