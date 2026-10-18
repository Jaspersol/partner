using Partner.Voice.Settings;

namespace Partner.Voice.Perception;

public enum BlockTrigger
{
    None,

    /// <summary>G1: a sentence-level pause (streaming mode only).</summary>
    SentenceGap,

    /// <summary>G2: the utterance is over.</summary>
    UtteranceGap,

    /// <summary>Continuous speech hit <c>windowSeconds</c>: cut so the block cannot grow forever.</summary>
    WindowCap,

    /// <summary>The caller asked for the current block (used when a stop must flush rather than discard).</summary>
    Manual,
}

/// <summary>What one tick decided. The caller performs the submission and reports the outcome back.</summary>
public sealed record PauseDecision
{
    public required bool Submit { get; init; }

    public required double BlockStart { get; init; }

    public required double BlockEnd { get; init; }

    /// <summary>Pause-triggered block: its transcript gets a sentence boundary appended.</summary>
    public required bool Pause { get; init; }

    public required BlockTrigger Trigger { get; init; }

    /// <summary>The segment is over: flush, release playback, close the gate.</summary>
    public required bool SegmentEnded { get; init; }

    public static PauseDecision None(double cursor) => new()
    {
        Submit = false,
        BlockStart = cursor,
        BlockEnd = cursor,
        Pause = false,
        Trigger = BlockTrigger.None,
        SegmentEnded = false,
    };
}

/// <summary>
/// The pause/blocking state machine (design doc §2.8.2 and §2.8.3). Time and energy are passed in, so the
/// whole thing is deterministic and testable without audio hardware.
/// <para>
/// The two knobs mean "the pause you actually make": the measured silence is short by one energy window
/// plus the poll cadence, so a fixed offset is added back.
/// </para>
/// </summary>
public sealed class PauseBlockingDetector
{
    /// <summary>Compensation for the energy window + poll cadence (design doc: 100 ms).</summary>
    public const double SilenceOffsetMs = 100;

    /// <summary>
    /// 宽限期内「用户开口了」的最短有声时长（毫秒）：同一簇声音里**累计**这么久过阈值才算数。
    /// <para>
    /// 单个能量窗不够：提示音回灌、咳嗽、键盘、桌面磕碰都能让一个 50 ms 窗超阈值，
    /// 而宽限期一旦被清掉，静默就按判句间隔计——人还没开口会话就关了。
    /// </para>
    /// <para>
    /// 必须是累计、不能是连续：汉语一个音节过阈值只有 60–190 ms，音节之间就掉到阈值以下（实测 VB-Cable
    /// 喂「今天天气怎么样」，最长一段连续有声 190 ms）。要求连续 200 ms 时整句命令都打不掉宽限期，
    /// 块被拖到最后一个音节后 5 s，<c>SpeechFraction</c> 稀释到 0.08，命令被当噪声丢掉（真机复现）。
    /// </para>
    /// </summary>
    public const double WakeOnsetMs = 200;

    /// <summary>
    /// 同一簇声音里允许的最长静默（秒）：短于它的间隙算音节间隙，累计不清零；长于它算新的一簇。
    /// 实测音节间隙 ≤ 140 ms；300 ms 把它们连起来，又不会把隔了几秒的两下磕碰攒成一次「开口」。
    /// </summary>
    public const double OnsetBridgeSeconds = 0.3;

    /// <summary>
    /// 宽限期结束时块起点回退到这簇声音开头之前多少秒：辅音起音低于阈值，不留余量会把首字掐掉。
    /// </summary>
    public const double OnsetPreRollSeconds = 0.3;

    /// <summary>调用方传进来的能量窗长度（毫秒）：一个窗过阈值时，人声最早在窗头就开始了。</summary>
    public const double EnergyWindowMs = 50;

    /// <summary>单个 tick 计入累计的上限（秒）：tick 卡顿时不让一次长间隔把累计一下子撑满。</summary>
    private const double MaxTickSeconds = 0.1;

    private readonly VoiceInputSetting _settings;
    private readonly double _sentenceGapMs;
    private readonly double _utteranceGapMs;
    private readonly double _windowSeconds;
    private readonly double _wakeTimeoutMs;

    private double? _quietSince;
    private bool _dirty;
    private bool _called;
    private bool _leftover;
    private bool? _heldPause;

    /// <summary>
    /// 当前这一段「有声音」的判定只来自唤醒词本身：还没等到真正的一句话。
    /// <para>
    /// 与 <see cref="_dirty"/> 分开是因为两者要的静默长度不同：唤醒词之后用户要想一下才开口，
    /// 用判句的 <see cref="VoiceInputSetting.UtteranceGapMs"/> 去关会话，人还没出声闸门就合上了。
    /// </para>
    /// </summary>
    private bool _awaitingSpeech;

    /// <summary>宽限期内当前这一簇声音的起点（流时钟）；簇的定义见 <see cref="OnsetBridgeSeconds"/>。</summary>
    private double? _onsetClusterStart;

    /// <summary>当前这一簇里累计的有声秒数，攒够 <see cref="WakeOnsetMs"/> 即算开口。</summary>
    private double _onsetVoicedSeconds;

    /// <summary>宽限期内上一次过阈值（且不在屏蔽窗里）的 tick 时刻；用来判断间隙是否超过 <see cref="OnsetBridgeSeconds"/>。</summary>
    private double? _onsetLastLoud;

    public PauseBlockingDetector(VoiceInputSetting settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sentenceGapMs = settings.SentenceGapMs;
        _utteranceGapMs = settings.UtteranceGapMs;
        _windowSeconds = settings.WindowSeconds;
        _wakeTimeoutMs = settings.WakeTimeoutMs;
        Cursor = 0;
    }

    /// <summary>Start of the pending audio block (stream seconds).</summary>
    public double Cursor { get; private set; }

    /// <summary>True once speech has been heard in the current segment.</summary>
    public bool Dirty => _dirty;

    /// <summary>The previous submission was cut short by the ring cap: audio is still owed.</summary>
    public bool HasLeftover => _leftover;

    /// <summary>A block was refused by a full backlog and is still owed.</summary>
    public bool HasHeldPause => _heldPause is not null;

    /// <summary>
    /// One tick. <paramref name="nowSeconds"/> is the stream clock, <paramref name="energy"/> the RMS.
    /// <para>
    /// <paramref name="suppressSpeechOnset"/> 为真时，这一 tick 的有声不算「用户开口」：唤醒提示音由输出设备
    /// 外放，非耳机时会被麦克风收回来，幅度轻松超过阈值——不屏蔽就等于「唤醒瞬间用户就开口了」，宽限期当场
    /// 失效，会话在提示音结束后约 1.1 s 关上，此后说的命令一个字都进不了 ASR（真机实测）。音频照常计入块，
    /// 只是不从这里清 <see cref="_awaitingSpeech"/>。
    /// </para>
    /// </summary>
    public PauseDecision Step(double nowSeconds, double energy, bool suppressSpeechOnset = false)
    {
        // The tick time is recorded here so callers cannot forget it: it is what decides whether a
        // submission left audio behind.
        var tickSeconds = Math.Clamp(nowSeconds - (_prevStep ?? nowSeconds), 0, MaxTickSeconds);
        _prevStep = nowSeconds;
        _lastNow = nowSeconds;

        var quiet = energy < _settings.SilenceThreshold;
        double silenceMs;

        // Step order matters (design doc §2.8.3): an owed block is retried before anything moves the cursor.
        if (_heldPause is not null)
        {
            return new PauseDecision
            {
                Submit = true,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = _heldPause.Value,
                Trigger = BlockTrigger.Manual,
                SegmentEnded = false,
            };
        }

        if (quiet)
        {
            _quietSince ??= nowSeconds;
            silenceMs = ((nowSeconds - _quietSince.Value) * 1000) + SilenceOffsetMs;

            // Nothing awake and nothing owed: the audio behind the cursor is silence only, so drop it.
            if (!_dirty && !_leftover)
            {
                Cursor = nowSeconds;
            }
        }
        else
        {
            _quietSince = null;
            _called = false;
            silenceMs = 0;
            _dirty = true;

            // 真听到人说话了：宽限期结束，此后按正常的判句静默收尾。
            // 要求一簇声音累计 WakeOnsetMs 而不是「一个能量窗」：见 WakeOnsetMs。
            if (_awaitingSpeech && !suppressSpeechOnset)
            {
                NoteOnsetCandidate(nowSeconds, tickSeconds);
            }
        }

        // Long unbroken speech: submit anyway so the block cannot grow past the window.
        if (!quiet
            && (_leftover || nowSeconds - Cursor >= _windowSeconds)
            && _heldPause is null)
        {
            return new PauseDecision
            {
                Submit = true,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = false,
                Trigger = BlockTrigger.WindowCap,
                SegmentEnded = false,
            };
        }

        // G1 (streaming only): one complete sentence per request. 唤醒后还没等到人开口时不算句界——
        // 那 450 ms 的静默是「用户在组织句子」，切出来的是纯静音块，白费一次判句。
        if (_settings.AsrStream
            && quiet
            && !_awaitingSpeech
            && silenceMs >= _sentenceGapMs
            && !_called
            && _heldPause is null)
        {
            return new PauseDecision
            {
                Submit = true,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = true,
                Trigger = BlockTrigger.SentenceGap,
                SegmentEnded = false,
            };
        }

        // G2: the utterance is over in both modes. 唤醒后还没等到人开口时用宽限期而不是判句间隔：
        // 提示音到人开口之间那段沉默不是「说完了」，按判句间隔去关会话会在人出声之前就合上闸门。
        var gapMs = _awaitingSpeech ? _wakeTimeoutMs : _utteranceGapMs;
        if (quiet && silenceMs >= gapMs && _dirty && _heldPause is null)
        {
            var submit = _leftover || !_settings.AsrStream;

            return new PauseDecision
            {
                Submit = submit,
                BlockStart = Cursor,
                BlockEnd = nowSeconds,
                Pause = true,
                Trigger = BlockTrigger.UtteranceGap,

                // The segment only ends once nothing is owed.
                SegmentEnded = !submit,
            };
        }

        return PauseDecision.None(Cursor);
    }

    /// <summary>Reports the outcome of a submission so the cursor and the owed-block state can advance.</summary>
    public void OnSubmitted(double end, bool pause, SubmissionOutcome outcome)
    {
        switch (outcome)
        {
            case SubmissionOutcome.Queued:
            case SubmissionOutcome.Skipped:
                Cursor = end;
                _leftover = end < _lastNow;
                _called = true;
                _heldPause = null;
                if (outcome == SubmissionOutcome.Queued)
                {
                    _dirty = true;
                }

                break;

            case SubmissionOutcome.Busy:
                // Refused: keep the cursor and the block's pause flag, retry next tick.
                _heldPause = pause;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    /// <summary>Called when the segment ends: flush happened, playback released, gate closed.</summary>
    public void OnSegmentEnded(double nowSeconds)
    {
        Cursor = nowSeconds;
        _leftover = false;
        _heldPause = null;
        _dirty = false;
        _awaitingSpeech = false;
        ResetOnset();
        _quietSince = null;
        _called = false;
    }

    /// <summary>Called when the gate reopens: the audio spoken during playback is discarded wholesale.</summary>
    public void OnGateReleased(double nowSeconds)
    {
        Cursor = nowSeconds;
        _heldPause = null;
        _leftover = false;
        _awaitingSpeech = false;
        ResetOnset();
        _quietSince = null;
        _called = false;
    }

    /// <summary>
    /// Called when the wake word hits: a new segment starts here. Identical to
    /// <see cref="OnGateReleased"/> except that it marks the segment as speech-bearing — the wake word
    /// <b>is</b> something the user said. Without it a session where the user says nothing after the word
    /// never satisfies G2 (<c>_dirty</c> stays false), so the gate stays open, the wake probe stops running,
    /// and the tone sounds exactly once.
    /// <para>
    /// It also arms the wake grace period (<see cref="_awaitingSpeech"/>): until the user actually says
    /// something, the silence after the word is measured against <see cref="VoiceInputSetting.WakeTimeoutMs"/>
    /// rather than the sentence gap. Closing the session on the sentence gap is what made a normal
    /// "wake word, then think for a moment, then speak" flow drop the whole command — the session ended
    /// ~1.1 s after the tone, and everything said afterwards was only ever fed to the wake probe.
    /// </para>
    /// <para>
    /// <paramref name="heardSpeechAtHit"/> 为真时不进宽限期：命中是延迟报出来的，唤醒词和命令一口气说完时，
    /// 命令的音频落在命中点之前，命中之后再没有声音——那看上去和「喊完词在想说什么」一模一样。宽限期一旦
    /// 生效就会把整块拖到 5 s 静默，<c>SpeechFraction</c> 被稀释到阈值以下，命令照样进不了 ASR。
    /// 调用方按命中点之前那段音频里有没有人声来判断：有 = 命令已经在说了，直接按判句间隔收尾。
    /// </para>
    /// </summary>
    public void OnWakeHit(double nowSeconds, bool heardSpeechAtHit = false)
    {
        OnGateReleased(nowSeconds);
        _dirty = true;
        _awaitingSpeech = !heardSpeechAtHit;
    }

    /// <summary>The tick needs the current stream position to compute leftover; set it on every step.</summary>
    public void NoteNow(double nowSeconds) => _lastNow = nowSeconds;

    /// <summary>
    /// 宽限期内一个过阈值的 tick：并进当前这一簇（或另起一簇），攒够 <see cref="WakeOnsetMs"/> 即算开口。
    /// <para>
    /// 开口那一刻把块起点前移到这簇声音开头（留 <see cref="OnsetPreRollSeconds"/>）：块里在那之前只有
    /// 唤醒词尾巴、提示音、用户想说什么的那段沉默——没有一个字值得转写，留着只会稀释
    /// <c>SpeechFraction</c>，让命令被当噪声丢掉；停得够久时还会把块撑过 <c>WindowSeconds</c>，
    /// 命令说到一半就被 WindowCap 切开。
    /// </para>
    /// </summary>
    private void NoteOnsetCandidate(double nowSeconds, double tickSeconds)
    {
        if (_onsetLastLoud is null || nowSeconds - _onsetLastLoud.Value > OnsetBridgeSeconds)
        {
            // 新的一簇：这一 tick 的能量窗整段都可能是人声，所以起点算到窗头、时长先记一个窗。
            _onsetClusterStart = nowSeconds - (EnergyWindowMs / 1000);
            _onsetVoicedSeconds = EnergyWindowMs / 1000;
        }
        else
        {
            _onsetVoicedSeconds += tickSeconds;
        }

        _onsetLastLoud = nowSeconds;

        if (_onsetVoicedSeconds * 1000 >= WakeOnsetMs)
        {
            _awaitingSpeech = false;
            Cursor = Math.Max(Cursor, _onsetClusterStart!.Value - OnsetPreRollSeconds);
            ResetOnset();
        }
    }

    private void ResetOnset()
    {
        _onsetClusterStart = null;
        _onsetVoicedSeconds = 0;
        _onsetLastLoud = null;
    }

    private double _lastNow;

    /// <summary>上一次 <see cref="Step"/> 的流时刻：累计有声时长按真实 tick 间隔算，不按 tick 个数。</summary>
    private double? _prevStep;
}

public enum SubmissionOutcome
{
    /// <summary>The worker accepted the block; the cursor may advance.</summary>
    Queued,

    /// <summary>Not worth a request (too short, no speech, evicted); the cursor may advance.</summary>
    Skipped,

    /// <summary>The backlog is full: the audio is still owed and must be retried.</summary>
    Busy,
}
