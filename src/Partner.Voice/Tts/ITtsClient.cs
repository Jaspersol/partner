namespace Partner.Voice.Tts;

/// <summary>语音合成客户端（设计文档 §2.8.10）。</summary>
public interface ITtsClient : IDisposable
{
    /// <summary>Streams PCM chunks (float32, <see cref="TtsOptions.SampleRate"/>) as they arrive.</summary>
    IAsyncEnumerable<float[]> SynthesizeAsync(string text, CancellationToken ct = default);
}
