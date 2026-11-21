using System.Text.Json;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Core.Agent.Delegation;

public sealed partial class JobEngine
{
    /// <summary>What the log says one round did, read back after the round rather than trusted from the model.</summary>
    /// <param name="Actions">The calls made, most frequent first, as <c>name ×n</c> with the action kinds when the tool has them.</param>
    /// <param name="Calls">The same, as counts by tool name, for the job-wide total.</param>
    /// <param name="LastFailure">The last tool failure text, or null if no tool failed.</param>
    /// <param name="Terminal">A <see cref="JobCategories"/> value when a tool failure means no round can succeed.</param>
    /// <param name="StepLimit">Whether the round ended because its step budget was spent.</param>
    private sealed record RoundFacts(
        IReadOnlyList<string> Actions,
        IReadOnlyDictionary<string, int> Calls,
        IReadOnlyDictionary<string, Dictionary<string, int>> Kinds,
        string? LastFailure,
        string? Terminal,
        bool StepLimit,
        IReadOnlyList<string> FileResults);

    private async Task<RoundFacts> ReadRoundAsync(long before, CancellationToken ct)
    {
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        string? lastFailure = null;
        string? terminal = null;
        var stepLimit = false;
        var fileCalls = new List<(string? Id, string Name, string? Target)>();
        var openFileCalls = new Dictionary<string, (string Name, string? Target)>(StringComparer.Ordinal);
        var matchedResults = new Dictionary<string, (string Status, string? Target)>(StringComparer.Ordinal);
        var fileResults = new List<string>();
        var events = new List<SessionEvent>();
        var callCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var resultCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        await foreach (var logged in session.ReadAsync(before + 1, 100_000, ct).ConfigureAwait(false))
        {
            events.Add(logged);
            if (logged.Meta?.GetValueOrDefault("id") is not { } id || string.IsNullOrWhiteSpace(id)) continue;
            var counts = logged.Kind switch
            {
                SessionEventKind.ToolCall => callCounts,
                SessionEventKind.ToolResult => resultCounts,
                _ => null,
            };
            if (counts is not null) counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        foreach (var logged in events)
        {
            switch (logged.Kind)
            {
                case SessionEventKind.ToolCall when logged.Meta is not null && logged.Meta.TryGetValue("name", out var name):
                    calls[name] = calls.GetValueOrDefault(name) + 1;
                    if (name is "file_list" or "file_read" or "file_write" or "file_edit")
                    {
                        var callId = logged.Meta.GetValueOrDefault("id");
                        var callInfo = (Name: name, Target: name is "file_write" or "file_edit" ? null : FileTarget(logged.Meta.GetValueOrDefault("args")));
                        fileCalls.Add((callId, callInfo.Name, callInfo.Target));
                        if (!string.IsNullOrWhiteSpace(callId) && callCounts.GetValueOrDefault(callId) == 1)
                            openFileCalls[callId] = callInfo;
                    }
                    if (KindOf(logged.Meta.GetValueOrDefault("args")) is { } kind)
                    {
                        var perKind = kinds.TryGetValue(name, out var existing) ? existing : kinds[name] = new(StringComparer.Ordinal);
                        perKind[kind] = perKind.GetValueOrDefault(kind) + 1;
                    }

                    break;

                case SessionEventKind.ToolResult when logged.Meta is not null
                    && logged.Meta.TryGetValue("id", out var resultId)
                    && !string.IsNullOrWhiteSpace(resultId)
                    && callCounts.GetValueOrDefault(resultId) == 1
                    && resultCounts.GetValueOrDefault(resultId) == 1
                    && openFileCalls.TryGetValue(resultId, out var fileCall)
                    && logged.Meta.GetValueOrDefault("name") == fileCall.Name:
                    openFileCalls.Remove(resultId);
                    if (fileCall.Name is "file_write" or "file_edit")
                    {
                        var receipt = WriteReceipt(logged.Body, logged.Meta.GetValueOrDefault("ok"));
                        matchedResults[resultId] = (receipt.Status, receipt.Target);
                        if (receipt.Status == "拒绝")
                        {
                            lastFailure = logged.Body;
                            terminal = Stronger(terminal, receipt.Approval == "denied" ? JobCategories.ApprovalDenied : JobCategories.GovernanceDenied);
                        }
                        else if (receipt.Status == "状态未确认，重试前请核实")
                        {
                            lastFailure = receipt.Status;
                            terminal = Stronger(terminal, JobCategories.Unconfirmed);
                        }
                        else if (receipt.Status != "成功") lastFailure = logged.Body;
                    }
                    else
                    {
                        var status = logged.Meta.GetValueOrDefault("ok") switch
                        {
                            "true" => "成功",
                            "false" => IsUnconfirmed(logged.Body) ? "状态未确认，重试前请核实" : "失败",
                            _ => "状态未确认，重试前请核实",
                        };
                        matchedResults[resultId] = (status, fileCall.Target);
                        if (logged.Meta.GetValueOrDefault("ok") == "false" && !IsUnconfirmed(logged.Body))
                        {
                            lastFailure = logged.Body;
                            terminal = Stronger(terminal, TerminalOf(logged.Body));
                        }
                    }
                    break;

                case SessionEventKind.ToolResult when logged.Meta is not null
                    && logged.Meta.TryGetValue("ok", out var ok) && ok == "false"
                    && logged.Meta.GetValueOrDefault("name") is not ("file_write" or "file_edit"):
                    lastFailure = logged.Body;
                    terminal = Stronger(terminal, TerminalOf(logged.Body));
                    break;

                case SessionEventKind.TurnEnd when logged.Meta is not null
                    && logged.Meta.TryGetValue("detail", out var detail) && detail == "step-limit":
                    stepLimit = true;
                    break;
            }
        }

        foreach (var (callId, name, target) in fileCalls)
        {
            if (callId is not null && matchedResults.TryGetValue(callId, out var receipt))
                fileResults.Add($"{name}，调用 ID {JsonSerializer.Serialize(callId)}，请求目标 {JsonSerializer.Serialize(receipt.Target)}，工具结果：{receipt.Status}");
            else
            {
                fileResults.Add($"{name}，调用 ID {JsonSerializer.Serialize(callId)}，请求目标 {JsonSerializer.Serialize(target)}，未关联到工具结果，状态未确认，重试前请核实");
                if (name is "file_write" or "file_edit")
                {
                    lastFailure = "文件写入状态未确认，重试前请核实";
                    terminal = Stronger(terminal, JobCategories.Unconfirmed);
                }
            }
        }

        return new RoundFacts(
            Describe(calls, kinds), calls, kinds, lastFailure is null ? null : Truncate(lastFailure), terminal, stepLimit, fileResults);
    }

    private static (string Status, string? Target, string? Approval) WriteReceipt(string body, string? ok)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("状态未确认，重试前请核实", null, null);
            string? Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var path = Field("path");
            var approval = Field("approval");
            var status = Field("status") switch
            {
                "success" when ok == "true" && (approval is "approved" or "not_requested") && !string.IsNullOrWhiteSpace(path) => "成功",
                "failed" when ok == "false" => "失败",
                "denied" when ok == "false" => "拒绝",
                "not_started" when ok == "false" => "未开始",
                _ => "状态未确认，重试前请核实",
            };
            return (status, path, approval);
        }
        catch (JsonException) { return ("状态未确认，重试前请核实", null, null); }
    }

    private static bool IsUnconfirmed(string body) =>
        (body.StartsWith("timeout after ", StringComparison.Ordinal)
            && body.Contains("; status unconfirmed", StringComparison.Ordinal))
        || body.StartsWith("canceled; status unconfirmed", StringComparison.Ordinal)
        || body.StartsWith("canceled before completion; status unconfirmed", StringComparison.Ordinal);

    private static string? FileTarget(string? argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? "{}");
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Tool calls as <c>name ×n</c>, with the action kinds in brackets when the tool carries them.</summary>
    private static IReadOnlyList<string> Describe(
        IReadOnlyDictionary<string, int> calls,
        IReadOnlyDictionary<string, Dictionary<string, int>> kinds) =>
        calls.OrderByDescending(pair => pair.Value).Select(pair =>
            kinds.TryGetValue(pair.Key, out var perKind)
                ? $"{pair.Key} ×{pair.Value}（{string.Join("、", perKind.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}）"
                : $"{pair.Key} ×{pair.Value}").ToList();

    /// <summary>
    /// Whether a tool failure is a refusal rather than a miss. The prefixes are the ones the runner and the
    /// file tools write; <paramref name="extraTerminal"/> adds markers only the host can recognise.
    /// </summary>
    private string? TerminalOf(string body)
    {
        var text = body.TrimStart();
        if (text.StartsWith("approval denied:", StringComparison.Ordinal))
        {
            return JobCategories.ApprovalDenied;
        }

        if (text.StartsWith("denied:", StringComparison.Ordinal)
            || text.StartsWith("outside_roots:", StringComparison.Ordinal)
            || text.StartsWith("denied_file:", StringComparison.Ordinal))
        {
            return JobCategories.GovernanceDenied;
        }

        return extraTerminal?.Invoke(body);
    }

    /// <summary>An unanswered approval outranks a rule refusal: it names the person, which is who the main agent should tell.</summary>
    private static string? Stronger(string? current, string? candidate) =>
        current == JobCategories.ApprovalDenied || candidate == JobCategories.ApprovalDenied ? JobCategories.ApprovalDenied
            : current == JobCategories.GovernanceDenied ? current : candidate ?? current;

    private static string? KindOf(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                    ? kind.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
