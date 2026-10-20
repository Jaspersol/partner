namespace Partner.Voice.Asr;

/// <summary>Speech recognition client.</summary>
public interface IAsrClient : IDisposable
{
    /// <summary>Non-streaming transcription of one complete audio block.</summary>
    Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default);

    /// <summary>SSE deltas of the growing transcript for one block.</summary>
    IAsyncEnumerable<string> TranscribeStreamAsync(float[] pcm, CancellationToken ct = default);
}
