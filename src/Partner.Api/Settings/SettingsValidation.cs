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

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var engine in web.Engines ?? [])
        {
            if (engine is null) { errors.Add("web.engines 不允许空项"); continue; }
            if (string.IsNullOrWhiteSpace(engine.Id) || !ids.Add(engine.Id)) errors.Add("web.engines 的 id 不能为空或重复");
            if (string.IsNullOrWhiteSpace(engine.Name)) errors.Add("web.engines 的名称不能为空");
            if (!string.IsNullOrWhiteSpace(engine.PresetId))
            {
                if (Partner.Core.Agent.Web.WebEngines.Find(engine.PresetId) is null || engine.Id != engine.PresetId)
                    errors.Add("web.engines 的预设无效");
                continue;
            }
            var template = engine.UrlTemplate ?? "";
            var marker = template.IndexOf("{query}", StringComparison.Ordinal);
            if (marker < 0 || marker != template.LastIndexOf("{query}", StringComparison.Ordinal)
                || !Uri.TryCreate(template.Replace("{query}", "test", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                || Partner.Core.Agent.Web.WebAddressGuard.Reject(uri) is not null
                || !template.Split('?', 2).Skip(1).Any(query => query.Split('#')[0].Split('&')
                    .Any(part => part.Contains('=') && part[(part.IndexOf('=') + 1)..].Contains("{query}", StringComparison.Ordinal)))
                || template.Replace("{query}", "", StringComparison.Ordinal).IndexOfAny(['{', '}']) >= 0)
                errors.Add("web.engines 的 URL 模板必须在查询参数值中包含一个 {query}，且为有效的 http(s) 地址");
            static bool ValidPath(string? path, bool required) => string.IsNullOrEmpty(path) ? !required
                : path.Split('.').All(segment => segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
            if (!ValidPath(engine.ResultsPath, false) || !ValidPath(engine.TitleField, true)
                || !ValidPath(engine.UrlField, true) || !ValidPath(engine.SnippetField, false))
                errors.Add("web.engines 的结果字段映射无效");
        }

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
        ValidatePetProfiles(pet.Profiles, errors);
    }

    // These durations match the built-in procedural tracks in ui/src/pet/actions.ts.
    private static readonly IReadOnlyDictionary<string, double> PetActionDurations = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["blink"] = 0.14, ["stretch"] = 2.6, ["greet"] = 2, ["wave"] = 2,
        ["headTurn"] = 1.2, ["headShake"] = 1.4, ["bodyTurn"] = 1.4, ["bodyShake"] = 1.2,
        ["nod"] = 1.2, ["shake"] = 1.2, ["tilt"] = 1.2, ["pat"] = 1.4, ["shrink"] = 1.4,
        ["surprise"] = 0.9, ["puff"] = 1.6, ["sob"] = 2.4, ["sleep"] = 2, ["wake"] = 0.6,
    };

    private static void ValidatePetProfiles(IReadOnlyDictionary<string, PetModelProfile>? profiles, List<string> errors)
    {
        if (profiles is null)
        {
            errors.Add("pet.profiles 不能为空");
            return;
        }

        foreach (var (modelId, profile) in profiles)
        {
            var path = $"pet.profiles.{modelId}";
            if (profile is null)
            {
                errors.Add($"{path} 不能为空");
                continue;
            }

            if (!double.IsFinite(profile.Amplitude) || profile.Amplitude is < -4 or > 4)
                errors.Add($"{path}.amplitude 必须是 -4–4 的有限数值");

            if (profile.Parameters is null || profile.Parameters.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
                errors.Add($"{path}.parameters 的通道和参数 ID 必须非空");

            if (profile.Procedural is null)
            {
                errors.Add($"{path}.procedural 不能为空");
                continue;
            }

            foreach (var (action, settings) in profile.Procedural)
            {
                var actionPath = $"{path}.procedural.{action}";
                if (!PetActionDurations.TryGetValue(action, out var duration) || settings is null)
                {
                    errors.Add($"{actionPath} 不是有效的程序化动作");
                    continue;
                }

                if (settings.Amplitude is { } amplitude && (!double.IsFinite(amplitude) || amplitude is < -4 or > 4))
                    errors.Add($"{actionPath}.amplitude 必须是 -4–4 的有限数值");

                if (settings.ExtraTracks is null)
                {
                    errors.Add($"{actionPath}.extraTracks 不能为空");
                    continue;
                }

                foreach (var track in settings.ExtraTracks)
                {
                    if (track is null || string.IsNullOrWhiteSpace(track.Id) || track.Keys is null || track.Keys.Count < 2)
                    {
                        errors.Add($"{actionPath}.extraTracks 需要参数 ID 和至少两个关键帧");
                        continue;
                    }

                    var keys = track.Keys;
                    var valid = keys.All(key => key is not null && double.IsFinite(key.At) && double.IsFinite(key.Value));
                    if (valid)
                    {
                        valid = keys[0].At == 0 && keys[0].Value == 0
                            && keys[^1].At == duration && keys[^1].Value == 0;
                        for (var i = 1; valid && i < keys.Count; i++) valid = keys[i].At > keys[i - 1].At;
                    }

                    if (!valid) errors.Add($"{actionPath}.extraTracks 的关键帧必须时间递增并在动作首尾归零");
                }
            }
        }
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
