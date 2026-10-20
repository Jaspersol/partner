namespace Partner.Voice.Tts;

/// <summary>Speech synthesis client.</summary>
public interface ITtsClient : IDisposable
{
    /// <summary>Streams PCM chunks (float32, <see cref="TtsOptions.SampleRate"/>) as they arrive.</summary>
    IAsyncEnumerable<float[]> SynthesizeAsync(string text, CancellationToken ct = default);
}
