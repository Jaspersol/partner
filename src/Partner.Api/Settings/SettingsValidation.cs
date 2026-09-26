namespace Partner.Api.Settings;

/// <summary>
/// Rejects configurations that would fail later, in a place where the failure is unattributable: a bad base
/// URL only surfaces as a turn that never completes, and a zero sample rate only as silence.
/// </summary>
public static class SettingsValidation
{
    public static IReadOnlyList<string> Validate(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();

        if (settings.Input.SilenceThreshold < 0)
        {
            errors.Add("input.silenceThreshold 不能为负");
        }

        if (settings.Input.UtteranceGapMs < 0 || settings.Input.SentenceGapMs < 0)
        {
            errors.Add("input 的间隔不能为负");
        }

        if (settings.Input.WindowSeconds <= 0)
        {
            errors.Add("input.windowSeconds 必须为正");
        }

        if (settings.Input.MaxSplitChars <= 0)
        {
            errors.Add("input.maxSplitChars 必须为正");
        }

        if (settings.Output.Speed <= 0)
        {
            errors.Add("output.speed 必须为正");
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

        return errors;
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
}
