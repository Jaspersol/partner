using Partner.Core.Agent.Llm;
using Partner.Voice.Asr;
using Partner.Voice.Tts;

namespace Partner.Api.Settings;

/// <summary>
/// Rejects configurations that would fail later, in a place where the failure is unattributable: a bad base
/// URL only surfaces as a turn that never completes, and a zero sample rate only as silence.
/// </summary>
public static class SettingsValidation
{
    /// <param name="settings">The settings about to be stored.</param>
    /// <param name="stored">
    /// What is stored now. A rule that depends on the machine (a directory that must exist) is held against what is
    /// <i>new</i>: otherwise an unplugged drive would make every unrelated save fail until the path was edited out.
    /// </param>
    public static IReadOnlyList<string> Validate(RuntimeSettings settings, RuntimeSettings? stored = null)
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

        errors.AddRange(AgentsValidation.Validate(settings, stored));

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
}
