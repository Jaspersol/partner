using System.Text.RegularExpressions;

namespace Partner.Api.Settings;

/// <summary>
/// The rules the agents section has to satisfy before it is stored. Called from <see cref="SettingsValidation"/>,
/// so a save that breaks one is rejected with every error at once.
/// <para>
/// The same rules back the "problems" the prompts page shows, so the page and the server cannot disagree about
/// what is wrong.
/// </para>
/// </summary>
public static partial class AgentsValidation
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    public static IReadOnlyList<string> Validate(RuntimeSettings settings, RuntimeSettings? stored = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        var agents = settings.Agents;
        var limit = PromptSize.Limit(settings.Model);

        if (PromptSize.Estimate(agents.Main.Prompt) > limit)
        {
            errors.Add($"主 agent 的提示词过长（约 {PromptSize.Estimate(agents.Main.Prompt)} token，上限 {limit}）");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, SubAgentSettings>(StringComparer.Ordinal);

        foreach (var agent in agents.SubAgents)
        {
            var label = (string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name) ?? string.Empty;

            if (!IdPattern().IsMatch(agent.Id ?? string.Empty))
            {
                errors.Add($"子 agent 的 id '{agent.Id}' 不合法（小写字母、数字、连字符，最长 32 位）");
            }
            else if (!ids.Add(agent.Id!))
            {
                errors.Add($"子 agent 的 id '{agent.Id}' 重复");
            }

            if (string.IsNullOrWhiteSpace(agent.Name))
            {
                errors.Add($"子 agent '{agent.Id}' 需要名称");
            }

            // V1: one scenario, from the closed list, held by one agent.
            if (AgentScenarios.Find(agent.Scenario) is null)
            {
                errors.Add($"「{label}」的场景 '{agent.Scenario}' 不存在；可选：{string.Join("、", AgentScenarios.All.Select(s => s.Key))}");
            }
            else if (owners.TryGetValue(agent.Scenario, out var first))
            {
                errors.Add($"场景 '{agent.Scenario}' 已被「{first.Name}」占用，同一场景只能有一个子 agent");
            }
            else
            {
                owners[agent.Scenario] = agent;
            }

            if (agent.MaxRounds is < 1 or > AgentsSettings.MaxRoundsCeiling)
            {
                errors.Add($"「{label}」的最大轮数必须在 1–{AgentsSettings.MaxRoundsCeiling} 之间");
            }

            if (agent.MaxSteps is < 1 or > 100)
            {
                errors.Add($"「{label}」的每轮最大步数必须在 1–100 之间");
            }

            if (PromptSize.Estimate(agent.Prompt) > limit)
            {
                errors.Add($"「{label}」的提示词过长（约 {PromptSize.Estimate(agent.Prompt)} token，上限 {limit}）");
            }

            if (agent.Model is { } model)
            {
                ValidateModel(label, model, errors);
            }
        }

        // V2: the built-in agent is the one that owns desktop control, and it cannot be taken away or re-pointed.
        var builtin = agents.SubAgents.FirstOrDefault(agent => agent.Id == AgentsSettings.DesktopId);
        if (builtin is null)
        {
            errors.Add("内置的桌面操控子 agent 不能删除");
        }
        else if (builtin.Scenario != AgentScenarios.Desktop)
        {
            errors.Add("内置的桌面操控子 agent 的场景不能修改");
        }

        // V3: desktop control without someone to do it would register no desktop tools at all, and the model would
        // quietly stop being able to operate the desktop. Refuse the configuration instead.
        if (settings.Desktop.Enabled && builtin is { Enabled: false })
        {
            errors.Add("桌面操控已开启，必须有一个启用的桌面操控子 agent");
        }

        // V4: a whitelist you can turn on with nothing in it is a switch that does nothing; say so.
        if (settings.Files.Enabled)
        {
            if (settings.Files.Roots.Count == 0)
            {
                errors.Add("读文件已开启，但没有配置允许读取的目录");
            }

            foreach (var root in settings.Files.Roots)
            {
                // A directory already on the list may have gone away since (a drive that is not plugged in). That is
                // reported as a problem, not a reason to refuse an unrelated save; a *new* entry has to exist now.
                var known = stored?.Files.Roots.Contains(root, StringComparer.OrdinalIgnoreCase) == true;
                if (!Path.IsPathRooted(root) || (!known && !Directory.Exists(root)))
                {
                    errors.Add($"允许读取的目录 '{root}' 必须是已存在的绝对路径");
                }
            }
        }

        return errors;
    }

    private static void ValidateModel(string label, SubAgentModelSettings model, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(model.BaseUrl)
            || !Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"「{label}」的模型地址必须是绝对的 http(s) 地址（想跟随主模型就不要填模型）");
        }

        if (string.IsNullOrWhiteSpace(model.Model))
        {
            errors.Add($"「{label}」的模型名不能为空");
        }

        if (model.TimeoutSeconds <= 0 || model.ContextTokens <= 0 || model.MaxTokens <= 0)
        {
            errors.Add($"「{label}」的模型超时、上下文、输出上限都必须为正");
        }
        else if (model.MaxTokens >= model.ContextTokens)
        {
            errors.Add($"「{label}」的 maxTokens 必须小于 contextTokens");
        }
    }
}
