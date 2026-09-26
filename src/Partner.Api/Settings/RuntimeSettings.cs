namespace Partner.Api.Settings;

/// <summary>
/// The runtime's editable configuration (design doc §2.5.5, §2.8.11–§2.8.12, §2.9.5).
/// <para>
/// Field names mirror the option records that actually consume them — <c>Partner.Voice.Asr.AsrOptions</c>,
/// <c>Partner.Voice.Tts.TtsOptions</c>, <c>Partner.Core.Agent.Llm.HttpLlmOptions</c> — so the correspondence
/// is checkable by reading the two side by side rather than by remembering a mapping.
/// </para>
/// </summary>
public sealed record RuntimeSettings
{
    public InputSettings Input { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AsrSettings Asr { get; init; } = new();

    public TtsSettings Tts { get; init; } = new();

    public ModelSettings Model { get; init; } = new();

    public static RuntimeSettings Default { get; } = new();
}

/// <summary>Mirrors <c>VoiceInputSetting</c>.</summary>
public sealed record InputSettings
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Fixed RMS gate; must sit above the room noise floor or silence is never detected.</summary>
    public double SilenceThreshold { get; init; } = 0.03;

    /// <summary>Absolute silence that ends the utterance.</summary>
    public double UtteranceGapMs { get; init; } = 1200;

    /// <summary>Silence that ends a sentence (streaming mode only).</summary>
    public double SentenceGapMs { get; init; } = 450;

    public double WindowSeconds { get; init; } = 8;

    /// <summary>Streaming ASR (one sentence per request) versus batch (one request per utterance).</summary>
    public bool AsrStream { get; init; }

    /// <summary>Forced split length for text with no punctuation.</summary>
    public int MaxSplitChars { get; init; } = 40;
}

/// <summary>Mirrors <c>VoiceOutputSetting</c>.</summary>
public sealed record OutputSettings
{
    public bool Enabled { get; init; }

    public string? DeviceId { get; init; }

    public string? Voice { get; init; }

    public double Speed { get; init; } = 1.0;

    public double GapMs { get; init; }

    public double DrainMs { get; init; } = 400;

    public int SampleRate { get; init; } = 24_000;

    public bool MuteDuringTts { get; init; } = true;
}

/// <summary>Mirrors <c>AsrOptions</c>. An absent <see cref="BaseUrl"/> means speech input is not configured.</summary>
public sealed record AsrSettings
{
    public string? BaseUrl { get; init; }

    public string Model { get; init; } = "qwen3-asr";

    /// <summary>Prefilled as the assistant turn so the model skips emitting metadata.</summary>
    public string? Language { get; init; } = "Chinese";

    public double ConnectTimeoutMs { get; init; } = 2000;

    public double ReadTimeoutMs { get; init; } = 3000;

    public int SampleRate { get; init; } = 16_000;
}

/// <summary>Mirrors <c>TtsOptions</c>. An absent <see cref="BaseUrl"/> means speech output is not configured.</summary>
public sealed record TtsSettings
{
    public string? BaseUrl { get; init; }

    public string Lang { get; init; } = "zh";

    public int SampleRate { get; init; } = 24_000;

    /// <summary>Reference audio for voice cloning; read once when the client is built.</summary>
    public string? SpeakerRefPath { get; init; }

    public double TimeoutSeconds { get; init; } = 600;

    public int ChunkMs { get; init; } = 100;
}

/// <summary>Mirrors <c>HttpLlmOptions</c>. The key is write-only; see <see cref="ModelSettingsView"/>.</summary>
public sealed record ModelSettings
{
    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// Never serialised into a response: the settings endpoint is reachable by anything holding the bearer
    /// token, and a key echoed back is a key leaked. On write, absent means "keep", empty means "clear".
    /// </summary>
    public string? ApiKey { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    /// <summary>How many content events feed a turn (the window the assembler trims to).</summary>
    public int ContextItems { get; init; } = 20;

    /// <summary>Output ceiling for one completion.</summary>
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>What the API returns: the same shape, minus the secret, plus whether one is stored.</summary>
public sealed record ModelSettingsView
{
    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    public bool ApiKeySet { get; init; }

    public double TimeoutSeconds { get; init; } = 120;

    public int ContextItems { get; init; } = 20;

    public int MaxTokens { get; init; } = 4096;
}

public sealed record SettingsView
{
    public InputSettings Input { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AsrSettings Asr { get; init; } = new();

    public TtsSettings Tts { get; init; } = new();

    public ModelSettingsView Model { get; init; } = new();

    public static SettingsView From(RuntimeSettings settings) => new()
    {
        Input = settings.Input,
        Output = settings.Output,
        Asr = settings.Asr,
        Tts = settings.Tts,
        Model = new ModelSettingsView
        {
            BaseUrl = settings.Model.BaseUrl,
            Model = settings.Model.Model,
            ApiKeySet = !string.IsNullOrEmpty(settings.Model.ApiKey),
            TimeoutSeconds = settings.Model.TimeoutSeconds,
            ContextItems = settings.Model.ContextItems,
            MaxTokens = settings.Model.MaxTokens,
        },
    };
}
