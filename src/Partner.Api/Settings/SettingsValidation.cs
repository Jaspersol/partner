using Partner.Core.Agent.Llm;
using Partner.Voice.Asr;
using Partner.Voice.Tts;

namespace Partner.Api.Settings;

/// <summary>
/// Rejects configurations that would fail later, in a place where the failure is unattributable: a bad base
/// URL only surfaces as a turn that never completes, and a zero sample rate only as silence.
/// </summary>
public static partial class SettingsValidation
{
    /// <param name="settings">The settings about to be stored.</param>
    /// <param name="stored">
    /// What is stored now. A rule that depends on the machine (a directory that must exist) is held against what is
    /// <i>new</i>: otherwise an unplugged drive would make every unrelated save fail until the path was edited out.
    /// </param>
    /// <param name="petModelIds">
    /// The imported model ids, or <see langword="null"/> to skip the model check: without the list every non-empty
    /// id would be a false rejection.
    /// </param>
    public static IReadOnlyList<string> Validate(
        RuntimeSettings settings,
        RuntimeSettings? stored = null,
        int apiPort = 0,
        IReadOnlyCollection<string>? petModelIds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();

        if (settings.Input.SilenceThreshold < 0)
        {
            errors.Add("input.silenceThreshold 不能为负");
        }

        if (settings.Input.UtteranceGapMs < 0
            || settings.Input.SentenceGapMs < 0
            || settings.Input.WakeTimeoutMs < 0)
        {
            errors.Add("input 的间隔不能为负");
        }

        if (settings.Input.WindowSeconds <= 0)
        {
            errors.Add("input.windowSeconds 必须为正");
        }

        if (settings.Output.Speed <= 0)
        {
            errors.Add("output.speed 必须为正");
        }

        if (settings.Output.MaxSplitChars <= 0)
        {
            errors.Add("output.maxSplitChars 必须为正");
        }

        if (settings.Output.MinSentenceChars <= 0)
        {
            errors.Add("output.minSentenceChars 必须为正");
        }

        if (settings.Output.DrainMs < 0)
        {
            errors.Add("output.drainMs 不能为负");
        }

        if (settings.Output.SampleRate <= 0)
        {
            errors.Add("output.sampleRate 必须为正");
        }

        CheckEndpoint("asr", settings.Asr.BaseUrl, settings.Asr.Model, errors);
        CheckEndpoint("tts", settings.Tts.BaseUrl, null, errors);
        CheckEndpoint("model", settings.Model.BaseUrl, settings.Model.Model, errors);

        if (!LlmProtocols.IsValid(settings.Model.Protocol))
        {
            errors.Add($"model.protocol 必须是 {string.Join(" / ", LlmProtocols.Values)}");
        }

        var asrProtocols = AsrProtocols.Values;
        if (!asrProtocols.Contains(settings.Asr.Protocol, StringComparer.Ordinal))
        {
            errors.Add($"asr.protocol 必须是 {string.Join(" / ", asrProtocols)}");
        }

        var ttsProtocols = TtsProtocols.Values;
        if (!ttsProtocols.Contains(settings.Tts.Protocol, StringComparer.Ordinal))
        {
            errors.Add($"tts.protocol 必须是 {string.Join(" / ", ttsProtocols)}");
        }

        if (settings.Tts.Speed < 0)
        {
            errors.Add("tts.speed 不能为负");
        }

        if (!Partner.Desktop.Input.InputModes.IsValid(settings.Desktop.InputMode))
        {
            errors.Add("desktop.inputMode 必须是 foreground / auto / background");
        }

        ValidateEngine("local.tts", settings.Local.Tts, errors);
        ValidateEngine("local.kws", settings.Local.Kws, errors);

        if (settings.Asr.SampleRate <= 0)
        {
            errors.Add("asr.sampleRate 必须为正");
        }

        if (settings.Asr.ConnectTimeoutMs <= 0 || settings.Asr.ReadTimeoutMs <= 0)
        {
            errors.Add("asr 的超时必须为正");
        }

        if (settings.Tts.SampleRate <= 0)
        {
            errors.Add("tts.sampleRate 必须为正");
        }

        if (settings.Tts.ChunkMs <= 0)
        {
            errors.Add("tts.chunkMs 必须为正");
        }

        if (settings.Tts.TimeoutSeconds <= 0)
        {
            errors.Add("tts.timeoutSeconds 必须为正");
        }

        if (settings.Model.TimeoutSeconds <= 0)
        {
            errors.Add("model.timeoutSeconds 必须为正");
        }

        if (settings.Model.ContextTokens <= 0)
        {
            errors.Add("model.contextTokens 必须为正");
        }

        if (settings.Model.MaxTokens <= 0)
        {
            errors.Add("model.maxTokens 必须为正");
        }

        // A completion that cannot fit alongside the prompt is not a configuration, it is a truncation.
        if (settings.Model.ContextTokens > 0 && settings.Model.MaxTokens >= settings.Model.ContextTokens)
        {
            errors.Add("model.maxTokens 必须小于 model.contextTokens");
        }

        ValidateWeb(settings.Web, errors);
        ValidatePet(settings.Pet, petModelIds, errors);

        errors.AddRange(AgentsValidation.Validate(settings, stored));
        errors.AddRange(ServicesValidation.Validate(settings, apiPort));

        return errors;
    }

    /// <summary>
    /// The web section's bounds. The engine ids are deliberately <b>not</b> validated against the built-in list:
    /// an id that no longer exists is a leftover from an older build, not a broken configuration, and refusing
    /// the save would leave the user with a settings file they cannot fix from the UI.
    /// </summary>
    private static void ValidateWeb(WebSettings web, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(web);

        if (!string.IsNullOrWhiteSpace(web.Proxy))
        {
            if (!Uri.TryCreate(web.Proxy, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("web.proxy 必须是绝对的 http(s) 地址");
            }
        }

        if (web.TimeBudgetSeconds < Partner.Core.Agent.Web.WebOptions.MinBudgetSeconds
            || web.TimeBudgetSeconds > Partner.Core.Agent.Web.WebOptions.MaxBudgetSeconds)
        {
            errors.Add($"web.timeBudgetSeconds 必须在 {Partner.Core.Agent.Web.WebOptions.MinBudgetSeconds}–"
                + $"{Partner.Core.Agent.Web.WebOptions.MaxBudgetSeconds} 秒之间");
        }

        if (web.MaxResults < Partner.Core.Agent.Web.WebOptions.MinResults
            || web.MaxResults > Partner.Core.Agent.Web.WebOptions.MaxResultsCeiling)
        {
            errors.Add($"web.maxResults 必须在 {Partner.Core.Agent.Web.WebOptions.MinResults}–"
                + $"{Partner.Core.Agent.Web.WebOptions.MaxResultsCeiling} 之间");
        }
    }

    /// <summary>An endpoint is optional; once present it must be usable, and some need a companion name.</summary>
    private static void CheckEndpoint(string section, string? baseUrl, string? companion, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{section}.baseUrl 必须是绝对的 http(s) 地址");
        }

        if (companion is not null && string.IsNullOrWhiteSpace(companion))
        {
            errors.Add($"{section}.model 不能为空");
        }
    }

    /// <summary>Managing a process requires an executable command, a valid port and a positive grace period. Empty Command = not managed, so skip.</summary>
    private static void ValidateEngine(string section, LocalEngineSettings engine, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (string.IsNullOrWhiteSpace(engine.Command))
        {
            return;
        }

        if (engine.Port is < 1 or > 65535)
        {
            errors.Add($"{section}.port 必须在 1–65535 之间");
        }

        if (engine.ReadyTimeoutSeconds <= 0)
        {
            errors.Add($"{section}.readyTimeoutSeconds 必须为正");
        }
    }

    /// <summary>The pet section's bounds.</summary>
    private static void ValidatePet(PetSettings pet, IReadOnlyCollection<string>? modelIds, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(pet);

        if (pet.Scale is < 0.5 or > 2)
        {
            errors.Add("pet.scale 必须在 0.5–2");
        }

        if (pet.IdleMinSeconds < 5 || pet.IdleMinSeconds > pet.IdleMaxSeconds || pet.IdleMaxSeconds > 3600)
        {
            errors.Add("pet.idleMinSeconds 必须在 5–3600 且不大于 pet.idleMaxSeconds");
        }

        if (pet.SleepAfterMinutes < 0 || pet.SleepAfterMinutes > 1440)
        {
            errors.Add("pet.sleepAfterMinutes 必须在 0–1440");
        }

        if (pet.EmotionCooldownSeconds < 0 || pet.EmotionCooldownSeconds > 600)
        {
            errors.Add("pet.emotionCooldownSeconds 必须在 0–600");
        }

        if (pet.ExpressionHoldSeconds < 1 || pet.ExpressionHoldSeconds > 120)
        {
            errors.Add("pet.expressionHoldSeconds 必须在 1–120");
        }

        if (pet.FastDragPxPerSecond < 100 || pet.FastDragPxPerSecond > 20000)
        {
            errors.Add("pet.fastDragPxPerSecond 必须在 100–20000");
        }

        var greetingTimes = pet.GreetingTimes ?? [];
        if (greetingTimes.Count > 12)
        {
            errors.Add("pet.greetingTimes 最多 12 个");
        }

        if (greetingTimes.Any(time => !GreetingTime().IsMatch(time)))
        {
            errors.Add("pet.greetingTimes 的每一项都必须是 HH:mm");
        }

        if (modelIds is not null
            && !string.IsNullOrEmpty(pet.ActiveModelId)
            && !modelIds.Contains(pet.ActiveModelId, StringComparer.Ordinal))
        {
            errors.Add($"pet.activeModelId 不是已导入的模型：{pet.ActiveModelId}");
        }

        ValidateEmotions(pet.Emotions ?? new PetEmotionDictionary(), errors);
    }

    /// <summary>
    /// The dictionary's own limits. <c>NegationPrefixes</c> may be empty (negation off) but never contain a blank.
    /// </summary>
    private static void ValidateEmotions(PetEmotionDictionary emotions, List<string> errors)
    {
        if (emotions.MinWeight is < 1 or > 10)
        {
            errors.Add("pet.emotions.minWeight 必须在 1–10");
        }

        var prefixes = emotions.NegationPrefixes ?? [];
        if (prefixes.Count > 32)
        {
            errors.Add("pet.emotions.negationPrefixes 最多 32 个");
        }

        if (prefixes.Any(prefix => string.IsNullOrWhiteSpace(prefix) || prefix.Length > 8))
        {
            errors.Add("pet.emotions.negationPrefixes 的每一项都必须非空且不超过 8 个字");
        }

        foreach (var (emotion, keywords) in emotions.Keywords ?? new Dictionary<string, IReadOnlyList<PetKeyword>>())
        {
            if (keywords is null || keywords.Count > 200)
            {
                errors.Add($"pet.emotions.{emotion} 最多 200 个关键词");
                continue;
            }

            foreach (var keyword in keywords)
            {
                var text = keyword.Text?.Trim() ?? string.Empty;
                if (text.Length == 0 || text.Length > 32)
                {
                    errors.Add($"pet.emotions.{emotion} 的关键词必须非空且不超过 32 个字");
                }

                if (keyword.Weight is < 1 or > 10)
                {
                    errors.Add($"pet.emotions.{emotion}.{text} 的 weight 必须在 1–10");
                }
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial System.Text.RegularExpressions.Regex GreetingTime();
}
