using Partner.Core.Agent.Hooks;

namespace Partner.Core.Agent.Web;

/// <summary>
/// Feeds what the person said into <see cref="WebHostLedger"/>.
/// <para>
/// A link somebody pastes is a link somebody meant: the model fetching it should not produce an approval banner,
/// because the person already asked for it. This hook is the only place that sees the raw input before the turn
/// runs, so it is where that gets recorded.
/// </para>
/// <para>
/// It reads and remembers; it never injects anything into the prompt, and it does not look at what the model
/// said (that text is the model's own, and letting it vouch for its own host would defeat the check).
/// </para>
/// </summary>
public sealed class WebHostLedgerHook(WebHostLedger ledger) : AgentHookBase
{
    public override AgentPhase Phase => AgentPhase.PostDispatch;

    public override string Name => "web-host-ledger";

    public override ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        // Only what the person, the scheduler or a sub-agent's report actually carried. A voice transcript has
        // no URL in it, and remembering nothing is the correct outcome there.
        ledger.Remember(ctx.InputPayload);
        return ValueTask.FromResult(HookOutcome.Continue);
    }
}
