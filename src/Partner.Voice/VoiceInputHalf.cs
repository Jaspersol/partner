using Partner.Voice.Asr;
using Partner.Voice.Audio;
using Partner.Voice.Perception;
using Partner.Voice.Playback;
using Partner.Voice.Settings;

namespace Partner.Voice;

/// <summary>One transcribed segment, with everything the queue item needs (design doc §2.2).</summary>
public sealed record VoiceUtterance
{
    public required string UtteranceId { get; init; }

    public required string DeviceId { get; init; }

    /// <summary>Process boot id: the stream clock resets on restart, so the key must include it.</summary>
    public required string BootId { get; init; }

    public required double SegStartMs { get; init; }

    public required double SegEndMs { get; init; }

    public required string Transcript { get; init; }

    public required BlockTrigger Trigger { get; init; }

    public required double AsrLatencyMs { get; init; }

    /// <summary><c>voice:{deviceId}:{bootId}:{segStartMs}</c> — see <see cref="BootId"/> for why.</summary>
    public string IdempotencyKey => $"voice:{DeviceId}:{BootId}:{SegStartMs:0}";
}

/// <summary>
/// Input half (design doc §2.8.1–§2.8.10): ring buffer → pause blocking → ASR → one utterance per segment.
/// <para>
/// <see cref="TickAsync"/> is public on purpose: the whole pipeline can be driven deterministically from a
/// synthetic source, which is the only way to test pause boundaries without a microphone.
/// </para>
/// </summary>
public sealed class VoiceInputHalf : IDisposable
{
    /// <summary>Shorter than this is not worth an ASR request.</summary>
    public const double MinBlockSeconds = 0.3;

    /// <summary>A block must contain at least this much speech, else it is noise.</summary>
    public const double MinVoicedFraction = 0.15;

    private readonly VoiceInputSetting _settings;
    private readonly IAudioSource _source;
    private readonly IAsrClient _asr;
    private readonly PlaybackGate _gate;
    private readonly Func<VoiceUtterance, Task> _onUtterance;
    private readonly Action<string> _log;
    private readonly List<VoiceUtterance> _utterances = new();

    /// <summary>唤醒命中时播放提示音的回调（宿主给 output sink 的 Enqueue）；null = 静默进会话。</summary>
    private readonly Action? _wakeTone;

    private bool _running;
    private bool _wasGated;
    private double _gateCursor;
    private bool _skippedHadSpeech;
    private bool _ambientChecked;

    // 唤醒门状态：_awake=false 时音频照常进环形缓冲，只是不推进停顿判定、不提交 ASR。
    // 唤醒会话在 G2（整段静默闭合）的那一 tick 回闸；中途提交（WindowCap/G1）不回闸。
    private bool _awake;

    public VoiceInputHalf(
        VoiceInputSetting settings,
        IAudioSource source,
        IAsrClient asr,
        PlaybackGate gate,
        Func<VoiceUtterance, Task> onUtterance,
        Action<string>? log = null,
        Action? wakeTone = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _onUtterance = onUtterance ?? throw new ArgumentNullException(nameof(onUtterance));
        _log = log ?? (_ => { });
        // 提示音是输出设备的职责，输入半区不该知道 sink 类型——只收一个动作。
        _wakeTone = settings.WakeTone ? wakeTone : null;

        Ring = new AudioRingBuffer(settings.WindowSeconds + 2.0, source.SampleRate);
        Detector = new PauseBlockingDetector(settings);
        DeviceId = source.DeviceName;
        BootId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}";
    }

    public AudioRingBuffer Ring { get; }

    public PauseBlockingDetector Detector { get; }

    public string DeviceId { get; }

    public string BootId { get; }

    /// <summary>An ASR request is in flight (one of the gate's seven conditions).</summary>
    public bool AsrBusy { get; private set; }

    /// <summary>A block has been submitted but its transcript has not been absorbed yet.</summary>
    public bool PendingBlock { get; private set; }

    public bool IsRunning => _running;

    /// <summary>Set when the ambient level already sits at or above the gate: silence can never be detected.</summary>
    public bool ThresholdMisconfigured { get; private set; }

    public double AmbientRms { get; private set; }

    /// <summary>最近 0.5 s 的输入电平（RMS）。死麦、安静房间、正在说话三者的区别只有它说得清。</summary>
    public double RecentRms { get; private set; }

    /// <summary>Speech was detected while the gate was closed (reported when the gate reopens).</summary>
    public bool DetectedSpeechWhileGated => _skippedHadSpeech;

    /// <summary>The wake gate is currently open (only meaningful when <see cref="VoiceInputSetting.Wake"/> is set).</summary>
    public bool Awake => _awake;

    public IReadOnlyList<VoiceUtterance> Utterances => _utterances;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        // The source is a pure device adapter; the ring is written here so any source works the same way.
        _source.Start(samples => Ring.Write(samples));
        _running = true;
        _log(
            $"voice: input started (device={DeviceId} threshold={_settings.SilenceThreshold} "
            + $"wake={(_settings.Wake is null ? "off" : "on")})");
    }

    public async Task RunAsync(TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);

        while (!ct.IsCancellationRequested)
        {
            await TickAsync(ct).ConfigureAwait(false);
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One tick: gate handling, then the pause state machine, then at most one ASR call.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = Ring.Seconds;
        Detector.NoteNow(now);

        // 电平条每 100 ms 刷一次：每 tick 都算 0.5 s 窗是白烧 CPU。门内门外都照样刷新——
        // 播放期间的电平同样要如实上报，否则「助手说话时麦克风还活着吗」就没法回答。
        if (_levelTicks == 0)
        {
            RecentRms = Ring.Energy(LevelWindowSeconds);
        }

        _levelTicks = (_levelTicks + 1) % LevelRefreshTicks;

        if (!_ambientChecked && now >= 0.5)
        {
            // Sampled on a quiet tick on purpose: taken unconditionally it lands inside the first sentence
            // and reports *speech* as the noise floor. "Quiet" therefore has to be checked, not assumed from
            // the clock — a user who starts talking right away would otherwise be told their threshold is
            // wrong, and a warning that cries wolf is worse than no warning. When no quiet window shows up
            // within AmbientProbeSeconds the report is made anyway, on the quietest window seen, and says so.
            var level = Ring.Energy(0.5);
            _quietestRms = Math.Min(_quietestRms, level);
            var quiet = level < _settings.SilenceThreshold;

            if (quiet || now >= AmbientProbeSeconds)
            {
                _ambientChecked = true;
                AmbientRms = _quietestRms;
                if (AmbientRms >= _settings.SilenceThreshold)
                {
                    ThresholdMisconfigured = true;
                    _log(
                        $"voice: threshold {_settings.SilenceThreshold:0.####} is not above the ambient level "
                        + $"{AmbientRms:0.####} — silence can never be detected, so no utterance will ever end "
                        + $"(no quiet window in the first {AmbientProbeSeconds:0} s)");
                }
            }
        }

        // While the assistant speaks, input is discarded wholesale (I4: the drop must be visible).
        if (_gate.Engaged)
        {
            if (!_wasGated)
            {
                _wasGated = true;
                _gateCursor = now;
            }

            var energy = Ring.Energy(0.05);
            _skippedHadSpeech |= energy >= _settings.SilenceThreshold;
            return;
        }

        // 唤醒门：未唤醒时只问 KWS，不推进停顿判定。音频仍进环形缓冲——一旦唤醒，
        // 最近的窗口（≤ WindowSeconds）立刻可用，唤醒词后紧跟着的话不会被掐头。
        if (_settings.Wake is not null && !_awake)
        {
            await PollWakeAsync(ct).ConfigureAwait(false);
            return;
        }

        if (_wasGated)
        {
            // Playback finished: drop the whole played span and resume immediately.
            _wasGated = false;
            var skipped = Math.Max(0, now - _gateCursor);
            _gate.NoteSkipped(skipped, _skippedHadSpeech);
            if (_skippedHadSpeech)
            {
                _log($"voice: discarded {skipped:0.0}s of audio captured while the assistant was speaking");
            }

            _skippedHadSpeech = false;
            Detector.OnGateReleased(now);
        }

        var decision = Detector.Step(now, Ring.Energy(0.05));

        // 唤醒会话结束（一段话被提交并被 Detector 闭合）之后回到监听态：下一段要先再喊一次唤醒词。
        if (decision.SegmentEnded)
        {
            if (_settings.Wake is not null && _awake)
            {
                _awake = false;
                _log("voice: wake session closed — waiting for the next wake word");
            }

            Detector.OnSegmentEnded(now);
            return;
        }

        if (decision.Submit)
        {
            await SubmitAsync(decision, ct).ConfigureAwait(false);
        }

        // 唤醒会话的关闭与提交结果解耦：G2（整段静默闭合）一发生就算说完，不管这一块最后值不值得
        // 转写。曾经只在 SubmitAsync 的成功路径里清 _awake，于是「只喊唤醒词、不说话」时块里全是静音
        // → SpeechFraction 不达标 → 提前 return → _awake 卡 true → 唤醒探针再也不跑、提示音只响一次，
        // 而闸门一直开着（此后每句话都不经唤醒词直接进 ASR）。
        if (_awake && decision.Trigger == BlockTrigger.UtteranceGap && decision.Pause)
        {
            _awake = false;
            _log("voice: wake session closed after the utterance — waiting for the next wake word");
        }
    }

    private async Task PollWakeAsync(CancellationToken ct)
    {
        // 探针读增量：从上次探针读到的位置起把新增的采集拿走。曾经每 tick 读「最近 320ms」再攒
        // 几个 tick 合并成立刻发送——那样相邻探针重叠 5/6，同一段音频被引擎一张流反复灌入，
        // 流式状态被污染，真实说话永远打不出命中（干净整段喂则一切正常）。增量读一次不错位。
        var delta = Ring.ReadSince(_wakeCursor, 2.0);
        if (delta is null || delta.Length < 16_000 * 0.10)
        {
            if (delta is null)
            {
                // 环形已被冲刷（窗口过期）：游标跟到当前流位置，重新起步，并按新流处理。
                _wakeCursor = Ring.Seconds;
                _wakeNeedsLead = true;
            }

            return; // 攒不满就不问：HTTP 频率被 100ms 增量摊薄到 ≤10 次/秒。
        }

        // 前置音频只在「引擎那条流是新的」时送。实测（同一个引擎、同一段音频）：
        // 裸增量喂新流不命中，前置 0.3s 也不行、1.0s 才命中——zipformer KWS 的解码器要用一小段
        // 左边上下文才能定住状态。但每帧都送就重复消费了：探针的帧是「[游标-1s, 游标] + 增量」，
        // 上一帧的增量正好落在这一帧的前置里，同一句话被同一条流听了两遍——两遍「小克」正好凑出
        // 双声词「小克小克」，于是喊一声也唤醒（实测：只首帧带前置 0 次命中，每帧都带 5 次）。
        // 所以：新流送一次前置把状态定住，之后只送增量。
        var lead = _wakeNeedsLead
            ? Ring.ReadSince(Math.Max(0, _wakeCursor - WakeLeadSeconds), WakeLeadSeconds)
              ?? Array.Empty<float>()
            : Array.Empty<float>();
        _wakeNeedsLead = false;

        var frame = new float[lead.Length + delta.Length];
        lead.CopyTo(frame, 0);
        delta.CopyTo(frame, lead.Length);

        _wakeCursor = Ring.Seconds; // 送出后才推进游标：没送出的增量下个 tick 还在原地等。

        try
        {
            var hit = await _settings.Wake!.PollAsync(frame, ct).ConfigureAwait(false);

            if (_wakeFailure is not null)
            {
                _wakeFailure = null;
                _log("voice: wake probe recovered");
            }

            if (hit.IsHit && !_awake)
            {
                _awake = true;

                // 引擎命中后会换一条新流重新听（同一条流会一直持有已匹配的前缀状态），
                // 所以下一次探针要重新送前置。
                _wakeNeedsLead = true;

                // 唤醒瞬间相当于一段新语音的开始：重置判定，让唤醒词后的话从干净状态进停顿机。
                // 用 OnWakeHit 而不是 OnGateReleased——唤醒词本身就是「已经听到人说话」，段必须
                // 立起 dirty，否则喊完词不说话时 G2 永不成立，会话卡死（见 TickAsync 收尾注释）。
                // 游标回溯 WakeCatchUpSeconds：命中是延迟报出来的，命令往往已经说在命中点之前。
                // 回溯窗里已经是成片人声 = 命令和唤醒词一口气说的，宽限期不必再等（见 WakeCatchUpSeconds）。
                var catchUpStart = Math.Max(0, Ring.Seconds - WakeCatchUpSeconds);
                var heardSpeechAtHit =
                    Ring.SpeechFraction(_settings.SilenceThreshold, WakeCatchUpSeconds) >= WakeCatchUpSpeechFraction;
                Detector.OnWakeHit(catchUpStart, heardSpeechAtHit);
                _wakeTone?.Invoke();
                _log($"voice: wake word \"{hit.Keyword}\" ({hit.Confidence:0.00}) — listening");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // KWS 失联必须可见，但该半区不能死：下一 tick 会再问，服务恢复后自动跟回。
            // 只在「第一次」和「换了原因」时打：引擎挂掉时每 tick 一条会把 stderr 淹掉。
            if (!string.Equals(_wakeFailure, ex.Message, StringComparison.Ordinal))
            {
                _wakeFailure = ex.Message;
                _log($"voice: wake poll failed: {ex.Message}");
            }

            // 引擎可能刚重启：它那条流没了，前置得重新送一次。
            _wakeNeedsLead = true;
        }
    }

    /// <summary>上次探针读到的流位置（Ring.Seconds）；探针从它起读增量，不重叠。</summary>
    private double _wakeCursor;

    /// <summary>
    /// 下一次探针要不要带前置音频。只在引擎那条流是新的时为真（首帧、命中换流、引擎重启、游标重同步）；
    /// 每帧都带会让同一条流把每句话听两遍，双声词于是能被单声喊醒。
    /// </summary>
    private bool _wakeNeedsLead = true;

    /// <summary>上一次打过的探针失败原因；相同原因不再重复打（引擎挂掉时每 tick 一条会淹掉 stderr）。</summary>
    private string? _wakeFailure;

    /// <summary>电平条刷新周期（tick 数）：20 ms 一 tick 时即 100 ms。</summary>
    private const int LevelRefreshTicks = 5;

    /// <summary>电平条取样窗口（秒）：太短会跳，太长会把上一句话的电平拖进静音段。</summary>
    private const double LevelWindowSeconds = 0.5;

    /// <summary>底噪探测的最长等待（秒）：这段时间里没有一帧低于阈值，就按「误配」如实上报。</summary>
    private const double AmbientProbeSeconds = 5.0;

    /// <summary>底噪探测期内见过的最安静的一帧；安静帧一直等不到时用它下结论。</summary>
    private double _quietestRms = double.MaxValue;

    private int _levelTicks;

    /// <summary>
    /// 唤醒探针前置音频长度（秒）。KWS 解码器要靠左边上下文定状态，实测 0.3s 前置打不出命中、
    /// 1.0s 才稳——取 1s 与实测一致，不靠猜。这段音频会重复喂给流的同一位置，无害。
    /// </summary>
    private const double WakeLeadSeconds = 1.0;

    /// <summary>
    /// 唤醒命中时块起点回溯的秒数：覆盖 KWS 的 chunk 级检测延迟。官方包只有 chunk-16（≈320 ms），
    /// 加上探针 100 ms 节奏的排队，命中报出来的位置实测落在命令开头之后 320–440 ms。
    /// <para>
    /// 游标原定在命中时刻（<c>Ring.Seconds</c>），于是命中点之前的音频全被丢：连读「小克小克打开灯」时
    /// 命令的首段进不了块；命令短到整句都落在延迟窗里时，块里只剩静音 → <c>SpeechFraction</c> 不达标
    /// → 提前 return，命令一个字都进不了 ASR，会话随即回闸。这就是「唤醒后说的话没进对话」。
    /// </para>
    /// <para>
    /// 回溯的代价是唤醒词尾巴（≤ 这个值）可能一起被转写进去。宁可有半句唤醒词，也不能丢整句命令。
    /// </para>
    /// </summary>
    private const double WakeCatchUpSeconds = 0.45;

    /// <summary>
    /// 判定「回溯窗里已经是成片人声」的比例下限，用来决定命中后还要不要走唤醒宽限期。
    /// <para>
    /// 取 0.5 而不是提交门的 <see cref="MinVoicedFraction"/>（0.15）：唤醒词的尾巴本身就在回溯窗的头部
    /// （chunk-16 的 320 ms 延迟意味着它占满窗的 ~29%），0.15 会把「喊完词停着不说话」也判成人声，
    /// 于是宽限期失效——那正是 b7396f8 修掉的老 bug。0.5 让「唤醒词尾巴 + 命令」这种成片的凑得够，
    /// 而「尾巴 + 静默」凑不够。
    /// </para>
    /// </summary>
    private const double WakeCatchUpSpeechFraction = 0.5;


    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _source.Stop();
        _log("voice: input stopped");
    }

    public void Dispose()
    {
        Stop();
        _source.Dispose();
        _asr.Dispose();
    }

    private async Task SubmitAsync(PauseDecision decision, CancellationToken ct)
    {
        var start = decision.BlockStart;
        var end = decision.BlockEnd;
        var duration = end - start;

        if (duration < MinBlockSeconds)
        {
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        var audio = Ring.ReadSince(start, _settings.WindowSeconds);
        if (audio is null)
        {
            _log($"voice: audio cursor evicted, dropped {duration:0.0}s and resynchronized");
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        if (Ring.SpeechFraction(_settings.SilenceThreshold, duration) < MinVoicedFraction)
        {
            // Pure silence or noise: not worth a request.
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
            return;
        }

        AsrBusy = true;
        PendingBlock = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var text = await _asr.TranscribeAsync(audio, ct).ConfigureAwait(false);
            watch.Stop();

            if (!string.IsNullOrWhiteSpace(text))
            {
                var utterance = new VoiceUtterance
                {
                    UtteranceId = Guid.NewGuid().ToString("n"),
                    DeviceId = DeviceId,
                    BootId = BootId,
                    SegStartMs = start * 1000,
                    SegEndMs = end * 1000,
                    Transcript = text.Trim(),
                    Trigger = decision.Trigger,
                    AsrLatencyMs = watch.Elapsed.TotalMilliseconds,
                };

                _utterances.Add(utterance);
                await _onUtterance(utterance).ConfigureAwait(false);
            }

            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Queued);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed transcription must be visible; it must not become an empty utterance.
            _log($"voice: transcription failed for {duration:0.0}s of audio: {ex.Message}");
            Detector.OnSubmitted(end, decision.Pause, SubmissionOutcome.Skipped);
        }
        finally
        {
            AsrBusy = false;
            PendingBlock = false;
        }
    }
}
