using Partner.Voice.Asr;

namespace Partner.Voice.Settings;

/// <summary>Voice input settings (design doc §2.8.12 — control is settings, not buttons).</summary>
public sealed record VoiceInputSetting
{
    public bool Enabled { get; init; }

    /// <summary>
    /// 唤醒门（本地 KWS）：非 null 时麦克风持续监听但<b>不提交 ASR</b>，
    /// 直到 <see cref="IWakeChecker"/> 报出命中。null = 不设门（纯语音输入，旧行为）。
    /// </summary>
    public IWakeChecker? Wake { get; init; }

    /// <summary>
    /// 唤醒命中瞬间的提示音（叮），在输出设备上自发播放。null/关 = 静默进会话。
    /// 无回调依赖：宿主把 sink 交给 <see cref="VoiceInputHalf"/>，命中那 tick 直接 Enqueue。
    /// </summary>
    public bool WakeTone { get; init; } = true;

    /// <summary>Input device name (partial, case/space-insensitive match) or null for the system default.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Fixed RMS gate. Must sit above the room noise floor or silence is never detected (R11).</summary>
    public double SilenceThreshold { get; init; } = 0.03;

    /// <summary>
    /// Speaking over the assistant stops it. Off by default: without echo cancellation a speaker in the room is
    /// heard by the microphone, and the assistant would interrupt itself. Meant for headphones.
    /// </summary>
    public bool BargeIn { get; init; }

    /// <summary>How long sound well above the silence threshold must last, while the assistant speaks, to count as interrupting.</summary>
    public double BargeInMs { get; init; } = 600;

    /// <summary>G2: absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>
    /// 唤醒词命中后、用户还没开口时，会话最多再等多久的静默。
    /// <para>
    /// 与 <see cref="UtteranceGapMs"/> 分开：提示音到人开口之间的那段沉默是「在想说什么」，
    /// 不是「说完了」。用判句间隔去关会话，人还没出声闸门就合上了——之后说的全部只喂给唤醒探针，
    /// 永远不进 ASR（实测：唤醒后停 1.2 s 再说话，utterance 为 0）。
    /// 一旦真听到语音，立刻回到 <see cref="UtteranceGapMs"/> 的正常判句。
    /// </para>
    /// </summary>
    public double WakeTimeoutMs { get; init; } = 5000;

    /// <summary>G1: silence that ends a sentence (streaming mode only).</summary>
    public double SentenceGapMs { get; init; } = 450;

    /// <summary>Longest block sent in one request; also sizes the ring (capacity = this + 2s).</summary>
    public double WindowSeconds { get; init; } = 8;

    /// <summary>Streaming ASR (one sentence per request) versus batch (one request per utterance).</summary>
    public bool AsrStream { get; init; }

    /// <summary>Forced split length for text with no punctuation.</summary>
    public int MaxSplitChars { get; init; } = 40;

    public static VoiceInputSetting Default { get; } = new();
}

/// <summary>Voice output settings (design doc §2.8.11 — the output half exists only when enabled).</summary>
public sealed record VoiceOutputSetting
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    public string? Voice { get; init; }

    public double Speed { get; init; } = 1.0;

    /// <summary>Silence inserted between segments; forced to 0 when a cloning reference is used.</summary>
    public double GapMs { get; init; }

    /// <summary>Device-buffer drain hold-off after the last write.</summary>
    public double DrainMs { get; init; } = 400;

    /// <summary>Playback sample rate; must match what the TTS service emits.</summary>
    public int SampleRate { get; init; } = 24_000;

    /// <summary>While playing, input is discarded so the assistant never transcribes itself.</summary>
    public bool MuteDuringTts { get; init; } = true;

    public static VoiceOutputSetting Default { get; } = new();
}
