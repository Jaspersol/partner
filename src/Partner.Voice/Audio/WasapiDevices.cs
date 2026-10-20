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
    private readonly Queue<(float[] Pcm, int Rate)> _queue = new();
    private readonly Thread _pump;
    private readonly AutoResetEvent _signal = new(false);
    private BufferedWaveProvider? _provider;
    private IWavePlayer? _player;
    private int _openRate;

    // The latch is set by each turn's FlushTurnAsync: the sink instance is reused across turns, so the
    // field starts false.
    private bool _held;
    private bool _running;
    private bool _disposed;
    private DateTimeOffset _busyUntil = DateTimeOffset.MinValue;

    public WasapiAudioSink(VoiceOutputSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        DeviceNumber = ResolveDevice(setting.DeviceId, out var name);
        DeviceName = name;
        DrainMs = setting.DrainMs;
        _pump = new Thread(Pump) { IsBackground = true, Name = "voice-playback" };
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
                return (_player?.PlaybackState == PlaybackState.Playing && _provider?.BufferedBytes > 0)
                    || DateTimeOffset.UtcNow < _busyUntil;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count > 0;
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
        if (_running)
        {
            return;
        }

        if (WaveOut.DeviceCount == 0)
        {
            throw new InvalidOperationException("No playback device is available.");
        }

        _running = true;
        _pump.Start();
    }

    public void Enqueue(float[] pcm, int sampleRate, bool final = false)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        lock (_gate)
        {
            _queue.Enqueue((pcm, sampleRate));
        }

        _signal.Set();
    }

    public void Hold()
    {
        lock (_gate)
        {
            _held = true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _held = false;
        }

        _signal.Set();
    }

    public void Stop()
    {
        _running = false;
        _signal.Set();

        lock (_gate)
        {
            _player?.Stop();
            _player?.Dispose();
            _player = null;
            _provider = null;
            _openRate = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _signal.Dispose();
    }

    private void Pump()
    {
        while (_running)
        {
            (float[] Pcm, int Rate) item;

            lock (_gate)
            {
                if (_held || _queue.Count == 0)
                {
                    item = (Array.Empty<float>(), 0);
                }
                else
                {
                    item = _queue.Dequeue();
                }
            }

            if (item.Rate == 0)
            {
                _signal.WaitOne(50);
                continue;
            }

            EnsureStream(item.Rate);
            Write(item.Pcm, item.Rate);
        }
    }

    private void EnsureStream(int rate)
    {
        lock (_gate)
        {
            // One stream per sample rate, reused: reopening per sentence costs 20–240 ms and leaks the old one.
            if (_player is not null && _openRate == rate)
            {
                return;
            }

            _player?.Dispose();
            _provider = new BufferedWaveProvider(new WaveFormat(rate, 16, 1))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(30),
            };
            _player = new WaveOutEvent { DeviceNumber = DeviceNumber };
            _player.Init(_provider);
            _player.Play();
            _openRate = rate;
            SampleRate = rate;
        }
    }

    private void Write(float[] pcm, int rate)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            var clamped = Math.Clamp(pcm[i], -1f, 1f);
            var value = (short)(clamped * 32767);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        lock (_gate)
        {
            _provider?.AddSamples(bytes, 0, bytes.Length);
            // stream.Write is effectively blocking: by the time we return, the audio has been sounding,
            // so only the device buffer's residual audio remains.
            _busyUntil = DateTimeOffset.UtcNow.AddMilliseconds(DrainMs);
        }
    }
}
