using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;

namespace Partner.Smoke;

/// <summary>Minimal tool used by the smoke run.</summary>
internal sealed class StubTool(string name, string answer) : ITool
{
    public ToolSpec Spec { get; } = new(name, $"stub tool returning {answer}", "{\"type\":\"object\"}");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult(true, answer));
}

/// <summary>Hook that injects a prompt fragment, proving the injection channel works end to end.</summary>
internal sealed class InjectingHook : AgentHookBase
{
    public override AgentPhase Phase => AgentPhase.PreExecute;

    public override string Name => "smoke.inject";

    public override ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        ctx.Inject("brief", 10);
        return ValueTask.FromResult(HookOutcome.Continue);
    }
}

/// <summary>
/// Lets a scripted model that drives the desktop tools as the main agent do the same work as the desktop
/// sub-agent, through the real delegation path.
/// <para>
/// The main agent's side is fixed: a user message becomes a delegation of that text, a delegation result becomes a
/// short acknowledgement, and the sub-agent's report coming back becomes another. The sub-agent's side is the
/// wrapped model, with one adjustment — when it would end its turn with text, it reports success instead, which is
/// what a job needs to end. Nothing in between is changed, so the checks still exercise governance, approval,
/// leases and the tools exactly as they did.
/// </para>
/// </summary>
internal sealed class DesktopAgentShim(ILlmClient inner) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var isSubAgent = (request.Messages.FirstOrDefault()?.Content ?? string.Empty).Contains("## 汇报方式", StringComparison.Ordinal);
        var last = request.Messages[^1];

        if (!isSubAgent)
        {
            if (last.Role == "tool")
            {
                return new LlmResponse("交给桌面操控了。", [], "stop");
            }

            var text = last.Content ?? string.Empty;
            return text.StartsWith("[子 agent", StringComparison.Ordinal)
                ? new LlmResponse("收到子 agent 的汇报。", [], "stop")
                : new LlmResponse(
                    null,
                    [new LlmToolCall("delegate", "agent_delegate", System.Text.Json.JsonSerializer.Serialize(new { scenario = "desktop", goal = text }))],
                    "tool_calls");
        }

        if (last is { Role: "tool", Name: "task_report" })
        {
            return new LlmResponse("已汇报。", [], "stop");
        }

        var response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);
        return response.ToolCalls.Count > 0
            ? response
            : new LlmResponse(
                null,
                [new LlmToolCall("report", "task_report", System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = "success",
                    summary = string.IsNullOrWhiteSpace(response.Text) ? "完成" : response.Text,
                    evidence = "脚本化模型的收尾",
                }))],
                "tool_calls");
    }
}
