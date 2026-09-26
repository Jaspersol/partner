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
