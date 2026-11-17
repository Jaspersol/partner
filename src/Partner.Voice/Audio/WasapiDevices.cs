using NAudio.Wave;
using Partner.Voice.Settings;

namespace Partner.Voice.Audio;

/// <summary>
/// WASAPI capture: 16 kHz mono float32 into the ring buffer.
/// <para>
/// The device callback only copies samples — no allocation-heavy work, no I/O, no logging — because it runs
/// on the audio thread and anything slow there produces dropouts.
/// </para>
/// </summary>
public sealed class WasapiAudioSource : IAudioSource
{
    private readonly WaveInEvent _capture;
    private Action<float[]>? _onSamples;
    private bool _running;

    public WasapiAudioSource(VoiceInputSetting setting)
    {

        ArgumentNullException.ThrowIfNull(setting);


        SampleRate = 16000;
        DeviceNumber = ResolveDevice(setting.DeviceId, out var name);
        DeviceName = name;

        _capture = new WaveInEvent
        {
            DeviceNumber = DeviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
        };

        _capture.DataAvailable += OnDataAvailable;
    }

    public int SampleRate { get; }

    public int DeviceNumber { get; }

    public string DeviceName { get; }

    public bool IsRunning => _running;

    public static int DeviceCount => WaveInEvent.DeviceCount;

    public static IReadOnlyList<string> ListDevices()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }

        return names;
    }

    /// <summary>Resolves a device by name (partial, case/space-insensitive) or falls back to the default.</summary>
    public static int ResolveDevice(string? requested, out string resolvedName)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var candidate = WaveInEvent.GetCapabilities(i).ProductName;
                if (Normalize(candidate).Contains(Normalize(requested), StringComparison.OrdinalIgnoreCase))
                {
                    resolvedName = candidate;
                    return i;
                }
            }
        }

        resolvedName = WaveInEvent.DeviceCount > 0 ? WaveInEvent.GetCapabilities(0).ProductName : "(none)";
        return 0;
    }

    public void Start(Action<float[]> onSamples)
    {
        _onSamples = onSamples ?? throw new ArgumentNullException(nameof(onSamples));

        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("No capture device is available.");
        }

        _capture.StartRecording();
        _running = true;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _capture.StopRecording();
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        var samples = new float[args.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            // 16-bit little-endian PCM → float in [-1, 1).
            var value = (short)(args.Buffer[(i * 2) + 1] << 8 | args.Buffer[i * 2]);
            samples[i] = value / 32768f;
        }

        // The ring is the single hand-off point: the source stays a pure device adapter.
        _onSamples?.Invoke(samples);
    }

    private static string Normalize(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}

/// <summary>
/// WASAPI playback: one reused output stream per sample rate, held while the segment
/// is still being synthesized, with a drain hold-off so the gate does not reopen into the device buffer.
/// </summary>
public sealed class WasapiAudioSink : IAudioSink
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Func<IWavePlayer> _createPlayer;
    private readonly bool _checkDevice;
    private BufferedWaveProvider? _provider;
    private IWavePlayer? _player;
    private Exception? _failure;
    private int _openRate;
    private int _generation;
    private bool _held;
    private bool _running;
    private bool _disposed;
    private bool _wasBuffered;
    private DateTimeOffset _busyUntil = DateTimeOffset.MinValue;

    public WasapiAudioSink(VoiceOutputSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        DeviceNumber = ResolveDevice(setting.DeviceId, out var name);
        DeviceName = name;
        _createPlayer = () => new WaveOutEvent { DeviceNumber = DeviceNumber };
        _checkDevice = true;
        DrainMs = setting.DrainMs;
    }

    /// <summary>Creates a sink with a supplied playback device, for hosts with a custom player.</summary>
    public WasapiAudioSink(VoiceOutputSetting setting, Func<IWavePlayer> createPlayer)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _createPlayer = createPlayer ?? throw new ArgumentNullException(nameof(createPlayer));
        DeviceName = "custom";
        DrainMs = setting.DrainMs;
    }

    public int SampleRate { get; private set; } = 24_000;
    public int DeviceNumber { get; }
    public string DeviceName { get; }
    public double DrainMs { get; set; }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                ThrowIfFailed();
                var buffered = !_held && _provider?.BufferedBytes > 0;
                if (buffered) _wasBuffered = true;
                else if (_wasBuffered && !_held)
                {
                    _wasBuffered = false;
                    _busyUntil = DateTimeOffset.UtcNow.AddMilliseconds(DrainMs);
                }
                return buffered || DateTimeOffset.UtcNow < _busyUntil;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                ThrowIfFailed();
                return _provider?.BufferedBytes > 0;
            }
        }
    }

    public static IReadOnlyList<string> ListDevices()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            names.Add(WaveOut.GetCapabilities(i).ProductName);
        }
        return names;
    }

    public static int ResolveDevice(string? requested, out string resolvedName)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                var candidate = WaveOut.GetCapabilities(i).ProductName;
                if (candidate.Contains(requested, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedName = candidate;
                    return i;
                }
            }
        }
        resolvedName = WaveOut.DeviceCount > 0 ? WaveOut.GetCapabilities(0).ProductName : "(none)";
        return 0;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFailed();
            if (_running) return;
            if (_checkDevice && WaveOut.DeviceCount == 0) throw new InvalidOperationException("No playback device is available.");
            _running = true;
        }
    }

    public void Enqueue(float[] pcm, int sampleRate, bool final = false) =>
        EnqueueAsync(pcm, sampleRate, final).GetAwaiter().GetResult();

    public async Task EnqueueAsync(float[] pcm, int sampleRate, bool final = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFailed();
            generation = _generation;
            if (!_running) throw new OperationCanceledException("Playback was stopped.");
        }
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = new byte[Math.Min(pcm.Length, 4096) * 2];
            var offset = 0;
            while (offset < pcm.Length)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    ThrowIfFailed();
                    if (generation != _generation || !_running) throw new OperationCanceledException("Playback was stopped.");
                    // A rate change must drain the previous stream, never discard its tail.
                    if (_provider is null || _openRate == sampleRate || _provider.BufferedBytes == 0)
                    {
                        EnsureStream(sampleRate);
                        var count = Math.Min(pcm.Length - offset, Math.Min(4096, (_provider!.BufferLength - _provider.BufferedBytes) / 2));
                        if (count > 0)
                        {
                            for (var i = 0; i < count; i++)
                            {
                                var value = (short)(Math.Clamp(pcm[offset + i], -1f, 1f) * 32767);
                                bytes[i * 2] = (byte)value;
                                bytes[i * 2 + 1] = (byte)(value >> 8);
                            }
                            _provider.AddSamples(bytes, 0, count * 2);
                            if (!_held) _wasBuffered = true;
                            offset += count;
                        }
                    }
                }
                if (offset < pcm.Length) await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
        finally { _writer.Release(); }
    }

    public void Hold() { lock (_gate) { _held = true; _player?.Pause(); } }

    public void Release()
    {
        lock (_gate)
        {
            ThrowIfFailed();
            _held = false;
            if (_provider?.BufferedBytes > 0) _wasBuffered = true;
            _player?.Play();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _running = false;
            _held = false;
            CloseStream();
            _wasBuffered = false;
            _busyUntil = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _running = false;
            CloseStream();
        }
    }

    private void CloseStream()
    {
        var player = _player;
        _player = null;
        _provider = null;
        _openRate = 0;
        if (player is not null)
        {
            player.PlaybackStopped -= OnPlaybackStopped;
            player.Stop();
            player.Dispose();
        }
    }

    private void EnsureStream(int rate)
    {
        if (_player is not null && _openRate == rate) return;
        CloseStream();
        _provider = new BufferedWaveProvider(new WaveFormat(rate, 16, 1))
        {
            DiscardOnBufferOverflow = false,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        _player = _createPlayer();
        _player.PlaybackStopped += OnPlaybackStopped;
        _player.Init(_provider);
        if (!_held) _player.Play();
        _openRate = rate;
        SampleRate = rate;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Interlocked.CompareExchange(ref _failure, args.Exception, null);
    }

    private void ThrowIfFailed()
    {
        var error = Volatile.Read(ref _failure);
        if (error is not null) throw new InvalidOperationException("Audio playback failed.", error);
    }
}
