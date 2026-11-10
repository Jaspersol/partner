using System.Text.RegularExpressions;
using Partner.Core.Agent.Llm;

namespace Partner.Api.Settings;

/// <summary>
/// Rules a services section must satisfy before it is stored. Called from <see cref="SettingsValidation"/>, so a
/// save that breaks one is rejected with every error at once.
/// </summary>
public static partial class ServicesValidation
{
    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex IdPattern();

    /// <param name="apiPort">
    /// The main API's listen port, threaded from <c>ApiHost</c>. Zero when unknown (a store opened without a
    /// host) — the "must not equal the main port" rule is skipped then.
    /// </param>
    public static IReadOnlyList<string> Validate(RuntimeSettings settings, int apiPort)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        var instances = settings.Services.Instances ?? Array.Empty<ServiceSettings>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ports = new HashSet<int>();
        var localPorts = LocalPorts(settings);

        foreach (var service in instances)
        {
            var id = service.Id ?? string.Empty;
            var label = $"services[{id}]";

            if (!IdPattern().IsMatch(id))
            {
                errors.Add($"{label}.id 只能是 [a-z0-9-]{{1,32}}");
            }

            if (!ids.Add(id))
            {
                errors.Add($"服务 id 重复：{service.Id}");
            }

            if (service.Port < 1024 || service.Port > 65535)
            {
                errors.Add($"{label}.port 必须在 1024–65535 之间");
            }
            else if (!ports.Add(service.Port))
            {
                errors.Add($"服务端口重复：{service.Port}");
            }

            if (apiPort > 0 && service.Port == apiPort)
            {
                errors.Add($"{label}.port 不能等于主 API 端口 {apiPort}");
            }

            if (localPorts.Contains(service.Port))
            {
                errors.Add($"{label}.port 不能等于本地引擎端口 {service.Port}");
            }

            if (!LlmProtocols.IsValid(service.Protocol))
            {
                errors.Add($"{label}.protocol 必须是 {string.Join(" / ", LlmProtocols.Values)}");
            }

            if (string.IsNullOrWhiteSpace(service.Model))
            {
                errors.Add($"{label}.model 不能为空");
            }

            // An empty token makes FixedTimeEquals("" , "") true, so a bare "Bearer " would pass the gate. 32 is
            // below the 43 characters TokenAuth.Generate() produces, leaving room for a shorter one set by hand.
            if (string.IsNullOrEmpty(service.Token) || service.Token.Length < 32)
            {
                errors.Add($"{label}.token 不能为空，长度不少于 32 个字符");
            }

            if (service.ContextTokens <= 0)
            {
                errors.Add($"{label}.contextTokens 必须为正");
            }

            if (service.Summary.IntervalMinutes < 1 || service.Summary.IntervalMinutes > 1440)
            {
                errors.Add($"{label}.summary.intervalMinutes 必须在 1–1440 之间");
            }

            if (service.Summary.MaxWaitMinutes < service.Summary.IntervalMinutes)
            {
                errors.Add($"{label}.summary.maxWaitMinutes 不能小于 intervalMinutes");
            }

            if (!service.Upstream.Inherit)
            {
                if (string.IsNullOrWhiteSpace(service.Upstream.BaseUrl))
                {
                    errors.Add($"{label}.upstream.baseUrl 在不继承主模型时必填");
                }

                if (string.IsNullOrWhiteSpace(service.Upstream.Model))
                {
                    errors.Add($"{label}.upstream.model 在不继承主模型时必填");
                }

                if (service.Upstream.MaxTokens is <= 0)
                {
                    errors.Add($"{label}.upstream.maxTokens 必须为正");
                }

                if (service.Upstream.TimeoutSeconds is <= 0)
                {
                    errors.Add($"{label}.upstream.timeoutSeconds 必须为正");
                }
            }

            // Pass-through never converts between wire formats: an OpenAI-shaped body sent to /messages (or the
            // reverse) is a guaranteed 400 from the upstream, so the mismatch is refused at save time.
            var serviceProtocol = LlmProtocols.Normalize(service.Protocol);
            var upstreamProtocol = EffectiveUpstreamProtocol(service, settings);
            if (!string.IsNullOrEmpty(upstreamProtocol)
                && LlmProtocols.IsValid(service.Protocol)
                && LlmProtocols.IsValid(upstreamProtocol)
                && !string.Equals(serviceProtocol, upstreamProtocol, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{label}.protocol 与上游协议 {upstreamProtocol} 不一致；透传模式不做协议转换");
            }
        }

        // Self-loop: neither the main model nor a service's upstream may point at a loopback service port. A main
        // model aimed at its own services, or a service aimed at another, would recurse through this process.
        CheckLoopback("model.baseUrl", settings.Model.BaseUrl, ports, errors);
        foreach (var service in instances)
        {
            CheckLoopback($"services[{service.Id}].upstream.baseUrl", service.Upstream.BaseUrl, ports, errors);
        }

        return errors;
    }

    /// <summary>
    /// The protocol the instance will actually speak upstream. Inheriting means the main model's protocol (via
    /// the same resolution the consumer uses, so the environment fallback counts). Empty base URL means nothing
    /// resolves yet — the mismatch is not flagged then, so services can be configured before a model exists.
    /// </summary>
    private static string EffectiveUpstreamProtocol(ServiceSettings service, RuntimeSettings settings)
    {
        if (!service.Upstream.Inherit)
        {
            if (string.IsNullOrWhiteSpace(service.Upstream.BaseUrl))
            {
                return string.Empty;
            }

            // Without a protocol override, a custom upstream speaks the service's own format.
            return LlmProtocols.Normalize(service.Protocol);
        }

        var (config, _) = ModelConfig.Resolve(settings);
        if (config is not null)
        {
            return LlmProtocols.Normalize(config.Protocol);
        }

        return string.IsNullOrWhiteSpace(settings.Model.BaseUrl)
            ? string.Empty
            : LlmProtocols.Normalize(settings.Model.Protocol);
    }

    private static HashSet<int> LocalPorts(RuntimeSettings settings)
    {
        var ports = new HashSet<int>();
        if (settings.Local?.Tts is { Port: > 0 } tts)
        {
            ports.Add(tts.Port);
        }

        if (settings.Local?.Kws is { Port: > 0 } kws)
        {
            ports.Add(kws.Port);
        }

        return ports;
    }

    private static void CheckLoopback(string label, string? baseUrl, IReadOnlySet<int> servicePorts, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !uri.IsLoopback)
        {
            return;
        }

        if (servicePorts.Contains(uri.Port))
        {
            errors.Add($"{label} 不能指向本机服务端口 {uri.Port}（自环）");
        }
    }
}
