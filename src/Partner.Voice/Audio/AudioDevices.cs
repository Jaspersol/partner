namespace Partner.Voice.Audio;

/// <summary>Mono float32 capture source. The device is behind an interface so the pipeline is testable.</summary>
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }

    /// <summary>Human-readable device name, for the manifest and diagnostics.</summary>
    string DeviceName { get; }

    /// <summary>Starts delivering samples. The callback must not block (it runs on the device thread).</summary>
    void Start(Action<float[]> onSamples);

    void Stop();

    bool IsRunning { get; }
}

/// <summary>Playback sink. Holds audio while <see cref="Hold"/> is set (the "don't speak yet" latch).</summary>
public interface IAudioSink : IDisposable
{
    int SampleRate { get; }

    string DeviceName { get; }

    void Start();

    void Enqueue(float[] pcm, int sampleRate, bool final = false);

    /// <summary>Latches playback: nothing is written while held.</summary>
    void Hold();

    void Release();

    bool IsPlaying { get; }

    bool HasPending { get; }

    double DrainMs { get; set; }

    void Stop();
}

/// <summary>
/// Scripted audio source: feeds a fixed timeline of frames, so the pause logic can be driven
/// deterministically without a microphone.
/// </summary>
public sealed class SyntheticAudioSource(int sampleRate = 16_000, string deviceName = "synthetic") : IAudioSource
{
    private Action<float[]>? _sink;
    private bool _running;

    public int SampleRate { get; } = sampleRate;

    public string DeviceName { get; } = deviceName;

    public bool IsRunning => _running;

    /// <summary>
    /// Makes <see cref="Start"/> throw the way a machine with no capture endpoint does. Opening the device
    /// succeeds and starting it fails, which is the order the real adapter has (both checks exist there); a test
    /// that only covered the first would not prove the host survives the second.
    /// </summary>
    public bool RefuseToStart { get; set; }

    public void Start(Action<float[]> onSamples)
    {
        ArgumentNullException.ThrowIfNull(onSamples);

        if (RefuseToStart)
        {
            throw new InvalidOperationException("No capture device is available.");
        }

        _sink = onSamples;
        _running = true;
    }

    public void Stop() => _running = false;

    /// <summary>Pushes one frame (e.g. 20 ms) into the pipeline.</summary>
    public void Emit(float amplitude, int samples)
    {
        if (!_running || _sink is null)
        {
            throw new InvalidOperationException("Source is not running.");
        }

        var frame = new float[samples];
        if (amplitude != 0)
        {
            // A tone rather than a constant: RMS of a constant is the constant, which makes threshold
            // tests misleadingly exact.
            for (var i = 0; i < samples; i++)
            {
                frame[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            }
        }

        _sink(frame);
    }

    public void Dispose() => Stop();
}

/// <summary>Recording sink: keeps every enqueued segment so tests can assert order and content.</summary>
public sealed class SyntheticAudioSink(int sampleRate = 24_000, string deviceName = "synthetic") : IAudioSink
{
    private readonly List<float[]> _segments = new();
    private bool _held;
    private bool _running;

    public int SampleRate { get; } = sampleRate;

    public string DeviceName { get; } = deviceName;

    public double DrainMs { get; set; } = 400;

    public IReadOnlyList<float[]> Segments => _segments;

    public bool IsPlaying { get; private set; }

    public bool HasPending { get; private set; }

    public bool IsRunning => _running;

    /// <summary>Makes <see cref="Start"/> throw the way a machine with no playback endpoint does.</summary>
    public bool RefuseToStart { get; set; }

    public void Start()
    {
        if (RefuseToStart)
        {
            throw new InvalidOperationException("No playback device is available.");
        }

        _running = true;
    }

    public void Enqueue(float[] pcm, int sampleRate, bool final = false)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        HasPending = true;
        if (_held)
        {
            // Held audio is buffered, not dropped: the segment still has to be spoken.
            _segments.Add(pcm);
            return;
        }

        _segments.Add(pcm);
        IsPlaying = true;
        HasPending = !final;
    }

    /// <summary>Whether playback is latched, so a test can see when a stream was let go.</summary>
    public bool IsHeld => _held;

    public void Hold() => _held = true;

    public void Release() => _held = false;

    /// <summary>Simulates the device finishing its buffer (tests need to drain without a real device).</summary>
    public void MarkDrained()
    {
        IsPlaying = false;
        HasPending = false;
    }

    public void Stop()
    {
        _running = false;
        IsPlaying = false;
        HasPending = false;
    }

    public void Dispose() => Stop();
}
